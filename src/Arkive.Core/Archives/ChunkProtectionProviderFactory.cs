using System.Security.Cryptography;
using System.Text;

namespace Arkive.Core.Archives;

public static class ChunkProtectionProviderFactory
{
    public const string WesternGlobalProfileId = "western-global-steam-v1";
    public const string WesternGlobalProviderName =
        "Arkive Western/global Steam profile v1 (AES-256-ECB protected prefix)";

    public static IChunkProtectionProvider CreateWesternGlobal()
    {
        // This profile is intentionally versioned: a future client key change should add or
        // replace a named profile rather than silently changing the meaning of an old report.
        var key = Encoding.ASCII.GetBytes("NCMZPFB5R74EGAUWE191UTCLBJV2N6GX");

        try
        {
            return new AesEcbPrefixProtectionProvider(key, WesternGlobalProviderName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
