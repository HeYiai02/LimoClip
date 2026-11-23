using System.Security.Cryptography;
using LiveMonitor.Platforms.Plugins;
using Xunit;

namespace LiveMonitor.Platforms.Tests.Plugins;

public sealed class Ed25519VerifierTests
{
    private static readonly byte[] Payload = System.Text.Encoding.UTF8.GetBytes("limoclip-plugin-payload");

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(Payload, seed);

        Assert.True(Ed25519Verifier.Verify(Payload, signature, publicKeyBase64: publicKey));
    }

    [Fact]
    public void Verify_WithMatchingSha256_ReturnsTrue()
    {
        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(Payload, seed);
        var sha = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

        Assert.True(Ed25519Verifier.Verify(Payload, signature, sha, publicKey));
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse()
    {
        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(Payload, seed);
        var tampered = (byte[])Payload.Clone();
        tampered[0] ^= 0xFF;

        Assert.False(Ed25519Verifier.Verify(tampered, signature, publicKeyBase64: publicKey));
    }

    [Fact]
    public void Verify_WrongPublicKey_ReturnsFalse()
    {
        var seed = PluginTestKeys.NewSeed();
        var signature = PluginTestKeys.Sign(Payload, seed);
        var otherPublicKey = PluginTestKeys.PublicKeyBase64(PluginTestKeys.NewSeed());

        Assert.False(Ed25519Verifier.Verify(Payload, signature, publicKeyBase64: otherPublicKey));
    }

    [Fact]
    public void Verify_Sha256Mismatch_ReturnsFalse()
    {
        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);
        var signature = PluginTestKeys.Sign(Payload, seed);
        var wrongSha = new string('0', 64);

        Assert.False(Ed25519Verifier.Verify(Payload, signature, wrongSha, publicKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 32 字节，长度不对
    public void Verify_InvalidSignature_ReturnsFalse(string? signature)
    {
        var seed = PluginTestKeys.NewSeed();
        var publicKey = PluginTestKeys.PublicKeyBase64(seed);

        Assert.False(Ed25519Verifier.Verify(Payload, signature, publicKeyBase64: publicKey));
    }

    [Fact]
    public void Verify_EmptyPayload_ReturnsFalse()
    {
        var seed = PluginTestKeys.NewSeed();
        var signature = PluginTestKeys.Sign(Payload, seed);

        Assert.False(Ed25519Verifier.Verify(Array.Empty<byte>(), signature, publicKeyBase64: PluginTestKeys.PublicKeyBase64(seed)));
    }

    [Fact]
    public void DefaultTrustedPublicKey_IsValid32ByteEd25519Key()
    {
        var bytes = Convert.FromBase64String(PluginTrust.PublicKeyBase64);

        Assert.Equal(32, bytes.Length);
    }
}
