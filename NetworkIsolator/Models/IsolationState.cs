namespace NetworkIsolator.Models;

/// <summary>Life cycle of a temporary-block session.</summary>
public enum IsolationState
{
    Created,
    Starting,
    Isolated,
    Stopping,
    Stopped,
    Failed
}
