using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PiSignage.Control;

/// <summary>
/// Minimal mDNS browser for _pisign._tcp.local.
/// Best-effort: some networks/VPNs block multicast — the UI always keeps
/// manual address entry as the reliable path.
/// </summary>
public static class MdnsDiscovery
{
    private const string ServiceName = "_pisign._tcp.local";

    public static async Task<List<DiscoveredDevice>> ScanAsync(TimeSpan timeout)
    {
        var found = new Dictionary<string, DiscoveredDevice>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            var group = IPAddress.Parse("224.0.0.251");
            udp.JoinMulticastGroup(group);

            byte[] query = BuildPtrQuery(ServiceName);
            await udp.SendAsync(query, query.Length, new IPEndPoint(group, 5353));

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var receiveTask = udp.ReceiveAsync();
                var done = await Task.WhenAny(receiveTask, Task.Delay(remaining));
                if (done != receiveTask) break;

                try
                {
                    var result = receiveTask.Result;
                    ParseResponse(result.Buffer, result.RemoteEndPoint, found);
                }
                catch { /* malformed packet from something else on the network */ }
            }
        }
        catch
        {
            // multicast unavailable (VPN, VM NAT, firewall) — return what we have
        }
        return found.Values.ToList();
    }

    private static byte[] BuildPtrQuery(string service)
    {
        var ms = new List<byte>
        {
            0, 0,       // transaction id
            0, 0,       // flags: standard query
            0, 1,       // 1 question
            0, 0, 0, 0, 0, 0
        };
        foreach (var label in service.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.Add((byte)bytes.Length);
            ms.AddRange(bytes);
        }
        ms.Add(0);           // name terminator
        ms.AddRange(new byte[] { 0, 12 });   // QTYPE = PTR
        ms.AddRange(new byte[] { 0, 1 });    // QCLASS = IN
        return ms.ToArray();
    }

    private static void ParseResponse(byte[] pkt, IPEndPoint from, Dictionary<string, DiscoveredDevice> found)
    {
        if (pkt.Length < 12) return;
        int qd = (pkt[4] << 8) | pkt[5];
        int an = (pkt[6] << 8) | pkt[7];
        int ns = (pkt[8] << 8) | pkt[9];
        int ar = (pkt[10] << 8) | pkt[11];

        int pos = 12;
        for (int i = 0; i < qd; i++)          // skip questions
        {
            ReadName(pkt, ref pos);
            pos += 4;
        }

        string? instance = null;
        int srvPort = 0;
        string? aAddress = null;

        int total = an + ns + ar;
        for (int i = 0; i < total && pos < pkt.Length; i++)
        {
            string name = ReadName(pkt, ref pos);
            if (pos + 10 > pkt.Length) return;
            int type = (pkt[pos] << 8) | pkt[pos + 1];
            pos += 8;                          // type, class, ttl
            int rdlen = (pkt[pos] << 8) | pkt[pos + 1];
            pos += 2;
            int rdStart = pos;
            if (rdStart + rdlen > pkt.Length) return;

            if (type == 12 && name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                int p = rdStart;
                string inst = ReadName(pkt, ref p);
                instance = FirstLabel(inst);
            }
            else if (type == 33 && name.EndsWith(ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                // SRV: priority(2) weight(2) port(2) target
                if (rdlen >= 6)
                    srvPort = (pkt[rdStart + 4] << 8) | pkt[rdStart + 5];
                if (instance == null) instance = FirstLabel(name);
            }
            else if (type == 1 && rdlen == 4)  // A record
            {
                aAddress = $"{pkt[rdStart]}.{pkt[rdStart + 1]}.{pkt[rdStart + 2]}.{pkt[rdStart + 3]}";
            }
            pos = rdStart + rdlen;
        }

        if (instance != null)
        {
            var dev = new DiscoveredDevice
            {
                Name = instance,
                Address = aAddress ?? from.Address.ToString(),
                Port = srvPort > 0 ? srvPort : 8080,
            };
            found[$"{dev.Address}:{dev.Port}"] = dev;
        }
    }

    private static string FirstLabel(string fqdn)
    {
        int dot = fqdn.IndexOf('.');
        return dot > 0 ? fqdn[..dot] : fqdn;
    }

    /// <summary>Reads a DNS name honoring compression pointers.</summary>
    private static string ReadName(byte[] pkt, ref int pos)
    {
        var sb = new StringBuilder();
        int jumps = 0;
        int cursor = pos;
        bool jumped = false;

        while (cursor < pkt.Length)
        {
            byte len = pkt[cursor];
            if (len == 0)
            {
                cursor++;
                break;
            }
            if ((len & 0xC0) == 0xC0)          // compression pointer
            {
                if (cursor + 1 >= pkt.Length || ++jumps > 10) break;
                int ptr = ((len & 0x3F) << 8) | pkt[cursor + 1];
                if (!jumped) pos = cursor + 2;
                jumped = true;
                cursor = ptr;
                continue;
            }
            cursor++;
            if (cursor + len > pkt.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(pkt, cursor, len));
            cursor += len;
        }
        if (!jumped) pos = cursor;
        return sb.ToString();
    }
}
