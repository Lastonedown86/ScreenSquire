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
    public void Save_rejects_stale_snapshot_instead_of_rolling_back_counter()
    {
        var path = TempFile();
        var protector = new ReversibleTestProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            vault.Put("device-1", new byte[] { 1 });
            var stale = vault.Load();

            Assert.Equal(1, vault.TakeNextCounter("device-1"));
            Assert.Throws<InvalidOperationException>(() => vault.Save(stale));

            Assert.Equal(2, new CredentialVault(path, protector)
                .TakeNextCounter("device-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_rejects_stale_snapshot_instead_of_resurrecting_removed_device()
    {
        var path = TempFile();
        var protector = new ReversibleTestProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            vault.Put("device-1", new byte[] { 1 });
            var stale = vault.Load();

            vault.Remove("device-1");
            Assert.Throws<InvalidOperationException>(() => vault.Save(stale));

            Assert.Null(new CredentialVault(path, protector).TryGet("device-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Every_persisted_mutation_advances_the_vault_revision()
    {
        var path = TempFile();
        var protector = new ReversibleTestProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            var editable = vault.Load();
            Assert.Equal(0, editable.Revision);

            vault.Put("device-1", new byte[] { 1 });
            Assert.Equal(1, vault.Load().Revision);

            vault.TakeNextCounter("device-1");
            Assert.Equal(2, vault.Load().Revision);

            vault.Remove("device-1");
            Assert.Equal(3, vault.Load().Revision);

            editable = vault.Load();
            editable.Devices["device-2"] = new ControllerCredential(new byte[] { 2 }, 1);
            vault.Save(editable);
            Assert.Equal(4, editable.Revision);
            Assert.Equal(4, vault.Load().Revision);
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
    public void Counter_overflow_does_not_change_persisted_credential()
    {
        var path = TempFile();
        var protector = new ReversibleTestProtector();
        try
        {
            var vault = new CredentialVault(path, protector);
            var data = vault.Load();
            data.Devices["device-1"] =
                new ControllerCredential(new byte[] { 1 }, long.MaxValue);
            vault.Save(data);

            Assert.Throws<OverflowException>(() => vault.TakeNextCounter("device-1"));
            Assert.Equal(
                long.MaxValue,
                new CredentialVault(path, protector).TryGet("device-1")!.NextCounter);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Put_and_try_get_do_not_alias_callers_secret_arrays()
    {
        var path = TempFile();
        try
        {
            var vault = new CredentialVault(path, new ReversibleTestProtector());
            var supplied = new byte[] { 1, 2, 3 };
            vault.Put("device-1", supplied);

            supplied[0] = 9;
            var fetched = vault.TryGet("device-1")!;
            fetched.Secret[1] = 9;

            Assert.Equal(new byte[] { 1, 2, 3 }, vault.TryGet("device-1")!.Secret);
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
