namespace Arkive.Core.Archives;

public sealed class PackageAccessException : IOException
{
    public PackageAccessException(string message)
        : base(message)
    {
    }

    public PackageAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
