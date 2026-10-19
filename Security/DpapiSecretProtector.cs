using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NativeTavern.Security;

public sealed class DpapiSecretProtector(ILogger<DpapiSecretProtector> logger) : ISecretProtector
{
    // Keep the original entropy so API keys saved before the Lorehaven rename still decrypt.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NativeTavern.ProviderSettings.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            logger.LogWarning(ex, "Unable to decrypt the saved provider credential.");
            return string.Empty;
        }
    }
}
