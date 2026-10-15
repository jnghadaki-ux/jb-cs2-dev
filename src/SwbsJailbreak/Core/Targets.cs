using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Commands.Targeting;

namespace SwbsJailbreak.Core;

/// <summary>
/// SourceMod-style target resolution (ProcessTargetString). Supports everything CSS's Target supports
/// (@all @bots @human @alive @dead @me @!me @aim @ct @t @spec #userid #steamid64 name), "@!<target>" (everyone except <target>) plus "magic" filters
/// registered by modules (SMPlugins MagicTargetFilter equivalents such as @warden, @rebels, @freedays).
/// </summary>
public static class Targets
{
    private static readonly Dictionary<string, (string Description, Func<CCSPlayerController, bool> Filter)> Magic =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["@prisoners"] = ("prisoners", c => c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist),
            ["@guards"] = ("guards", c => c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist),
            ["@humans"] = ("humans", c => !c.IsBot),
            ["@alivet"] = ("alive prisoners", c => c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist && c.IsAliveP()),
            ["@alivect"] = ("alive guards", c => c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist && c.IsAliveP()),
            ["@deadt"] = ("dead prisoners", c => c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist && !c.IsAliveP()),
            ["@deadct"] = ("dead guards", c => c.Team == CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist && !c.IsAliveP()),
        };

    /// <summary>CSS's own group targets (usable after "@!" too).</summary>
    private static readonly HashSet<string> GroupTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "@all", "@bots", "@human", "@alive", "@dead", "@me", "@aim", "@ct", "@t", "@spec"
    };

    public static void RegisterMagic(string token, string description, Func<CCSPlayerController, bool> filter) =>
        Magic[token] = (description, filter);

    public static IEnumerable<string> MagicTokens => Magic.Keys;

    public sealed class Result
    {
        public List<CCSPlayerController> Players { get; } = new();

        /// <summary>Text for "Admin slayed {Description}" messages: player name or group description.</summary>
        public string Description { get; set; } = "";

        public bool IsGroup { get; set; }
        public string? Error { get; set; }
        public bool Ok => Error == null && Players.Count > 0;
    }

    /// <summary>Resolve a target pattern. Errors are already phrased for the caller.</summary>
    public static Result Find(CCSPlayerController? caller, string pattern, bool aliveOnly = false, bool deadOnly = false,
        bool singleOnly = false, bool respectImmunity = true, bool allowBots = true)
    {
        var res = new Result();
        pattern = pattern.Trim();
        if (pattern.Length == 0)
        {
            res.Error = Lang.T("core.target.none");
            return res;
        }

        List<CCSPlayerController> found;
        if (pattern.Length > 2 && pattern.StartsWith("@!") && !pattern.Equals("@!me", StringComparison.OrdinalIgnoreCase))
        {
            // "@!<target>": everyone except whoever <target> resolves to (a name, #userid, steamid or another @group).
            // "@!ct" / "@!t" / "@!warden" exclude a group; anything else is a player (name, #userid, steamid).
            var inner = pattern[2..];
            var asGroup = "@" + inner.TrimStart('@');
            var excluded = GroupTokens.Contains(asGroup) || Magic.ContainsKey(asGroup)
                ? Find(caller, asGroup, respectImmunity: false)
                : Find(caller, inner, respectImmunity: false);
            if (!excluded.Ok)
            {
                res.Error = excluded.Error ?? Lang.T("core.target.none");
                return res;
            }

            var skip = excluded.Players.Select(c => c.Slot).ToHashSet();
            found = Players.All().Where(c => !skip.Contains(c.Slot)).ToList();
            res.IsGroup = true;
            res.Description = "everyone but " + (excluded.IsGroup ? excluded.Description : excluded.Players[0].PlayerName);
        }
        else if (Magic.TryGetValue(pattern, out var magic))
        {
            found = Players.All().Where(magic.Filter).ToList();
            res.Description = magic.Description;
            res.IsGroup = true;
        }
        else
        {
            found = new Target(pattern).GetTarget(caller).Players
                .Where(c => c is { IsValid: true, IsHLTV: false }).ToList();
            res.IsGroup = pattern.StartsWith('@') && pattern is not "@me" and not "@aim";
            res.Description = res.IsGroup ? pattern.TrimStart('@') : found.FirstOrDefault()?.PlayerName ?? pattern;

            // Exact name match wins over partial matches (SourceMod behaviour).
            if (!pattern.StartsWith('@') && !pattern.StartsWith('#') && found.Count > 1)
            {
                var exact = found.Where(c => string.Equals(c.PlayerName, pattern, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 1) found = exact;
            }
        }

        if (!allowBots) found.RemoveAll(c => c.IsBot);
        if (aliveOnly) found.RemoveAll(c => !c.IsAliveP());
        if (deadOnly) found.RemoveAll(c => c.IsAliveP());

        if (found.Count == 0)
        {
            res.Error = Lang.T("core.target.none");
            return res;
        }

        if (singleOnly && found.Count > 1)
        {
            res.Error = Lang.T("core.target.ambiguous", pattern);
            return res;
        }

        if (respectImmunity && caller != null)
        {
            var before = found.Count;
            found.RemoveAll(t => !Perms.CanTarget(caller, t));
            if (found.Count == 0 && before > 0)
            {
                res.Error = Lang.T("core.target.immune");
                return res;
            }
        }

        res.Players.AddRange(found);
        if (!res.IsGroup && found.Count == 1) res.Description = found[0].PlayerName;
        return res;
    }

    /// <summary>Find + reply the error to the caller. Returns null on failure.</summary>
    public static Result? FindOrReply(CCSPlayerController? caller, CommandInfo info, string pattern, bool aliveOnly = false,
        bool deadOnly = false, bool singleOnly = false, bool respectImmunity = true)
    {
        var r = Find(caller, pattern, aliveOnly, deadOnly, singleOnly, respectImmunity);
        if (r.Ok) return r;
        Chat.Reply(caller, info, r.Error ?? Lang.T("core.target.none"));
        return null;
    }
}
