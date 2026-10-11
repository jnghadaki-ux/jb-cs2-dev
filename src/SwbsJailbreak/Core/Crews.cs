using System.Drawing;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

// =====================================================================================================================
// Crews: persistent prisoner gangs (design: docs/design/CREWS.md). This file is engine-free (model + rules) so the
// rules are unit-tested; the module (Modules/Crews) does the CS2 side.
// =====================================================================================================================

public enum CrewRank
{
    Member = 0,
    Lieutenant = 1,
    Boss = 2
}

public sealed class CrewMember
{
    [JsonPropertyName("steam")] public string SteamId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rank")] public CrewRank Rank { get; set; }
    [JsonPropertyName("joined")] public long JoinedUnix { get; set; }

    /// <summary>Respect this member has earned for the crew (lifetime, shown on the roster).</summary>
    [JsonPropertyName("earned")] public int Earned { get; set; }
}

public sealed class Crew
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("color")] public int Color { get; set; }
    [JsonPropertyName("motd")] public string Motd { get; set; } = "";
    [JsonPropertyName("founded")] public long FoundedUnix { get; set; }

    /// <summary>Spendable respect (favors). Decays weekly.</summary>
    [JsonPropertyName("respect")] public int Respect { get; set; }

    /// <summary>Lifetime respect: sets the crew level, only lost through admin action or penalties.</summary>
    [JsonPropertyName("legacy")] public int Legacy { get; set; }

    [JsonPropertyName("wars")] public int WarsWon { get; set; }
    [JsonPropertyName("rival")] public string RivalId { get; set; } = "";
    [JsonPropertyName("decayed")] public long LastDecayUnix { get; set; }
    [JsonPropertyName("members")] public List<CrewMember> Members { get; set; } = new();

    public CrewMember? Boss => Members.FirstOrDefault(m => m.Rank == CrewRank.Boss);
    public CrewMember? Member(string steamId) => Members.FirstOrDefault(m => m.SteamId == steamId);
}

/// <summary>Colour palette: chat colour tag + RGB for beams, rings and glow.</summary>
public static class CrewColors
{
    public static readonly (string Name, string Chat, Color Rgb)[] All =
    {
        ("Red", "{red}", Color.FromArgb(255, 230, 50, 50)),
        ("Blue", "{blue}", Color.FromArgb(255, 70, 130, 255)),
        ("Green", "{green}", Color.FromArgb(255, 60, 210, 90)),
        ("Gold", "{gold}", Color.FromArgb(255, 230, 180, 40)),
        ("Purple", "{purple}", Color.FromArgb(255, 170, 90, 230)),
        ("Orange", "{orange}", Color.FromArgb(255, 255, 140, 30)),
        ("Sky", "{lightblue}", Color.FromArgb(255, 120, 200, 255)),
        ("Pink", "{magenta}", Color.FromArgb(255, 240, 90, 200))
    };

    public static (string Name, string Chat, Color Rgb) Of(int index) => All[Math.Clamp(index, 0, All.Length - 1)];

    /// <summary>Colours unlocked at a crew level (4 at level 1, +1 per level).</summary>
    public static int Unlocked(int level) => Math.Clamp(3 + level, 1, All.Length);
}

/// <summary>A favor a crew can call once per round with respect (information / identity only, never combat stats).</summary>
public sealed record CrewFavor(string Key, string Name, int Cost, int MinLevel, string Description);

/// <summary>All crew rules, engine-free.</summary>
public static class CrewRules
{
    /// <summary>Lifetime respect needed for levels 1..7.</summary>
    public static readonly int[] LevelThresholds = { 0, 100, 300, 700, 1500, 3000, 6000 };

    public static int Level(int legacy)
    {
        var level = 1;
        for (var i = 1; i < LevelThresholds.Length; i++)
            if (legacy >= LevelThresholds[i]) level = i + 1;
        return level;
    }

    /// <summary>Respect still needed for the next level, 0 at max.</summary>
    public static int ToNextLevel(int legacy)
    {
        var level = Level(legacy);
        return level >= LevelThresholds.Length ? 0 : LevelThresholds[level] - legacy;
    }

    public static int Slots(int level, int baseSlots, int maxSlots) => Math.Clamp(baseSlots + level - 1, 1, Math.Max(1, maxSlots));

    public static int MaxLieutenants(int level) => level >= 5 ? 3 : 2;

    // ------------------------------------------------------------------ names and tags

    private static readonly Regex NameChars = new(@"^[A-Za-z0-9 _\-'.!&]+$", RegexOptions.Compiled);
    private static readonly Regex TagChars = new(@"^[A-Za-z0-9]+$", RegexOptions.Compiled);

    /// <summary>3-24 characters: letters, digits, space and _ - ' . ! &amp;; no edge or double spaces; no banned words.</summary>
    public static bool ValidName(string name, IEnumerable<string> banned, out string reasonKey)
    {
        reasonKey = "";
        if (name.Length is < 3 or > 24) { reasonKey = "crews.err.name_length"; return false; }
        if (name != name.Trim() || name.Contains("  ")) { reasonKey = "crews.err.name_spaces"; return false; }
        if (!NameChars.IsMatch(name)) { reasonKey = "crews.err.name_chars"; return false; }
        if (ContainsBanned(name, banned)) { reasonKey = "crews.err.banned"; return false; }
        return true;
    }

    /// <summary>2-5 letters/digits, stored upper-case.</summary>
    public static bool ValidTag(string tag, IEnumerable<string> banned, out string reasonKey)
    {
        reasonKey = "";
        if (tag.Length is < 2 or > 5) { reasonKey = "crews.err.tag_length"; return false; }
        if (!TagChars.IsMatch(tag)) { reasonKey = "crews.err.tag_chars"; return false; }
        if (ContainsBanned(tag, banned)) { reasonKey = "crews.err.banned"; return false; }
        return true;
    }

    public static string NormalizeTag(string tag) => tag.Trim().ToUpperInvariant();

    private static bool ContainsBanned(string s, IEnumerable<string> banned)
    {
        var plain = new string(s.Where(char.IsLetterOrDigit).ToArray());
        return banned.Any(b => b.Length >= 3 && (s.Contains(b, StringComparison.OrdinalIgnoreCase) || plain.Contains(b, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------ ranks

    public static bool CanInvite(CrewRank r) => r >= CrewRank.Lieutenant;
    public static bool CanUseFavors(CrewRank r) => r >= CrewRank.Lieutenant;
    public static bool CanSetMotd(CrewRank r) => r >= CrewRank.Lieutenant;
    public static bool CanManageCrew(CrewRank r) => r == CrewRank.Boss; // promote, demote, transfer, colour, rival, disband

    /// <summary>The Boss can kick anyone but themselves; a Lieutenant only Members.</summary>
    public static bool CanKick(CrewRank actor, CrewRank target) =>
        actor == CrewRank.Boss ? target != CrewRank.Boss : actor == CrewRank.Lieutenant && target == CrewRank.Member;

    // ------------------------------------------------------------------ respect

    /// <summary>Weekly decay of spendable respect: whole weeks since the last decay, compounding. Returns the new stamp.</summary>
    public static long ApplyDecay(Crew crew, long nowUnix, double percentPerWeek)
    {
        const long week = 7L * 24 * 3600;
        if (crew.LastDecayUnix <= 0) return crew.LastDecayUnix = nowUnix;
        if (percentPerWeek <= 0 || nowUnix - crew.LastDecayUnix < week) return crew.LastDecayUnix;
        var weeks = (nowUnix - crew.LastDecayUnix) / week;
        var keep = Math.Pow(1.0 - Math.Clamp(percentPerWeek, 0, 100) / 100.0, weeks);
        crew.Respect = (int)Math.Floor(crew.Respect * keep);
        crew.LastDecayUnix += weeks * week;
        return crew.LastDecayUnix;
    }

    /// <summary>Respect a member can still earn this round under the per-round cap.</summary>
    public static int Grantable(int earnedThisRound, int amount, int roundCap) =>
        amount <= 0 ? amount : Math.Max(0, Math.Min(amount, roundCap - earnedThisRound));

    // ------------------------------------------------------------------ turf

    /// <summary>
    /// Who holds a cell block after the cells open: the crew with the most living members inside it, at least
    /// <paramref name="minMembers"/>. A tie for the most keeps the current holder if it is part of the tie, otherwise
    /// nothing changes.
    /// </summary>
    public static string? ResolveTurf(IReadOnlyDictionary<string, int> presence, string? holder, int minMembers)
    {
        var best = presence.Where(kv => kv.Value >= Math.Max(1, minMembers)).OrderByDescending(kv => kv.Value).ToList();
        if (best.Count == 0) return holder;
        var top = best[0].Value;
        var tied = best.Where(kv => kv.Value == top).Select(kv => kv.Key).ToList();
        return tied.Count == 1 ? tied[0] : holder;
    }

    // ------------------------------------------------------------------ favors

    public static readonly CrewFavor[] Favors =
    {
        new("banner", "Crew Banner", 10, 1, "Raise a beam pillar in the crew colour where you stand (rendezvous point) for the round."),
        new("colors", "Crew Colours", 15, 2, "Crew members wear the crew colour this round (easier to find, also for the guards)."),
        new("lookout", "Lookout", 25, 3, "Crewmates see each other through walls for 8 seconds."),
        new("rollcall", "Roll Call", 20, 4, "For 30 seconds every crewmate sees where the others are (named zones / cell blocks) and who is down.")
    };

    public static CrewFavor? Favor(string key) => Favors.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>8 lower-case hex characters, collision-checked by the caller.</summary>
    public static string NewId(Random rng) => rng.Next().ToString("x8");
}

/// <summary>What other modules may ask about crews.</summary>
public interface ICrewService
{
    bool Available { get; }

    /// <summary>Crew of a player (SteamID64), or null.</summary>
    Crew? CrewOf(ulong steamId64);

    bool SameCrew(CCSPlayerController a, CCSPlayerController b);
}

public sealed class NullCrewService : ICrewService
{
    public static readonly NullCrewService Instance = new();
    public bool Available => false;
    public Crew? CrewOf(ulong steamId64) => null;
    public bool SameCrew(CCSPlayerController a, CCSPlayerController b) => false;
}
