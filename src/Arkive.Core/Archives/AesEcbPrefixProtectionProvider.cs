using System.Security.Cryptography;
using Arkive.Core.Packages;

namespace Arkive.Core.Archives;

public sealed class AesEcbPrefixProtectionProvider : IChunkProtectionProvider
{
    public const string DefaultName = "AES-256-ECB protected prefix";
    public const int RequiredKeyBytes = 32;
    public const int ProtectedPrefixBytes = 4096;

    private readonly byte[] _key;
    private readonly string _name;

    public AesEcbPrefixProtectionProvider(ReadOnlySpan<byte> key, string? name = null)
    {
        if (key.Length != RequiredKeyBytes)
        {
            throw new ArgumentException(
                $"The provider requires exactly {RequiredKeyBytes} key bytes.",
                nameof(key));
        }

        _key = key.ToArray();
        _name = string.IsNullOrWhiteSpace(name) ? DefaultName : name;
    }

    public string Name => _name;

    public bool CanUnprotect(PackageSummary summary, CompressedChunk chunk) =>
        chunk.LostArkFlags == 1;

    public bool CanProtect(PackageSummary summary, CompressedChunk chunk) =>
        chunk.LostArkFlags == 1;

    public void UnprotectBlock(
        PackageSummary summary,
        CompressedChunk chunk,
        int blockIndex,
        Span<byte> compressedBlock)
    {
        var decryptLength = Math.Min(ProtectedPrefixBytes, compressedBlock.Length);
        decryptLength -= decryptLength % 16;
        if (decryptLength == 0)
        {
            return;
        }

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = _key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.DecryptEcb(compressedBlock[..decryptLength], compressedBlock[..decryptLength], PaddingMode.None);
    }

    public void ProtectBlock(
        PackageSummary summary,
        CompressedChunk chunk,
        int blockIndex,
        Span<byte> compressedBlock)
    {
        var encryptLength = Math.Min(ProtectedPrefixBytes, compressedBlock.Length);
        encryptLength -= encryptLength % 16;
        if (encryptLength == 0)
        {
            return;
        }

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = _key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.EncryptEcb(compressedBlock[..encryptLength], compressedBlock[..encryptLength], PaddingMode.None);
    }
}
