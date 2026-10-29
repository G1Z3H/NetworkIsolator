namespace NetworkIsolator.Models;

/// <summary>REAL state of an application in Windows Firewall (read, not assumed).</summary>
public enum BlockState
{
    /// <summary>Check in progress or impossible.</summary>
    Unknown = 0,

    /// <summary>No permanent rule: the application can connect.</summary>
    Free,

    /// <summary>Some rules exist, others are missing (incomplete block).</summary>
    Partial,

    /// <summary>All rules exist: the application is blocked.</summary>
    Blocked
}
