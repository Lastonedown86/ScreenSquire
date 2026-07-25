using System.Collections.Concurrent;
using System.Text;
using PiSignage.Signage;
using Xunit;

public class ControllerCredentialsTests
{
    static string TempFile() => Path.Combine(
        Path.GetTempPath(), $"credentials-{Guid.NewGuid():N}.dat");

    [Fact]
    public void Vault_round_trips_and_allocates_persisted_counters()
    {
        var path = TempFile();
        var protector = new ReversibleTestProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            vault.Put("device-1", new byte[] { 1, 2, 3 });

            Assert.Equal(1, vault.TakeNextCounter("device-1"));
            Assert.Equal(2, new CredentialVault(vault.Path, protector)
                .TakeNextCounter("device-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Removing_one_device_does_not_remove_other_secrets()
    {
        var path = TempFile();
        try
        {
            var vault = new CredentialVault(path, new ReversibleTestProtector());
            vault.Put("one", new byte[] { 1 });
            vault.Put("two", new byte[] { 2 });

            vault.Remove("one");

            Assert.Null(vault.TryGet("one"));
            Assert.Equal(new byte[] { 2 }, vault.TryGet("two")!.Secret);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_and_load_preserve_controller_identity_and_device_data()
    {
        var path = TempFile();
        var protector = new ReversibleTestProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            var data = vault.Load();
            data.ControllerId = "store-controller";
            data.Devices["device-1"] = new ControllerCredential(new byte[] { 4, 5 }, 12);

            vault.Save(data);

            var loaded = new CredentialVault(path, protector).Load();
            Assert.Equal("store-controller", loaded.ControllerId);
            Assert.Equal(new byte[] { 4, 5 }, loaded.Devices["device-1"].Secret);
            Assert.Equal(12, loaded.Devices["device-1"].NextCounter);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Entire_vault_blob_is_protected_at_rest()
    {
        var path = TempFile();
        var protector = new TrackingProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            var data = vault.Load();
            data.ControllerId = "plaintext-controller-id";
            vault.Save(data);
            vault.Put("plaintext-device-id", Encoding.UTF8.GetBytes("plaintext-secret"));

            var stored = File.ReadAllBytes(path);

            Assert.True(protector.ProtectCalls >= 1);
            Assert.DoesNotContain(
                "plaintext-controller-id",
                Encoding.UTF8.GetString(stored),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "plaintext-device-id",
                Encoding.UTF8.GetString(stored),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "plaintext-secret",
                Encoding.UTF8.GetString(stored),
                StringComparison.Ordinal);
            Assert.Equal(
                Encoding.UTF8.GetBytes("plaintext-secret"),
                new CredentialVault(path, protector).TryGet("plaintext-device-id")!.Secret);
            Assert.True(protector.UnprotectCalls >= 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Concurrent_vault_instances_allocate_unique_persisted_counters()
    {
        var path = TempFile();
        var protector = new ReversibleTestProtector();
        try
        {
            var first = new CredentialVault(path, protector);
            var second = new CredentialVault(path, protector);
            first.Put("device-1", new byte[] { 9 });
            var allocated = new ConcurrentBag<long>();

            Parallel.For(0, 64, i =>
            {
                var vault = i % 2 == 0 ? first : second;
                allocated.Add(vault.TakeNextCounter("device-1"));
            });

            Assert.Equal(Enumerable.Range(1, 64).Select(i => (long)i), allocated.Order());
            Assert.Equal(65, new CredentialVault(path, protector)
                .TakeNextCounter("device-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Counter_is_not_returned_or_advanced_when_persistence_fails()
    {
        var path = TempFile();
        var protector = new FailingProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            vault.Put("device-1", new byte[] { 7 });
            protector.FailProtection = true;

            Assert.Throws<IOException>(() => vault.TakeNextCounter("device-1"));

            protector.FailProtection = false;
            Assert.Equal(1, new CredentialVault(path, protector)
                .TakeNextCounter("device-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Default_path_is_the_current_users_app_data_vault()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PiSignage",
                "credentials.dat"),
            CredentialVault.DefaultPath());
    }

    sealed class ReversibleTestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }

    sealed class TrackingProtector : ISecretProtector
    {
        const byte Mask = 0xa5;

        public int ProtectCalls { get; private set; }
        public int UnprotectCalls { get; private set; }

        public byte[] Protect(byte[] plaintext)
        {
            ProtectCalls++;
            return Transform(plaintext);
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            UnprotectCalls++;
            return Transform(ciphertext);
        }

        static byte[] Transform(byte[] value) => value.Select(b => (byte)(b ^ Mask)).ToArray();
    }

    sealed class FailingProtector : ISecretProtector
    {
        public bool FailProtection { get; set; }

        public byte[] Protect(byte[] plaintext)
        {
            if (FailProtection)
                throw new IOException("Test persistence failure.");
            return plaintext.Reverse().ToArray();
        }

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }
}
