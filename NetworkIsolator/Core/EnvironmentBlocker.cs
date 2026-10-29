namespace NetworkIsolator.Core;

/// <summary>
/// Defense-in-depth layer: a dead proxy injected into the application's environment.
/// Does not replace the firewall rule (many apps ignore these variables) but cleanly
/// cuts off the HTTP clients that honor them (curl, Python, Node, Electron, ...).
/// </summary>
public static class EnvironmentBlocker
{
    private const string Dead = "http://127.0.0.1:0";
    private const string DeadSocks = "socks5://127.0.0.1:0";

    /// <summary>Writes the "dead" proxy variables into an environment dictionary.</summary>
    public static void Apply(IDictionary<string, string?> env)
    {
        env["HTTP_PROXY"] = Dead;
        env["HTTPS_PROXY"] = Dead;
        env["ALL_PROXY"] = DeadSocks;
        env["http_proxy"] = Dead;
        env["https_proxy"] = Dead;
        env["all_proxy"] = DeadSocks;
        env["NO_PROXY"] = "";
        env["no_proxy"] = "";
    }
}
