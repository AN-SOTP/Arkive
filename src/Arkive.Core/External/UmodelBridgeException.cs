namespace Arkive.Core.External;

public sealed class UmodelBridgeException : IOException
{
    public UmodelBridgeException(string message)
        : base(message)
    {
    }

    public UmodelBridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
