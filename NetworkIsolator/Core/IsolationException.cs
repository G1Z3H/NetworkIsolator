using NetworkIsolator.Models;

namespace NetworkIsolator.Core;

/// <summary>Blocking error with a message that is already readable by the user.</summary>
public sealed class IsolationException : Exception
{
    public IsolationErrorKind Kind { get; }

    public IsolationException(IsolationErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
