namespace TShock.AgentBridge;

/// <summary>
/// Who may own a region created through the bridge. The server admin decides:
/// the default owner is used when the caller does not ask for another one, and
/// the allowlist is the only set of names a caller may request. An empty
/// allowlist means "default owner only".
/// </summary>
internal sealed record RegionOwnerPolicy(string DefaultOwner, IReadOnlySet<string> AllowedOwners)
{
    internal const string DefaultOwnerName = "mcp-gateway";
    internal const int MaxOwnerLength = 64;

    /// <summary>
    /// Reads the policy from <c>TSHOCK_BRIDGE_REGION_OWNER</c> and
    /// <c>TSHOCK_BRIDGE_REGION_OWNERS</c> (comma or semicolon separated). Invalid
    /// entries are ignored on purpose: a bad name must not stop the server, and
    /// the default owner is always allowed.
    /// </summary>
    internal static RegionOwnerPolicy FromEnvironment()
    {
        var configuredDefault = Environment.GetEnvironmentVariable("TSHOCK_BRIDGE_REGION_OWNER");
        var defaultOwner = IsUsableName(configuredDefault) ? configuredDefault!.Trim() : DefaultOwnerName;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { defaultOwner };
        var rawAllowed = Environment.GetEnvironmentVariable("TSHOCK_BRIDGE_REGION_OWNERS");
        if (!string.IsNullOrWhiteSpace(rawAllowed))
        {
            foreach (var candidate in rawAllowed.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = candidate.Trim();
                if (IsUsableName(name))
                {
                    allowed.Add(name);
                }
            }
        }

        return new RegionOwnerPolicy(defaultOwner, allowed);
    }

    internal bool IsAllowed(string owner) => AllowedOwners.Contains(owner);

    private static bool IsUsableName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Trim().Length <= MaxOwnerLength &&
        !name.Any(char.IsControl);
}
