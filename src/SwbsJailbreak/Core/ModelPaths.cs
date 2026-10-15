using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>
/// Guards against player model paths that do not exist on the current CS2 build. Valve moved its agents from
/// characters/models/ctm_* / tm_* to agents/models/...; a player given a model that does not exist turns into a small
/// box and sees an oversized first-person gun, so those paths are never applied.
/// </summary>
public static class ModelPaths
{
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Engine-free: an agent path from before CS2 moved its agents to agents/models/.</summary>
    public static bool IsLegacyAgentPath(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/').ToLowerInvariant();
        return p.StartsWith("characters/models/ctm_") || p.StartsWith("characters/models/tm_");
    }

    /// <summary>False (and one warning per path) for a path that must not be applied to a player.</summary>
    public static bool UsableOnPlayer(string? path, string setting)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!IsLegacyAgentPath(path)) return true;
        if (Warned.Add(path.Trim()))
            JailbreakPlugin.Instance?.Logger.LogWarning(
                "[Models] {Setting} = {Path} is an old agent path that no longer exists in CS2 (agents now live in agents/models/...). " +
                "It is not applied, because a missing player model gives players huge first-person guns.", setting, path);
        return false;
    }
}
