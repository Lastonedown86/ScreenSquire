using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using PiSignage.Signage;

namespace PiSignage.Control;

// Small thumbnails for image media, fetched from the agent's /media static mount
// and cached on disk so lists stay instant on later launches.
public static class ThumbnailCache
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiSignage", "thumbs");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    const int DecodeHeight = 64;   // shown at ~32-48px; 64 keeps it crisp on high-DPI

    public static async Task<BitmapImage?> GetAsync(string baseUrl, string name, long bytes)
    {
        try
        {
            var key = Sanitize($"{new Uri(baseUrl).Host}_{name}_{bytes}") + ".png";
            var path = Path.Combine(Dir, key);
            if (File.Exists(path))
                return Decode(await File.ReadAllBytesAsync(path));

            var raw = await Http.GetByteArrayAsync($"{baseUrl}/media/{Uri.EscapeDataString(name)}");
            var img = Decode(raw);
            if (img == null) return null;

            // persist the downsized version, not the original photo
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            Directory.CreateDirectory(Dir);
            using (var fs = File.Create(path)) enc.Save(fs);
            return img;
        }
        catch { return null; }   // no thumbnail is fine — the glyph shows instead
    }

    /// <summary>Thumbnail from an arbitrary https URL (YouTube video thumbs).</summary>
    public static async Task<BitmapImage?> GetUrlAsync(string url)
    {
        try
        {
            var key = "url_" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(url)))[..24] + ".png";
            var path = Path.Combine(Dir, key);
            if (File.Exists(path))
                return Decode(await File.ReadAllBytesAsync(path));

            var img = Decode(await Http.GetByteArrayAsync(url));
            if (img == null) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            Directory.CreateDirectory(Dir);
            using (var fs = File.Create(path)) enc.Save(fs);
            return img;
        }
        catch { return null; }   // no thumbnail is fine — the glyph shows instead
    }

    public static void ClearDevice(SavedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Directory.Exists(Dir)) return;

        var hosts = new[] { device.Ip, device.Hostname, device.Name }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Sanitize)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            foreach (var path in Directory.EnumerateFiles(Dir, host + "_*.png"))
                File.Delete(path);
        }
    }

    static BitmapImage? Decode(byte[] data)
    {
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(data);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelHeight = DecodeHeight;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();   // usable from any thread
            return img;
        }
        catch { return null; }
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
