using System.Security.Cryptography;
using PiSignage.Signage;

namespace PiSignage.Control;

public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}
