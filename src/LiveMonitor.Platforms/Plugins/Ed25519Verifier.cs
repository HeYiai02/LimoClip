using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace LiveMonitor.Platforms.Plugins;

/// <summary>
/// 插件发布物（DLL / manifest 条目中的 extraScripts）的 Ed25519 签名校验。
/// 校验顺序：先比对 SHA256（快速失败），再校验 Ed25519 签名；任何异常都视为校验失败。
/// </summary>
public static class Ed25519Verifier
{
    private const int SignatureLength = 64;
    private const int PublicKeyLength = 32;

    /// <summary>
    /// 校验 payload 的 Ed25519 签名。
    /// </summary>
    /// <param name="payload">被签名的原始字节（插件 DLL 原始字节等）。</param>
    /// <param name="signatureBase64">Base64 的 64 字节签名。</param>
    /// <param name="expectedSha256Hex">可选的期望 SHA256（64 位小写十六进制），为空时跳过比对比对。</param>
    /// <param name="publicKeyBase64">可选的公钥覆盖（测试用），为空时使用 <see cref="PluginTrust.PublicKeyBase64"/>。</param>
    public static bool Verify(
        byte[]? payload,
        string? signatureBase64,
        string? expectedSha256Hex = null,
        string? publicKeyBase64 = null)
    {
        if (payload is null || payload.Length == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(expectedSha256Hex))
            {
                var actualSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
                if (!string.Equals(actualSha, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var signatureBytes = Convert.FromBase64String(signatureBase64.Trim());
            if (signatureBytes.Length != SignatureLength)
            {
                return false;
            }

            var keyBytes = Convert.FromBase64String(
                string.IsNullOrWhiteSpace(publicKeyBase64) ? PluginTrust.PublicKeyBase64 : publicKeyBase64.Trim());
            if (keyBytes.Length != PublicKeyLength)
            {
                return false;
            }

            var signer = new Ed25519Signer();
            signer.Init(forSigning: false, new Ed25519PublicKeyParameters(keyBytes));
            signer.BlockUpdate(payload, 0, payload.Length);
            return signer.VerifySignature(signatureBytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or Org.BouncyCastle.Crypto.CryptoException)
        {
            return false;
        }
    }

    /// <summary>
    /// 校验文件的 Ed25519 签名。
    /// </summary>
    public static bool VerifyFile(
        string filePath,
        string? signatureBase64,
        string? expectedSha256Hex = null,
        string? publicKeyBase64 = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return Verify(File.ReadAllBytes(filePath), signatureBase64, expectedSha256Hex, publicKeyBase64);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
