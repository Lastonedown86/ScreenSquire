# Secure Controller Onboarding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the first production Pi accept state-changing commands only from its paired store laptop, with USB/PIN onboarding, safe recovery, temporary builder testing, and a destructive Prepare for delivery handoff.

**Architecture:** Each Display Pi has a persistent device ID, an 8-digit Recovery PIN verifier, and at most one controller record. USB pairing replaces the controller record and returns a unique 256-bit secret; subsequent Control requests use HMAC-SHA256 plus a monotonically increasing counter, avoiding dependence on the Pi clock before Wi-Fi/NTP. FastAPI routes remain adapters over focused trust, authentication, reset, and persistence modules; the Windows app keeps secrets in a DPAPI-protected credential vault.

**Tech Stack:** Python 3/FastAPI, Python standard-library cryptography primitives, .NET 8/WPF, `System.Security.Cryptography.ProtectedData`, xUnit, pytest, Raspberry Pi OS Bookworm/Trixie, NetworkManager, USB NCM.

## Global Constraints

- Exactly one Controller laptop is trusted by each Display Pi.
- Store staff normally use one shared Windows login.
- Daily use is passwordless after pairing.
- Pairing and Ownership recovery require both USB source address `10.55.0.0/24` and the Pi's unique 8-digit Recovery PIN.
- Recovery PIN verification uses PBKDF2-HMAC-SHA256 with 200,000 iterations and constant-time comparison.
- Five consecutive bad PIN attempts block pairing for 60 seconds; successful pairing resets the guard.
- Each Pi/controller pairing uses a unique random 32-byte secret.
- Every state-changing Wi-Fi Control request is HMAC-signed and replay-resistant.
- HMAC counters are persisted; a counter is valid only when it is strictly greater than the Pi's last accepted counter.
- Display pages, media reads, status, discovery, and dashboard reads stay unsigned and unencrypted.
- The Wi-Fi password travels only over USB in a signed request.
- Builder setup creates temporary ownership; Prepare for delivery removes it.
- Prepare for delivery is USB-only, signed, and requires an explicit destructive confirmation in the Windows app.
- Prepare for delivery erases media, playlist, dashboard, timer, override, custom name, builder Wi-Fi profiles, local thumbnails, saved-device entry, and builder credential.
- Prepare for delivery preserves agent software, USB provisioning, device ID, and Recovery PIN verifier.
- Remote support uses attended Windows Quick Assist; no custom remote infrastructure or unattended builder credential is added.
- There are no deployed legacy units; no unauthenticated production compatibility mode is permitted.
- No new cloud dependency, account system, TLS certificate infrastructure, or multi-controller support.

---

## File Structure

### New Python modules

- `agent/trust.py` — durable device identity, Recovery PIN verifier, controller secret, accepted counter, PIN attempt guard, and CLI initialization.
- `agent/control_auth.py` — canonical HMAC message construction, request verification, entity-hash verification, and FastAPI dependencies.
- `agent/delivery_reset.py` — customer-data reset and NetworkManager Wi-Fi profile removal.
- `agent/tests/test_trust.py` — trust-store, pairing, counter, replacement, and throttling tests.
- `agent/tests/test_control_auth.py` — canonical signing, authentication, replay, and entity-integrity tests.
- `agent/tests/test_delivery_reset.py` — reset preservation/deletion and USB-only endpoint tests.

### New .NET modules

- `signage-core/ControllerCredentials.cs` — controller/device credential records, protected vault persistence, counter allocation, and the secret-protector seam.
- `signage-core/ControlRequestSigner.cs` — canonical message construction and HMAC headers.
- `signage-core/PairingClient.cs` — USB pair/status calls and wire contracts.
- `signage-core.Tests/ControllerCredentialsTests.cs`
- `signage-core.Tests/ControlRequestSignerTests.cs`
- `signage-core.Tests/PairingClientTests.cs`
- `windows-app/DpapiSecretProtector.cs` — Windows CurrentUser DPAPI adapter.
- `windows-app/DeliveryPreparation.cs` — USB delivery-reset orchestration and local cleanup.

### Modified files

- `agent/main.py` — compose trust/auth/reset modules; add pair/status/reset routes; protect all mutations; expose stable device ID.
- `agent/tests/conftest.py` — import the agent only after redirecting all runtime data to a temporary directory; signed-request fixture.
- Existing `agent/tests/test_*.py` — send authenticated mutations.
- `agent/requirements.txt` and `agent/requirements-dev.txt` — pin tested dependency versions.
- `pi-setup/provision-usb.sh` — initialize trust and print the Recovery PIN once.
- `windows-app/PiSignageControl.csproj` — add DPAPI package and embed every agent Python module.
- `signage-core/SavedDevice.cs` — persist `DeviceId` and `Port`.
- `signage-core/SettingsStore.cs` — persist tournament selections by device ID.
- `signage-core/PushClient.cs`, `WifiProvisioner.cs`, and `AgentUpdater.cs` — sign every mutation.
- `windows-app/ApiClient.cs` — use signed mutations and parse device identity.
- `windows-app/WifiSetupWindow.xaml` and `.xaml.cs` — Recovery PIN entry, pair-before-Wi-Fi, credential persistence.
- `windows-app/MainWindow.xaml` and `.xaml.cs` — stable identity/port use, rename fix, Prepare for delivery entry point.
- `windows-app/SignageWindow.xaml.cs` — target credentials by stable device ID.
- `windows-app/ThumbnailCache.cs` — clear one device's cached thumbnails.
- `deploy-agent.ps1` — load the current user's protected vault and sign update requests.
- `README.md` — builder, store onboarding, recovery, delivery, and Quick Assist runbooks.
- `CONTEXT.md` — remains the source of domain terminology.

---

### Task 1: Make Agent Tests Hermetic and Pin the Tested Python Environment

**Files:**
- Modify: `agent/tests/conftest.py`
- Modify: `agent/tests/test_dashboard.py`
- Modify: `agent/requirements.txt`
- Modify: `agent/requirements-dev.txt`

**Interfaces:**
- Consumes: `SIGNAGE_DATA` environment selection in `agent/main.py`
- Produces: isolated `agent_module` and `client` pytest fixtures used by every later Python task

- [ ] **Step 1: Write the failing isolation test**

Add an isolation test that imports `main` with a temporary data directory:

```python
def test_agent_import_uses_test_data_dir(agent_module):
    repository_data = Path(__file__).resolve().parents[1] / "data"
    assert agent_module.DATA_DIR != repository_data
    assert agent_module.DASHBOARD_FILE.parent == agent_module.DATA_DIR
```

Move `import main` out of module scope and create session fixtures only after setting `SIGNAGE_DATA`:

```python
import importlib
import sys
from pathlib import Path
import pytest
from fastapi.testclient import TestClient

@pytest.fixture(scope="session")
def agent_module(tmp_path_factory):
    data = tmp_path_factory.mktemp("agent-data")
    patch = pytest.MonkeyPatch()
    patch.setenv("SIGNAGE_DATA", str(data))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    sys.modules.pop("main", None)
    module = importlib.import_module("main")
    yield module
    patch.undo()

@pytest.fixture
def client(agent_module):
    return TestClient(agent_module.app)
```

- [ ] **Step 2: Run the isolation test and verify the current suite fails**

Run: `agent\.venv\Scripts\python.exe -m pytest agent\tests -q`

Expected before the fix: at least one import/fixture failure, and `agent/data/dashboard.json` is still touched by dashboard tests.

- [ ] **Step 3: Convert tests to fixtures and pin dependencies**

Replace module-level `main` and `client` imports with fixture parameters. Ensure dashboard tests write only under the session temp directory.

Set exact tested versions:

```text
# requirements.txt
fastapi==0.140.0
uvicorn[standard]==0.51.0
python-multipart==0.0.32
zeroconf==0.150.0
```

```text
# requirements-dev.txt
pytest==9.1.1
httpx==0.28.1
```

- [ ] **Step 4: Verify hermetic behavior**

Record the timestamp and hash of `agent/data/dashboard.json`, run:

`agent\.venv\Scripts\python.exe -m pytest agent -q`

Expected: `36 passed` or greater; the ignored runtime dashboard file timestamp and hash are unchanged.

- [ ] **Step 5: Commit**

```powershell
git add agent/tests agent/requirements.txt agent/requirements-dev.txt
git commit -m "test(agent): isolate runtime data and pin dependencies"
```

---

### Task 2: Add Durable Device Trust and Recovery PIN Initialization

**Files:**
- Create: `agent/trust.py`
- Create: `agent/tests/test_trust.py`
- Modify: `pi-setup/provision-usb.sh`

**Interfaces:**
- Produces: `TrustStore(path: Path)`, `initialize() -> str`, `pair(pin, controller_id) -> PairResult`, `controller_secret(controller_id) -> bytes | None`, `accept_counter(controller_id, counter) -> bool`, `clear_controller()`, and `device_id`
- Produces: `python trust.py init --data-dir <path>` CLI that prints exactly `RECOVERY_PIN=<8 digits>` on first initialization

- [ ] **Step 1: Write failing trust-store tests**

```python
def test_initialize_returns_eight_digits_and_persists_device_id(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()
    assert pin.isdigit() and len(pin) == 8
    assert TrustStore(tmp_path / "trust.json").device_id == store.device_id

def test_pair_returns_unique_secret_and_replaces_previous_controller(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()
    first = store.pair(pin, "builder")
    second = store.pair(pin, "store")
    assert len(first.secret) == len(second.secret) == 32
    assert first.secret != second.secret
    assert store.controller_id == "store"
    assert store.controller_secret("builder") is None

def test_counter_must_increase_and_survives_reload(tmp_path):
    store = TrustStore(tmp_path / "trust.json")
    pin = store.initialize()
    store.pair(pin, "store")
    assert store.accept_counter("store", 4)
    reloaded = TrustStore(tmp_path / "trust.json")
    assert not reloaded.accept_counter("store", 4)
    assert reloaded.accept_counter("store", 5)
```

- [ ] **Step 2: Run tests to verify failure**

Run: `agent\.venv\Scripts\python.exe -m pytest agent\tests\test_trust.py -q`

Expected: FAIL with `ModuleNotFoundError: No module named 'trust'`.

- [ ] **Step 3: Implement the trust store**

Use this persisted shape:

```python
{
    "device_id": "uuid-hex",
    "pin_salt": "base64",
    "pin_hash": "base64",
    "controller_id": None,
    "controller_secret": None,
    "last_counter": 0,
}
```

Use:

```python
def _pin_hash(pin: str, salt: bytes) -> bytes:
    return hashlib.pbkdf2_hmac("sha256", pin.encode("ascii"), salt, 200_000)

def initialize(self) -> str:
    if self.path.exists():
        raise RuntimeError("Trust is already initialized")
    pin = f"{secrets.randbelow(100_000_000):08d}"
    salt = secrets.token_bytes(16)
    self._data = {
        "device_id": uuid.uuid4().hex,
        "pin_salt": _b64(salt),
        "pin_hash": _b64(_pin_hash(pin, salt)),
        "controller_id": None,
        "controller_secret": None,
        "last_counter": 0,
    }
    self._save()
    return pin
```

Write through a same-directory temporary file, `os.replace`, and `chmod(0o600)`. Compare PIN hashes with `hmac.compare_digest`.

- [ ] **Step 4: Add and test PIN throttling**

Implement an in-memory `PairingGuard`:

```python
class PairingGuard:
    def __init__(self):
        self.failures = 0
        self.blocked_until = 0.0

    def check(self, now: float) -> None:
        if now < self.blocked_until:
            raise PairingBlocked(round(self.blocked_until - now))

    def failed(self, now: float) -> None:
        self.failures += 1
        if self.failures >= 5:
            self.blocked_until = now + 60
            self.failures = 0

    def succeeded(self) -> None:
        self.failures = 0
        self.blocked_until = 0.0
```

Add a fake-clock test proving the sixth attempt is blocked and a valid PIN works after 60 seconds.

- [ ] **Step 5: Initialize trust during USB provisioning**

After installing the gadget script, run initialization as the target user only when `agent/data/trust.json` is absent:

```bash
TRUST_FILE="$HOME_DIR/pi-signage/agent/data/trust.json"
if [ ! -f "$TRUST_FILE" ]; then
  mkdir -p "$(dirname "$TRUST_FILE")"
  chown "$USER_NAME:$USER_NAME" "$(dirname "$TRUST_FILE")"
  PIN_LINE="$(sudo -u "$USER_NAME" python3 "$HOME_DIR/pi-signage/agent/trust.py" init \
    --data-dir "$HOME_DIR/pi-signage/agent/data")"
  echo "============================================================"
  echo "$PIN_LINE"
  echo "Print this 8-digit PIN on the bottom label before delivery."
  echo "It will not be displayed again."
  echo "============================================================"
fi
```

- [ ] **Step 6: Verify and commit**

Run:

```powershell
agent\.venv\Scripts\python.exe -m pytest agent\tests\test_trust.py -q
bash -n pi-setup/provision-usb.sh
```

Expected: all trust tests pass; shell syntax succeeds.

```powershell
git add agent/trust.py agent/tests/test_trust.py pi-setup/provision-usb.sh
git commit -m "feat(agent): add device trust and recovery PIN"
```

---

### Task 3: Add USB Pairing and Signed Control Verification

**Files:**
- Create: `agent/control_auth.py`
- Create: `agent/tests/test_control_auth.py`
- Modify: `agent/main.py`
- Modify: existing mutation tests under `agent/tests/`

**Interfaces:**
- Consumes: `TrustStore`
- Produces: `POST /api/pair`, `GET /api/pair/status`
- Produces headers: `X-PiSignage-Controller`, `X-PiSignage-Counter`, `X-PiSignage-Entity-SHA256`, `X-PiSignage-Signature`
- Produces canonical message: `controller_id + "\n" + counter + "\n" + method + "\n" + path_and_query + "\n" + entity_sha256`

- [ ] **Step 1: Write failing canonical-signature and route tests**

```python
def test_signature_matches_known_vector():
    canonical = canonical_message("store", 7, "POST", "/api/name",
                                  hashlib.sha256(b'{"name":"Front"}').hexdigest())
    got = sign(bytes.fromhex("11" * 32), canonical)
    assert got == "5a2a17c6dacd1fbf9584c45e4b8348ee875d6ce4e9e15aa01d60942eb2e04ef5"

def test_unsigned_mutation_is_rejected(client):
    assert client.post("/api/name", json={"name": "Front"}).status_code == 401

def test_signed_mutation_succeeds_once_and_replay_fails(client, paired_signer):
    headers = paired_signer("POST", "/api/name", b'{"name":"Front"}', counter=1)
    assert client.post("/api/name", content=b'{"name":"Front"}', headers=headers).status_code == 200
    assert client.post("/api/name", content=b'{"name":"Front"}', headers=headers).status_code == 409
```

Generate the fixed expected HMAC once with Python's `hmac.new` and commit the literal value.

- [ ] **Step 2: Run tests to verify failure**

Run: `agent\.venv\Scripts\python.exe -m pytest agent\tests\test_control_auth.py -q`

Expected: import or 401 expectation failures because mutations are still public.

- [ ] **Step 3: Implement USB detection and pairing routes**

```python
USB_NET = ipaddress.ip_network("10.55.0.0/24")

def require_usb(request: Request) -> None:
    try:
        address = ipaddress.ip_address(request.client.host)
    except ValueError:
        raise HTTPException(403, "USB connection required")
    if address not in USB_NET:
        raise HTTPException(403, "USB connection required")
```

Pairing request and response:

```python
class PairRequest(BaseModel):
    recovery_pin: str = Field(pattern=r"^\d{8}$")
    controller_id: str = Field(min_length=16, max_length=64)

@app.post("/api/pair")
async def pair_controller(req: PairRequest, request: Request):
    require_usb(request)
    result = trust_store.pair(req.recovery_pin, req.controller_id)
    return {
        "device_id": trust_store.device_id,
        "controller_id": req.controller_id,
        "controller_secret": base64.b64encode(result.secret).decode("ascii"),
    }
```

`GET /api/pair/status` is USB-only and returns `device_id`, `paired`, and `controller_id`; it never returns a secret or PIN data.

Also add `device_id` and `paired` to `/api/status` so discovery and reconnect
can repair saved endpoint data without treating a mutable display name as
identity.

- [ ] **Step 4: Implement signed-control dependency**

`require_control` must:

1. Parse all four headers.
2. Reject missing/malformed headers with 401.
3. Hash the exact JSON body for non-multipart requests and compare it with `X-PiSignage-Entity-SHA256`.
4. Recompute the HMAC with the stored controller secret.
5. Compare signatures with `hmac.compare_digest`.
6. Atomically accept and persist only a strictly increasing counter.
7. Return 409 for replayed/stale counters.

For multipart media/update routes, call:

```python
async def verify_uploaded_entity(file: UploadFile, expected_hex: str) -> None:
    digest = hashlib.sha256()
    while chunk := await file.read(1024 * 1024):
        digest.update(chunk)
    await file.seek(0)
    if not hmac.compare_digest(digest.hexdigest(), expected_hex):
        raise HTTPException(400, "Uploaded content hash does not match signature")
```

- [ ] **Step 5: Protect every mutation**

Apply `Depends(require_control)` to:

- `/api/dashboard`
- `/api/wifi`
- `/api/kiosk`
- `/api/update`
- `/api/name`
- `PUT /api/playlist`
- media upload/delete/detach/rename
- show-now set/clear
- next

Do not protect:

- display pages and static media GET
- status
- playlist/media/dashboard GET
- Wi-Fi status GET
- WebSocket
- pair/status pairing routes

- [ ] **Step 6: Convert existing mutation tests to signed fixtures**

The fixture owns one paired controller and allocates increasing counters:

```python
def signed_test_request(client, secret, counter, method, path, *,
                        json_body=None, files=None):
    if json_body is not None:
        entity = json.dumps(json_body, separators=(",", ":")).encode()
        send = {"content": entity,
                "headers": {"Content-Type": "application/json"}}
    elif files is not None:
        _, entity, _ = files["file"]
        send = {"files": files, "headers": {}}
    else:
        entity = b""
        send = {"headers": {}}
    entity_hash = hashlib.sha256(entity).hexdigest()
    canonical = canonical_message(
        "test-controller", counter, method.upper(), path, entity_hash)
    send["headers"].update({
        "X-PiSignage-Controller": "test-controller",
        "X-PiSignage-Counter": str(counter),
        "X-PiSignage-Entity-SHA256": entity_hash,
        "X-PiSignage-Signature": sign(secret, canonical),
    })
    return client.request(method, path, **send)

@pytest.fixture
def signed(client, agent_module):
    counter = itertools.count(1)
    secret = agent_module.trust_store.pair(
        agent_module._test_recovery_pin, "test-controller").secret
    def request(method, path, *, json=None, files=None):
        return signed_test_request(client, secret, next(counter), method, path,
                                   json_body=json, files=files)
    return request
```

In Task 3, extend the session `agent_module` fixture immediately after import:

```python
module._test_recovery_pin = module.trust_store.initialize()
```

This is a test-only module attribute; do not add a Recovery PIN getter to
production code.

- [ ] **Step 7: Verify and commit**

Run: `agent\.venv\Scripts\python.exe -m pytest agent -q`

Expected: all existing and new tests pass.

```powershell
git add agent/control_auth.py agent/main.py agent/tests
git commit -m "feat(agent): require paired signatures for control"
```

---

### Task 4: Add Windows Credential Vault and Request Signer

**Files:**
- Create: `signage-core/ControllerCredentials.cs`
- Create: `signage-core/ControlRequestSigner.cs`
- Create: `signage-core.Tests/ControllerCredentialsTests.cs`
- Create: `signage-core.Tests/ControlRequestSignerTests.cs`
- Create: `windows-app/DpapiSecretProtector.cs`
- Modify: `windows-app/PiSignageControl.csproj`

**Interfaces:**
- Produces: `ControllerCredential(byte[] Secret, long NextCounter)`, keyed by DeviceId in the vault
- Produces: `CredentialVault.Load()`, `Save()`, `Put()`, `Remove()`, and `TakeNextCounter(deviceId)`
- Produces: `ControlRequestSigner.Sign(HttpRequestMessage, controllerId, secret, counter, entityHash)`

- [ ] **Step 1: Write failing vault tests**

```csharp
[Fact]
public void Vault_round_trips_and_allocates_persisted_counters()
{
    var protector = new ReversibleTestProtector();
    var vault = new CredentialVault(TempFile(), protector);
    vault.Put("device-1", new byte[] { 1, 2, 3 });
    Assert.Equal(1, vault.TakeNextCounter("device-1"));
    Assert.Equal(2, new CredentialVault(vault.Path, protector)
        .TakeNextCounter("device-1"));
}

[Fact]
public void Removing_one_device_does_not_remove_other_secrets()
{
    var vault = new CredentialVault(TempFile(), new ReversibleTestProtector());
    vault.Put("one", new byte[] { 1 });
    vault.Put("two", new byte[] { 2 });
    vault.Remove("one");
    Assert.Null(vault.TryGet("one"));
    Assert.Equal(new byte[] { 2 }, vault.TryGet("two")!.Secret);
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test signage-core.Tests\signage-core.Tests.csproj --filter ControllerCredentialsTests`

Expected: compile failure because the vault types do not exist.

- [ ] **Step 3: Implement protected atomic vault persistence**

```csharp
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed record ControllerCredential(byte[] Secret, long NextCounter);

public sealed class CredentialVaultData
{
    public string ControllerId { get; set; } = Guid.NewGuid().ToString("N");
    public Dictionary<string, ControllerCredential> Devices { get; set; } = new();
}
```

The test file defines a reversible test-only protector:

```csharp
sealed class ReversibleTestProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();
    public byte[] Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
}

static string TempFile() => Path.Combine(
    Path.GetTempPath(), $"credentials-{Guid.NewGuid():N}.dat");

static string Sha256Hex(string text) => Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
```

Serialize `CredentialVaultData` to UTF-8 JSON, protect the entire blob, and write it with temp-file plus atomic move under `%AppData%\PiSignage\credentials.dat`. Lock counter allocation and persist the increment before returning it.

- [ ] **Step 4: Write signer known-vector tests**

```csharp
[Fact]
public void Signer_matches_python_known_vector()
{
    var request = new HttpRequestMessage(HttpMethod.Post, "http://pi/api/name")
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"name\":\"Front\"}"))
    };
    ControlRequestSigner.Sign(request, "store", Convert.FromHexString(new string('1', 64)),
                              7, Sha256Hex("{\"name\":\"Front\"}"));
    Assert.Equal("7", request.Headers.GetValues("X-PiSignage-Counter").Single());
    Assert.Equal(
        "5a2a17c6dacd1fbf9584c45e4b8348ee875d6ce4e9e15aa01d60942eb2e04ef5",
        request.Headers.GetValues("X-PiSignage-Signature").Single());
}
```

Use the same literal vector as Python Task 3.

- [ ] **Step 5: Implement CurrentUser DPAPI adapter**

```csharp
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}
```

Add:

```xml
<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
```

- [ ] **Step 6: Verify and commit**

Run:

```powershell
dotnet test signage-core.Tests\signage-core.Tests.csproj
dotnet build PiSignage.slnx
```

Expected: all tests pass; build has zero errors.

```powershell
git add signage-core signage-core.Tests windows-app
git commit -m "feat(app): protect controller credentials and sign requests"
```

---

### Task 5: Pair Before Wi-Fi and Persist Stable Device Identity

**Files:**
- Create: `signage-core/PairingClient.cs`
- Create: `signage-core.Tests/PairingClientTests.cs`
- Modify: `signage-core/SavedDevice.cs`
- Modify: `signage-core/DeviceStore.cs`
- Modify: `signage-core/SettingsStore.cs`
- Modify: `windows-app/Models.cs`
- Modify: `windows-app/WifiSetupWindow.xaml`
- Modify: `windows-app/WifiSetupWindow.xaml.cs`
- Modify: `windows-app/MainWindow.xaml.cs`
- Modify: `windows-app/SignageWindow.xaml.cs`

**Interfaces:**
- Consumes: `CredentialVault`, `ControlRequestSigner`
- Produces: `PairingClient.PairAsync(baseUrl, pin, controllerId) -> PairResult`
- Produces: persisted `SavedDevice.DeviceId` and `SavedDevice.Port`

- [ ] **Step 1: Write pairing-client and device persistence tests**

```csharp
sealed class StubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        const string json =
            """{"device_id":"device-id","controller_id":"11111111111111111111111111111111","controller_secret":"AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}

[Fact]
public async Task Pair_posts_pin_and_returns_device_secret()
{
    var client = new PairingClient(new HttpClient(new StubHandler()));
    var result = await client.PairAsync(
        "http://10.55.0.1:8080", "12345678",
        "11111111111111111111111111111111");
    Assert.Equal("device-id", result.DeviceId);
    Assert.Equal(32, result.Secret.Length);
}

[Fact]
public void Saved_device_round_trip_keeps_device_id_and_port()
{
    store.Save(new[] { new SavedDevice {
        DeviceId = "device-id", Name = "Front", Hostname = "pi-front",
        Ip = "192.168.1.20", Port = 8080 }});
    var got = store.Load().Single();
    Assert.Equal("device-id", got.DeviceId);
    Assert.Equal(8080, got.Port);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test signage-core.Tests\signage-core.Tests.csproj --filter "PairingClientTests|DeviceStoreTests"`

Expected: compile/test failures for missing device identity and pairing types.

- [ ] **Step 3: Add Recovery PIN to the USB wizard**

Insert before Wi-Fi fields:

```xml
<DockPanel Grid.Row="0" Margin="0,0,0,10">
  <TextBlock Text="8-digit PIN" Width="120" VerticalAlignment="Center"/>
  <PasswordBox x:Name="TxtRecoveryPin" MaxLength="8"
               PasswordChanged="Field_Changed"
               ToolTip="Printed on the bottom of the Pi"/>
</DockPanel>
```

Enable Connect only when the PIN matches `^\d{8}$` and SSID/password are non-empty.

- [ ] **Step 4: Reorder onboarding**

`Connect_Click` must execute:

1. Pair over `10.55.0.1:8080` using Recovery PIN and the vault's controller ID.
2. Save returned secret under returned device ID.
3. Send the signed Wi-Fi request over USB.
4. Confirm Wi-Fi status over USB.
5. Read `/api/status` and persist DeviceId, Port, name, hostname, and LAN IP.
6. Send one signed `/api/name` only if the operator supplies a name later.
7. If Wi-Fi configuration fails, retain the pairing and allow retry without consuming the PIN again.

Before step 1, read `/api/pair/status`. If the Pi is already paired to a
different controller ID, show:

```text
Pair this Pi to this laptop?

The previous laptop will lose access. Continue only if you are setting up a
replacement store laptop.
```

Proceed only after an explicit Yes response.

- [ ] **Step 5: Move tournament selection identity from names to device IDs**

Add `AppSettings.SignageDeviceIds`. Restore by ID when available; fall back to legacy hostname only for the builder's existing local settings. Save IDs on window close.

Fix rename by changing only the display name/agent-reported hostname; stable selection and credential lookup remain keyed by DeviceId.

- [ ] **Step 6: Stop discarding discovered ports**

Persist `d.Port` in `SavedDevice`, and replace every hardcoded remote `8080` with `dev.Port`. Keep `localhost:8080` for the Chromium kiosk's own display URL and `10.55.0.1:8080` for USB.

- [ ] **Step 7: Verify and commit**

Run:

```powershell
dotnet test PiSignage.slnx
dotnet build PiSignage.slnx
```

Expected: all tests pass and the WPF build succeeds.

```powershell
git add signage-core signage-core.Tests windows-app
git commit -m "feat(app): pair store laptop during USB onboarding"
```

---

### Task 6: Sign Every Windows Control Path

**Files:**
- Modify: `windows-app/ApiClient.cs`
- Modify: `signage-core/PushClient.cs`
- Modify: `signage-core/WifiProvisioner.cs`
- Modify: `signage-core/AgentUpdater.cs`
- Modify: corresponding tests under `signage-core.Tests/`
- Modify: `windows-app/MainWindow.xaml.cs`
- Modify: `windows-app/SignageWindow.xaml.cs`

**Interfaces:**
- Consumes: stable DeviceId, `CredentialVault.TakeNextCounter`, `ControlRequestSigner`
- Produces: authenticated name, playlist, media, dashboard, timer, kiosk, Wi-Fi, and update operations

- [ ] **Step 1: Add failing tests for each mutation family**

For ApiClient, PushClient, WifiProvisioner, and AgentUpdater, assert:

```csharp
static string Header(HttpRequestMessage request, string name) =>
    request.Headers.GetValues(name).Single();

static string Sha256Hex(byte[] bytes) => Convert.ToHexString(
    SHA256.HashData(bytes)).ToLowerInvariant();

Assert.Equal(controllerId, Header(request, "X-PiSignage-Controller"));
Assert.True(long.Parse(Header(request, "X-PiSignage-Counter")) > 0);
Assert.Equal(Sha256Hex(expectedEntity), Header(request, "X-PiSignage-Entity-SHA256"));
Assert.Equal(64, Header(request, "X-PiSignage-Signature").Length);
```

For multipart uploads, assert the entity hash equals the source file/zip bytes rather than the multipart envelope.

- [ ] **Step 2: Run targeted tests to verify failure**

Run:

`dotnet test signage-core.Tests\signage-core.Tests.csproj --filter "PushClientTests|WifiProvisionerTests|AgentUpdaterTests"`

Expected: header assertions fail.

- [ ] **Step 3: Thread device credentials through control calls**

Every mutation method receives a `ControlContext`:

```csharp
public sealed record ControlContext(
    string DeviceId,
    string ControllerId,
    byte[] Secret,
    Func<long> TakeNextCounter);
```

Create the JSON bytes before sending, compute their SHA-256, allocate a counter, sign, and send. Serialize only once so the signed bytes equal the transmitted bytes.

For uploads, hash source bytes, set the entity hash header, sign that hash, and transmit the same source.

- [ ] **Step 4: Serialize signed requests per device**

Use one `SemaphoreSlim` per DeviceId around counter allocation through response receipt. This prevents counter 2 arriving before counter 1 when two UI actions overlap.

- [ ] **Step 5: Verify all control paths**

Run:

```powershell
dotnet test PiSignage.slnx
dotnet build PiSignage.slnx
agent\.venv\Scripts\python.exe -m pytest agent -q
```

Expected: all tests and build pass.

- [ ] **Step 6: Commit**

```powershell
git add signage-core signage-core.Tests windows-app
git commit -m "feat(app): authenticate every Pi control request"
```

---

### Task 7: Implement Prepare for Delivery

**Files:**
- Create: `agent/delivery_reset.py`
- Create: `agent/tests/test_delivery_reset.py`
- Modify: `agent/main.py`
- Create: `windows-app/DeliveryPreparation.cs`
- Modify: `windows-app/MainWindow.xaml`
- Modify: `windows-app/MainWindow.xaml.cs`
- Modify: `windows-app/ThumbnailCache.cs`

**Interfaces:**
- Produces: signed, USB-only `POST /api/prepare-delivery`
- Produces: `DeliveryPreparation.RunAsync(device, controlContext)`

- [ ] **Step 1: Write failing reset tests**

Seed media, playlist, dashboard, name, override, controller trust, and mocked Wi-Fi profiles:

```python
def test_prepare_delivery_erases_customer_data_but_preserves_identity(
        signed_usb, agent_module):
    before_id = agent_module.trust_store.device_id
    response = signed_usb.post("/api/prepare-delivery")
    assert response.status_code == 200
    assert list(agent_module.MEDIA_DIR.iterdir()) == []
    assert agent_module.state.playlist.items == []
    assert agent_module._dashboard["view_data"]["boards"] == {}
    assert not agent_module.NAME_FILE.exists()
    assert agent_module.trust_store.controller_id is None
    assert agent_module.trust_store.device_id == before_id
    assert agent_module.trust_store.has_recovery_pin

def test_prepare_delivery_rejects_wifi_source(signed_lan):
    assert signed_lan.post("/api/prepare-delivery").status_code == 403
```

- [ ] **Step 2: Run tests to verify failure**

Run: `agent\.venv\Scripts\python.exe -m pytest agent\tests\test_delivery_reset.py -q`

Expected: 404 because the route does not exist.

- [ ] **Step 3: Implement reset ordering**

The route must:

1. Verify USB.
2. Verify signed controller.
3. Clear media files and `.part` files.
4. Replace playlist with empty enabled playlist; clear override/index.
5. Replace dashboard with empty boards and stopped timer.
6. Remove custom name and reset `DEVICE_NAME` to hostname.
7. Persist cleared state and wake the scheduler.
8. Enumerate NetworkManager connections by UUID/type and delete every `802-11-wireless` profile.
9. Clear controller ID, secret, and counter last.
10. Return `{"ok": true, "device_id": "device-id-from-trust-store"}`.

Keep trust identity/PIN and all installed code.

- [ ] **Step 4: Add the Windows confirmation and orchestration**

Add a secondary, builder-facing button labeled **Prepare Pi for delivery**. On click:

1. Require the Pi at `10.55.0.1:8080`.
2. Read pair status and match DeviceId to the selected saved device.
3. Show:

```text
Prepare this Pi for delivery?

This permanently removes your control access, Wi-Fi, media, playlists,
tournament screens, timer, and temporary name. The client will need the
8-digit PIN sticker to set it up.
```

4. Require the operator to type `PREPARE`.
5. Send the signed reset.
6. Remove that DeviceId from credential vault, saved devices, and SignageDeviceIds.
7. Call `ThumbnailCache.ClearDevice(device)`.
8. Show “Ready for delivery — unplug the USB cable.”

- [ ] **Step 5: Verify and commit**

Run:

```powershell
agent\.venv\Scripts\python.exe -m pytest agent -q
dotnet test PiSignage.slnx
dotnet build PiSignage.slnx
```

Expected: all tests and build pass.

```powershell
git add agent windows-app
git commit -m "feat: add safe prepare-for-delivery reset"
```

---

### Task 8: Secure Agent Bundles and Developer Deployment

**Files:**
- Modify: `windows-app/PiSignageControl.csproj`
- Modify: `windows-app/AgentBundle.cs`
- Modify: `agent/main.py`
- Modify: `agent/tests/test_update.py`
- Modify: `deploy-agent.ps1`

**Interfaces:**
- Consumes: controller vault and signed update route
- Produces: update bundles containing `main.py`, `trust.py`, `control_auth.py`, `delivery_reset.py`, and `static/**`

- [ ] **Step 1: Write failing bundle/update tests**

Assert the app bundle contains every Python module and the agent accepts only:

```text
main.py
trust.py
control_auth.py
delivery_reset.py
static/**
```

Add tests rejecting any other root file, unsigned update, mismatched zip hash, and archives whose compressed request exceeds 20 MB.

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
agent\.venv\Scripts\python.exe -m pytest agent\tests\test_update.py -q
dotnet test signage-core.Tests\signage-core.Tests.csproj --filter AgentUpdaterTests
```

Expected: new module/bundle assertions fail.

- [ ] **Step 3: Update embedding and validation**

Replace the single embedded `main.py` entry with:

```xml
<EmbeddedResource Include="..\agent\*.py" LogicalName="agent/%(Filename)%(Extension)" />
<EmbeddedResource Include="..\agent\static\**\*"
                  LogicalName="agent/static/%(RecursiveDir)%(Filename)%(Extension)" />
```

Compile every uploaded `.py` file before changing installed files. Enforce both compressed request and expanded size caps.

- [ ] **Step 4: Sign PowerShell deployments**

`deploy-agent.ps1` must:

1. Unprotect `%APPDATA%\PiSignage\credentials.dat` with CurrentUser DPAPI.
2. Match each saved device by DeviceId.
3. Atomically increment and rewrite its counter before each request.
4. Hash the zip bytes.
5. Construct the same canonical HMAC message as Python/C#.
6. Attach the four headers to `Invoke-RestMethod`.
7. Refuse deployment when a target lacks a paired credential.

Add `-WhatIf` output showing target name, DeviceId, version, and “paired” without revealing secrets.

- [ ] **Step 5: Verify and commit**

Run:

```powershell
agent\.venv\Scripts\python.exe -m pytest agent -q
dotnet test PiSignage.slnx
dotnet build PiSignage.slnx
pwsh -NoProfile -File .\deploy-agent.ps1 -WhatIf
```

Expected: tests/build pass; WhatIf lists targets without network mutations or secret output.

```powershell
git add agent windows-app signage-core deploy-agent.ps1
git commit -m "feat: authenticate complete agent updates"
```

---

### Task 9: Documentation, Security Regression, and Real-Pi Acceptance

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-07-23-usb-wifi-provisioning-design.md`
- Modify: `docs/superpowers/specs/2026-07-24-http-agent-update-design.md`
- Modify: `CONTEXT.md` only if implementation reveals a genuine domain-language correction

**Interfaces:**
- Consumes: completed secure lifecycle
- Produces: builder, client, recovery, delivery, and Remote support runbooks

- [ ] **Step 1: Update documentation**

Document exact flows:

**Builder setup**

1. Run `install.sh`.
2. Run `provision-usb.sh`.
3. Print `RECOVERY_PIN` on the bottom sticker.
4. Pair builder laptop over USB.
5. Join builder Wi-Fi.
6. Run full media, tournament, kiosk, and update tests.
7. Reconnect USB and run Prepare for delivery.
8. Confirm the Pi disappears from the builder device list and no longer accepts builder-signed Wi-Fi requests.

**Store onboarding**

1. Install/open the control app on the shared store Windows login.
2. Connect Pi by USB data cable.
3. Enter bottom-label PIN.
4. Choose store Wi-Fi and enter password.
5. Confirm the Pi appears by stable identity and responds over Wi-Fi.

**Ownership recovery**

1. Install/open app on replacement store laptop.
2. Connect each Pi by USB.
3. Enter its bottom-label PIN.
4. Confirm the warning that the previous laptop loses access.
5. Verify the old laptop receives 401 and the replacement succeeds.

**Remote support**

Use attended Windows Quick Assist; the store employee initiates and approves every session.

- [ ] **Step 2: Run automated security regression**

```powershell
agent\.venv\Scripts\python.exe -m pytest agent -q
dotnet test PiSignage.slnx
dotnet build PiSignage.slnx -c Release
dotnet list PiSignage.slnx package --vulnerable --include-transitive
```

Expected:

- all Python tests pass;
- all .NET tests pass;
- Release build succeeds;
- no vulnerable packages in the shipped `signage-core` or `PiSignageControl` projects;
- any test-only advisory is documented and removed by updating test packages before completion.

- [ ] **Step 3: Perform real-Pi builder acceptance**

On a test Pi:

1. Initialize and photograph the PIN label.
2. Pair builder laptop over USB.
3. Join builder Wi-Fi.
4. Upload image/video, save playlist, capture a tournament board, start/pause/extend timer, toggle kiosk, and update agent.
5. Use an unsigned curl mutation and verify 401.
6. Replay a captured signed request and verify 409.
7. Try five bad PINs and verify the 60-second block.
8. Prepare for delivery over USB.
9. Verify media, playlist, dashboard, timer, name, Wi-Fi, and builder access are gone.
10. Verify device ID and correct Recovery PIN still work.

- [ ] **Step 4: Perform store-laptop simulation**

On a second Windows user/profile or second laptop:

1. Pair with the same Pi using USB plus the Recovery PIN.
2. Join a different Wi-Fi network.
3. Verify all daily operations are passwordless.
4. Verify the builder laptop can no longer mutate the Pi.
5. Re-pair once more to prove Ownership recovery invalidates the prior laptop.

- [ ] **Step 5: Final repository checks and commit**

```powershell
git status --short
git diff --check
git grep -n "No auth\|unauthenticated" -- README.md docs agent windows-app signage-core
```

Expected: only intentional changes remain; no whitespace errors; obsolete unauthenticated-control documentation is gone.

```powershell
git add README.md docs CONTEXT.md
git commit -m "docs: document secure Pi ownership lifecycle"
```

---

## Self-Review Checklist

- [ ] Every state-changing route is protected or explicitly listed as pairing/reset bootstrap.
- [ ] Pairing and Prepare for delivery reject non-USB source addresses.
- [ ] Correct PIN plus USB replaces the old controller and invalidates its secret.
- [ ] HMAC known vectors match in Python, C#, and PowerShell.
- [ ] Counters survive restarts and reject replay without relying on wall-clock time.
- [ ] Multipart file integrity binds the uploaded bytes to the signature.
- [ ] The agent test suite cannot touch `agent/data`.
- [ ] Builder testing works over Wi-Fi before delivery.
- [ ] Prepare for delivery preserves only software, USB provisioning, device identity, and PIN verifier.
- [ ] Stable device identity survives rename, DHCP changes, and non-default ports.
- [ ] Daily store operation requires no password prompt.
- [ ] No builder credential remains after delivery.
- [ ] Quick Assist remains documentation-only and attended.
- [ ] No legacy insecure mode exists.
