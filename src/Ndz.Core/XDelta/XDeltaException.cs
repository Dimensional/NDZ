namespace Ndz.Core.XDelta;

/// <summary>Thrown when applying or generating an xdelta3/VCDIFF (RFC 3284) patch fails.</summary>
public sealed class XDeltaException : Exception
{
    public XDeltaException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
