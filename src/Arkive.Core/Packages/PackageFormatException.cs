namespace Arkive.Core.Packages;

public sealed class PackageFormatException : IOException
{
    public PackageFormatException(string message)
        : base(message)
    {
    }

    public PackageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
