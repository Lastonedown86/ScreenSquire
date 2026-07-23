# USB WiFi Provisioning Wizard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A customer plugs a pre-imaged Pi 4 into their Windows PC via USB-C, opens the app's Add-a-Pi wizard, types their WiFi SSID + password, clicks Connect to WiFi, and the wizard confirms the Pi joined and shows its new LAN IP.

**Architecture:** The pre-image puts the Pi's USB-C port in NCM ethernet-gadget mode with a fixed `usb0` IP (10.55.0.1), so the agent is reachable over the cable at `http://10.55.0.1:8080`. The agent gains `/api/wifi` (set creds via `nmcli`) and `/api/wifi/status` (confirm). A testable `WifiProvisioner` in `signage-core` wraps those calls; a `WifiSetupWindow` drives the customer through detect → enter → connect → confirm.

**Tech Stack:** Python 3.13 / FastAPI (agent, `nmcli` via async subprocess), C# / .NET 8 (`signage-core` + WPF), pytest (agent), xUnit (core), bash + systemd + NetworkManager (pre-image, `pi-setup/`).

## Global Constraints

- Agent WiFi endpoints use **stdlib only** (`asyncio` subprocess) — no new packages in `agent/requirements.txt`.
- **Never log the WiFi password**; never include it in any response/error string.
- USB IP is fixed: **Pi `usb0` = `10.55.0.1/24`**, agent reachable at `http://10.55.0.1:8080`. PC gets `10.55.0.10-20` via dnsmasq.
- WiFi backend is **`nmcli`** (Bookworm/NetworkManager); the agent calls it via `sudo` (sudoers drop-in in the pre-image).
- Wire contracts (exact keys):
  - `POST /api/wifi` req `{ "ssid": str, "password": str }` → resp `{ "ok": bool, "connected": bool, "ip": str|null, "error": str|null }`
  - `GET /api/wifi/status` → `{ "connected": bool, "ssid": str|null, "ip": str|null }`
- `signage-core` stays net8.0, no WPF. WPF namespace `PiSignage.Control`, core namespace `PiSignage.Signage`.
- The `pi-setup` gadget provisioning is **hardware-only verifiable** (real Pi 4); its tasks ship with a syntax check + manual E2E steps, no CI test.
- Branch: `feat/usb-wifi-provisioning`.

---

## File Structure

**Agent (Python):**
- Modify `agent/main.py` — `_run` subprocess helper, `WifiRequest` model, `/api/wifi`, `/api/wifi/status`, `_wlan_ip`, `_wlan_ssid`.
- Test `agent/tests/test_wifi.py`.

**Library (`signage-core/`, net8.0):**
- `WifiProvisioner.cs` — `DetectAsync`, `ConnectAsync`, `GetStatusAsync` + `WifiResult`/`WifiStatus` DTOs.
- Test `signage-core.Tests/WifiProvisionerTests.cs` (stub `HttpMessageHandler`).

**WPF:**
- Create `windows-app/WifiSetupWindow.xaml` + `.cs`.
- Modify `windows-app/MainWindow.xaml` + `.cs` — "Add a Pi" button.

**Pre-image (`pi-setup/`):**
- Create `pi-setup/usb-gadget-ncm.sh`, `pi-setup/provision-usb.sh`, and config snippets; document in `README`.

---

## Task 1: Agent WiFi endpoints

**Files:**
- Modify: `agent/main.py`
- Test: `agent/tests/test_wifi.py`

**Interfaces:**
- Produces (HTTP): `POST /api/wifi {ssid,password}` → `{ok,connected,ip,error}`; `GET /api/wifi/status` → `{connected,ssid,ip}`.
- Produces (module, for tests): async `main._run(cmd: list[str], timeout: float) -> tuple[int,str,str]` (monkeypatched in tests).

- [ ] **Step 1: Write the failing tests**

Create `agent/tests/test_wifi.py`:
```python
import main
from fastapi.testclient import TestClient

client = TestClient(main.app)

def _fake_run(script):
    """Return an async _run that dispatches on the command verb."""
    async def _run(cmd, timeout=30.0):
        return script(cmd)
    return _run

def test_wifi_connect_success(monkeypatch):
    def script(cmd):
        if cmd[:4] == ["sudo", "nmcli", "dev", "wifi"]:
            return (0, "Device 'wlan0' successfully activated", "")
        if cmd[:2] == ["nmcli", "-t"] and "IP4.ADDRESS" in cmd:
            return (0, "IP4.ADDRESS[1]:192.168.1.42/24\n", "")
        return (0, "", "")
    monkeypatch.setattr(main, "_run", _fake_run(script))
    r = client.post("/api/wifi", json={"ssid": "Shop", "password": "secret123"})
    body = r.json()
    assert body["ok"] is True and body["connected"] is True
    assert body["ip"] == "192.168.1.42"
    assert body["error"] is None

def test_wifi_connect_failure_hides_password(monkeypatch):
    def script(cmd):
        return (4, "", "Error: Secrets were required, but not provided.")
    monkeypatch.setattr(main, "_run", _fake_run(script))
    r = client.post("/api/wifi", json={"ssid": "Shop", "password": "secret123"})
    body = r.json()
    assert body["ok"] is False and body["connected"] is False
    assert "Secrets were required" in body["error"]
    assert "secret123" not in body["error"]   # password never leaks

def test_wifi_status_parses(monkeypatch):
    def script(cmd):
        if "GENERAL.CONNECTION" in cmd:
            return (0, "GENERAL.CONNECTION:ShopWiFi\n", "")
        if "IP4.ADDRESS" in cmd:
            return (0, "IP4.ADDRESS[1]:192.168.1.42/24\n", "")
        return (0, "", "")
    monkeypatch.setattr(main, "_run", _fake_run(script))
    s = client.get("/api/wifi/status").json()
    assert s["connected"] is True and s["ssid"] == "ShopWiFi" and s["ip"] == "192.168.1.42"
```

- [ ] **Step 2: Run to verify failure**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_wifi.py -v
```
Expected: FAIL (endpoints/`_run` not defined).

- [ ] **Step 3: Implement in `main.py`**

Ensure `import asyncio` is present (it is). After the dashboard endpoints, add:
```python
# ---- WiFi provisioning (USB setup) ----
class WifiRequest(BaseModel):
    ssid: str
    password: str


async def _run(cmd: list[str], timeout: float = 30.0) -> tuple[int, str, str]:
    """Run a command with a timeout. Returns (returncode, stdout, stderr)."""
    proc = await asyncio.create_subprocess_exec(
        *cmd, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    try:
        out, err = await asyncio.wait_for(proc.communicate(), timeout=timeout)
    except asyncio.TimeoutError:
        proc.kill()
        return 124, "", "timed out"
    return proc.returncode or 0, out.decode(errors="replace"), err.decode(errors="replace")


async def _wlan_ip() -> Optional[str]:
    _, out, _ = await _run(["nmcli", "-t", "-f", "IP4.ADDRESS", "dev", "show", "wlan0"], timeout=5)
    for line in out.splitlines():
        if ":" in line:
            val = line.split(":", 1)[1].strip()
            if val:
                return val.split("/")[0]
    return None


async def _wlan_ssid() -> Optional[str]:
    _, out, _ = await _run(["nmcli", "-t", "-f", "GENERAL.CONNECTION", "dev", "show", "wlan0"], timeout=5)
    for line in out.splitlines():
        if line.startswith("GENERAL.CONNECTION:"):
            return (line.split(":", 1)[1].strip() or None)
    return None


@app.post("/api/wifi")
async def set_wifi(req: WifiRequest):
    rc, out, err = await _run(
        ["sudo", "nmcli", "dev", "wifi", "connect", req.ssid,
         "password", req.password, "ifname", "wlan0"],
        timeout=30.0,
    )
    if rc != 0:
        # never echo the password back
        return {"ok": False, "connected": False, "ip": None,
                "error": (err.strip() or out.strip() or "connect failed")}
    ip = await _wlan_ip()
    return {"ok": True, "connected": ip is not None, "ip": ip, "error": None}


@app.get("/api/wifi/status")
async def wifi_status():
    ssid = await _wlan_ssid()
    ip = await _wlan_ip()
    return {"connected": ip is not None, "ssid": ssid, "ip": ip}
```

- [ ] **Step 4: Run to verify pass**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/test_wifi.py -v
```
Expected: 3 passed.

- [ ] **Step 5: Full agent suite (no regressions)**

```bash
cd agent && ./.venv/Scripts/python.exe -m pytest tests/ -q
```
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add agent/main.py agent/tests/test_wifi.py
git commit -m "feat(agent): /api/wifi + /api/wifi/status (nmcli), password never logged"
```

---

## Task 2: signage-core WifiProvisioner

**Files:**
- Create: `signage-core/WifiProvisioner.cs`
- Test: `signage-core.Tests/WifiProvisionerTests.cs`

**Interfaces:**
- Produces: `WifiProvisioner(HttpClient http)` with `Task<bool> DetectAsync(string baseUrl)`, `Task<WifiResult> ConnectAsync(string baseUrl, string ssid, string password)`, `Task<WifiStatus> GetStatusAsync(string baseUrl)`; `WifiResult{ bool ok, bool connected, string? ip, string? error }`, `WifiStatus{ bool connected, string? ssid, string? ip }`.

- [ ] **Step 1: Write the failing tests (stub handler, no live server)**

Create `signage-core.Tests/WifiProvisionerTests.cs`:
```csharp
using System.Net;
using System.Net.Http;
using System.Text;
using PiSignage.Signage;
using Xunit;

public class WifiProvisionerTests
{
    sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _resp;
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> resp) => _resp = resp;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            var (code, json) = _resp(req);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ConnectPostsCredentialsAndParsesResult()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK,
            "{\"ok\":true,\"connected\":true,\"ip\":\"192.168.1.42\",\"error\":null}"));
        var p = new WifiProvisioner(new HttpClient(stub));
        var r = await p.ConnectAsync("http://10.55.0.1:8080", "Shop", "secret123");
        Assert.True(r.Ok); Assert.True(r.Connected); Assert.Equal("192.168.1.42", r.Ip);
        Assert.Contains("\"ssid\":\"Shop\"", stub.LastBody);
        Assert.Contains("\"password\":\"secret123\"", stub.LastBody);
        Assert.EndsWith("/api/wifi", stub.Last!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task StatusParses()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK,
            "{\"connected\":true,\"ssid\":\"ShopWiFi\",\"ip\":\"192.168.1.42\"}"));
        var p = new WifiProvisioner(new HttpClient(stub));
        var s = await p.GetStatusAsync("http://10.55.0.1:8080");
        Assert.True(s.Connected); Assert.Equal("ShopWiFi", s.Ssid); Assert.Equal("192.168.1.42", s.Ip);
    }

    [Fact]
    public async Task DetectTrueOn200_FalseOnError()
    {
        var ok = new WifiProvisioner(new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
        Assert.True(await ok.DetectAsync("http://10.55.0.1:8080"));
        var bad = new WifiProvisioner(new HttpClient(new StubHandler(_ => (HttpStatusCode.ServiceUnavailable, ""))));
        Assert.False(await bad.DetectAsync("http://10.55.0.1:8080"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test signage-core.Tests --filter WifiProvisionerTests
```
Expected: FAIL (types not found).

- [ ] **Step 3: Implement `WifiProvisioner.cs`**

```csharp
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PiSignage.Signage;

public sealed class WifiResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class WifiStatus
{
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("ssid")] public string? Ssid { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
}

public sealed class WifiProvisioner(HttpClient http)
{
    public async Task<bool> DetectAsync(string baseUrl)
    {
        try
        {
            var r = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/status");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<WifiResult> ConnectAsync(string baseUrl, string ssid, string password)
    {
        var resp = await http.PostAsJsonAsync(baseUrl.TrimEnd('/') + "/api/wifi", new { ssid, password });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WifiResult>() ?? new WifiResult();
    }

    public async Task<WifiStatus> GetStatusAsync(string baseUrl)
        => await http.GetFromJsonAsync<WifiStatus>(baseUrl.TrimEnd('/') + "/api/wifi/status") ?? new WifiStatus();
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test signage-core.Tests --filter WifiProvisionerTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add signage-core/WifiProvisioner.cs signage-core.Tests/WifiProvisionerTests.cs
git commit -m "feat(core): WifiProvisioner (detect/connect/status) + tests"
```

---

## Task 3: WPF Add-a-Pi wizard

**Files:**
- Create: `windows-app/WifiSetupWindow.xaml` + `.cs`
- Modify: `windows-app/MainWindow.xaml` (button), `windows-app/MainWindow.xaml.cs` (handler)

**Interfaces:**
- Consumes: `WifiProvisioner`, `WifiResult`, `WifiStatus` from `signage-core`; `CaptureExclusion.ExcludeFromCapture` (optional, harmless).

- [ ] **Step 1: Create `WifiSetupWindow.xaml`**

```xml
<Window x:Class="PiSignage.Control.WifiSetupWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Add a Pi — Connect to WiFi" Height="380" Width="460">
    <StackPanel Margin="16">
        <TextBlock Text="1. Plug the Pi into this PC with a USB-C data cable."
                   TextWrapping="Wrap" FontWeight="SemiBold"/>
        <StackPanel Orientation="Horizontal" Margin="0,6,0,12">
            <ProgressBar x:Name="Detecting" Width="120" Height="6" IsIndeterminate="True"/>
            <TextBlock x:Name="DetectStatus" Text="Looking for the Pi over USB…"
                       Margin="10,0,0,0" VerticalAlignment="Center" Foreground="#666"/>
        </StackPanel>

        <Grid x:Name="Form" IsEnabled="False">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <TextBlock Grid.Row="0" Text="2. Enter your WiFi details" FontWeight="SemiBold" Margin="0,0,0,6"/>
            <DockPanel Grid.Row="1" Margin="0,0,0,6">
                <TextBlock Text="Network (SSID)" Width="120" VerticalAlignment="Center"/>
                <TextBox x:Name="TxtSsid" TextChanged="Field_Changed"/>
            </DockPanel>
            <DockPanel Grid.Row="2" Margin="0,0,0,10">
                <TextBlock Text="Password" Width="120" VerticalAlignment="Center"/>
                <PasswordBox x:Name="TxtPass" PasswordChanged="Field_Changed"/>
            </DockPanel>
            <Button Grid.Row="3" x:Name="BtnConnect" Content="Connect to WiFi"
                    Click="Connect_Click" IsEnabled="False" Padding="12,5" HorizontalAlignment="Left"/>
        </Grid>

        <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
            <ProgressBar x:Name="Working" Width="120" Height="6" IsIndeterminate="True" Visibility="Collapsed"/>
            <TextBlock x:Name="Result" Margin="10,0,0,0" VerticalAlignment="Center" TextWrapping="Wrap"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Create `WifiSetupWindow.xaml.cs`**

```csharp
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using PiSignage.Signage;

namespace PiSignage.Control;

public partial class WifiSetupWindow : Window
{
    const string PiUsbBase = "http://10.55.0.1:8080";   // fixed USB-gadget address
    readonly WifiProvisioner _wifi = new(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
    bool _detected;

    public WifiSetupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await DetectLoop();
    }

    async Task DetectLoop()
    {
        for (int i = 0; i < 60 && !_detected; i++)   // ~60s of polling
        {
            if (await _wifi.DetectAsync(PiUsbBase))
            {
                _detected = true;
                Detecting.Visibility = Visibility.Collapsed;
                DetectStatus.Text = "Pi found over USB.";
                Form.IsEnabled = true;
                return;
            }
            await Task.Delay(1000);
        }
        DetectStatus.Text = "No Pi found. Check the cable is a DATA USB-C cable and wait for the Pi to boot.";
    }

    void Field_Changed(object s, RoutedEventArgs e)
        => BtnConnect.IsEnabled = TxtSsid.Text.Trim().Length > 0 && TxtPass.Password.Length > 0;

    async void Connect_Click(object s, RoutedEventArgs e)
    {
        Form.IsEnabled = false;
        Working.Visibility = Visibility.Visible;
        Result.Foreground = Brushes.Gray;
        Result.Text = $"Connecting the Pi to {TxtSsid.Text.Trim()}…";
        try
        {
            var r = await _wifi.ConnectAsync(PiUsbBase, TxtSsid.Text.Trim(), TxtPass.Password);
            bool ok = r.Ok && r.Connected;
            if (!ok)   // one confirming re-check in case connect returned before DHCP settled
            {
                await Task.Delay(3000);
                var st = await _wifi.GetStatusAsync(PiUsbBase);
                ok = st.Connected;
                if (ok) r = new WifiResult { Ok = true, Connected = true, Ip = st.Ip };
            }
            if (ok)
            {
                Result.Foreground = Brushes.Green;
                Result.Text = $"Connected — this Pi is on {TxtSsid.Text.Trim()} at {r.Ip}. You can unplug the USB cable.";
            }
            else
            {
                Result.Foreground = Brushes.Firebrick;
                Result.Text = "Couldn't connect: " + (r.Error ?? "check the network name and password") + "  — Try again.";
                Form.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Result.Foreground = Brushes.Firebrick;
            Result.Text = "Setup failed: " + ex.Message;
            Form.IsEnabled = true;
        }
        finally { Working.Visibility = Visibility.Collapsed; }
    }
}
```

- [ ] **Step 3: Add the launch button to `MainWindow.xaml`**

In the connect bar `DockPanel` (near the "Tournament Signage" button), add:
```xml
<Button Content="Add a Pi" DockPanel.Dock="Right" Margin="8,0,0,0" Click="AddPi_Click"/>
```

- [ ] **Step 4: Handle it in `MainWindow.xaml.cs`**

Add to the `MainWindow` class:
```csharp
void AddPi_Click(object sender, System.Windows.RoutedEventArgs e)
{
    var w = new WifiSetupWindow { Owner = this };
    w.Closed += (_, _) => Activate();   // keep main window in front on close
    w.Show();
}
```

- [ ] **Step 5: Build**

```bash
taskkill //F //IM PiSignageControl.exe 2>/dev/null
dotnet build PiSignage.slnx -v q --nologo
```
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add windows-app/WifiSetupWindow.xaml windows-app/WifiSetupWindow.xaml.cs windows-app/MainWindow.xaml windows-app/MainWindow.xaml.cs
git commit -m "feat(wpf): Add-a-Pi WiFi setup wizard (detect USB, enter creds, connect, confirm)"
```

---

## Task 4: Pre-image USB-gadget provisioning (hardware-only)

**Files:**
- Create: `pi-setup/usb-gadget-ncm.sh`, `pi-setup/provision-usb.sh`
- Modify: `README.md` (pre-image instructions)

> Verification is **manual on a real Pi 4** — there is no CI test. Steps include a `bash -n` syntax check as the automated gate.

- [ ] **Step 1: Create `pi-setup/usb-gadget-ncm.sh`**

```bash
#!/usr/bin/env bash
# Bring up a USB NCM ethernet gadget on usb0 with a fixed IP.
# Runs at boot (see provision-usb.sh). Requires dtoverlay=dwc2 in config.txt.
set -e
modprobe libcomposite
G=/sys/kernel/config/usb_gadget/pisignage
if [ ! -d "$G" ]; then
  mkdir -p "$G"; cd "$G"
  echo 0x1d6b > idVendor        # Linux Foundation
  echo 0x0104 > idProduct       # Multifunction Composite Gadget
  echo 0x0100 > bcdDevice; echo 0x0200 > bcdUSB
  mkdir -p strings/0x409
  echo "PiSignage" > strings/0x409/manufacturer
  echo "PiSignage Setup" > strings/0x409/product
  echo "0001" > strings/0x409/serialnumber
  mkdir -p configs/c.1/strings/0x409
  echo "NCM" > configs/c.1/strings/0x409/configuration
  mkdir -p functions/ncm.usb0
  ln -sf functions/ncm.usb0 configs/c.1/
  ls /sys/class/udc > UDC
fi
ip addr add 10.55.0.1/24 dev usb0 2>/dev/null || true
ip link set usb0 up
```

- [ ] **Step 2: Create `pi-setup/provision-usb.sh`**

```bash
#!/usr/bin/env bash
# Run ONCE on the Pi (as part of pre-imaging) to install USB WiFi-setup provisioning.
set -euo pipefail

# 1. USB gadget requires dwc2
grep -q '^dtoverlay=dwc2' /boot/firmware/config.txt 2>/dev/null || \
  echo 'dtoverlay=dwc2' | sudo tee -a /boot/firmware/config.txt >/dev/null

# 2. Install the gadget bring-up script
sudo install -m 0755 "$(dirname "$0")/usb-gadget-ncm.sh" /usr/local/sbin/usb-gadget-ncm.sh

# 3. systemd unit to run it at boot
sudo tee /etc/systemd/system/usb-gadget-ncm.service >/dev/null <<'EOF'
[Unit]
Description=PiSignage USB NCM gadget
After=sys-kernel-config.mount
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/usb-gadget-ncm.sh
RemainAfterExit=yes
[Install]
WantedBy=multi-user.target
EOF

# 4. Let NetworkManager ignore usb0 (we own it)
sudo tee /etc/NetworkManager/conf.d/10-pisignage-usb.conf >/dev/null <<'EOF'
[keyfile]
unmanaged-devices=interface-name:usb0
EOF

# 5. DHCP for the PC on usb0
sudo apt-get install -y dnsmasq
sudo tee /etc/dnsmasq.d/pisignage-usb.conf >/dev/null <<'EOF'
interface=usb0
bind-interfaces
dhcp-range=10.55.0.10,10.55.0.20,255.255.255.0,1h
EOF

# 6. Allow the agent (user pi) to drive nmcli without a password
echo 'pi ALL=(root) NOPASSWD: /usr/bin/nmcli' | sudo tee /etc/sudoers.d/pisignage-nmcli >/dev/null
sudo chmod 0440 /etc/sudoers.d/pisignage-nmcli

sudo systemctl enable usb-gadget-ncm.service
echo "==> USB provisioning installed. Reboot, then this Pi presents a USB setup link at 10.55.0.1:8080."
```

- [ ] **Step 3: Syntax-check both scripts (automated gate)**

```bash
bash -n pi-setup/usb-gadget-ncm.sh && bash -n pi-setup/provision-usb.sh && echo "syntax OK"
```
Expected: `syntax OK`.

- [ ] **Step 4: Document in `README.md`**

Add a "Pre-imaging for USB WiFi setup" section:
```markdown
## Pre-imaging a unit for USB WiFi setup (builder)

After running `install.sh` on a fresh image, also run:

    cd ~/pi-signage/pi-setup && bash provision-usb.sh && sudo reboot

The unit now presents a USB network link at `10.55.0.1:8080` when plugged into a
PC. The customer uses the app's **Add a Pi** wizard: plug in the Pi with a USB-C
**data** cable, enter WiFi SSID + password, click **Connect to WiFi**.
```

- [ ] **Step 5: Manual E2E (real Pi 4 — record result, do not block the commit on hardware you lack)**

1. Pre-image a Pi 4, run `provision-usb.sh`, reboot.
2. Plug USB-C (data) into a Win11 PC → confirm a USB network adapter appears and `http://10.55.0.1:8080/api/status` responds.
3. App → **Add a Pi** → enter real WiFi → **Connect to WiFi** → confirm success + LAN IP shown.
4. Unplug USB → reach the Pi over LAN via Scan/Connect.

- [ ] **Step 6: Commit**

```bash
git add pi-setup/usb-gadget-ncm.sh pi-setup/provision-usb.sh README.md
git commit -m "feat(pi-setup): USB NCM gadget provisioning for WiFi setup wizard"
```

---

## Self-Review

- **Spec coverage:** USB NCM gadget + fixed IP ✓ (Task 4); agent `/api/wifi` + `/api/wifi/status` via nmcli, password never logged ✓ (Task 1); testable `WifiProvisioner` ✓ (Task 2); wizard detect→enter→connect→confirm with a **Connect to WiFi** button and manual SSID+password ✓ (Task 3); nmcli sudoers + NM-ignores-usb0 + dnsmasq ✓ (Task 4); security (local-only password) ✓ (Tasks 1, spec).
- **Deferred (spec non-goals):** nearby-SSID scan, saved-device naming, serial/RNDIS fallback.
- **Placeholder scan:** none — full code in every code step.
- **Type/contract consistency:** payload keys `ssid/password` and response keys `ok/connected/ip/error` / `connected/ssid/ip` match across agent (Task 1), `WifiProvisioner` DTOs via `[JsonPropertyName]` (Task 2), and the wizard's use (Task 3). Fixed USB base `http://10.55.0.1:8080` matches the pre-image `usb0` IP (Task 4).
- **Hardware caveat:** Task 4 and the wizard's real USB link are verifiable only on a Pi 4; automated gate is `bash -n` + the mocked agent tests + stubbed core tests. Everything else (agent logic, provisioner, wizard build) is CI-testable on this machine.
