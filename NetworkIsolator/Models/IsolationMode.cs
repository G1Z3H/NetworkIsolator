namespace NetworkIsolator.Models;

/// <summary>Level of network blocking applied to the application.</summary>
public enum IsolationMode
{
    /// <summary>All outgoing traffic (and incoming if enabled) is blocked.</summary>
    BlockAll = 0,

    /// <summary>The local network (LAN, loopback, multicast) stays allowed: only the Internet is blocked.</summary>
    AllowLan = 1
}
