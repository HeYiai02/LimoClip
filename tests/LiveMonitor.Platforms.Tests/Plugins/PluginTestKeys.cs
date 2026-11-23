using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace LiveMonitor.Platforms.Tests.Plugins;

/// <summary>单测用 Ed25519 密钥与签名工具（与生产私钥无关）。</summary>
internal static class PluginTestKeys
{
    public static byte[] NewSeed()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
    }

    public static string PublicKeyBase64(byte[] seed)
    {
        var privateKey = new Ed25519PrivateKeyParameters(seed);
        return Convert.ToBase64String(privateKey.GeneratePublicKey().GetEncoded());
    }

    public static string Sign(byte[] payload, byte[] seed)
    {
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(seed));
        signer.BlockUpdate(payload, 0, payload.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }
}
