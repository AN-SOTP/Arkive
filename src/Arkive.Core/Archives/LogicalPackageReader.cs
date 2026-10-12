using Arkive.Core.Packages;

namespace Arkive.Core.Archives;

public sealed class LogicalPackageReader
{
    private readonly UpkPackageReader _summaryReader;
    private readonly IChunkProtectionProvider _protectionProvider;

    public LogicalPackageReader(
        UpkPackageReader? summaryReader = null,
        IChunkProtectionProvider? protectionProvider = null)
    {
        _summaryReader = summaryReader ?? new UpkPackageReader();
        _protectionProvider = protectionProvider ?? NoChunkProtectionProvider.Instance;
    }

    public LogicalPackageStream Open(string filePath)
    {
        var summary = _summaryReader.Read(filePath);
        return new LogicalPackageStream(summary, _protectionProvider);
    }

    public LogicalPackageStream Open(PackageSummary summary) =>
        new(summary, _protectionProvider);
}
