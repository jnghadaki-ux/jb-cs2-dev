namespace SwbsJailbreak.Core;

/// <summary>Whether an admin turned a day or last request off, and where.</summary>
public enum Availability
{
    On,
    OffThisMap,
    OffEverywhere
}

/// <summary>A list of disabled names (maps/&lt;kind&gt;/&lt;map&gt;.json, and maps/&lt;kind&gt;/_global.json for every map).</summary>
public sealed class DisabledListDoc
{
    public List<string> Disabled { get; set; } = new();
}

/// <summary>
/// Engine-free (unit-tested) rules of the admin on/off menus (!daysedit, !lredit): each click moves an entry
/// On -> off on this map -> off on every map -> On. "Off everywhere" wins over the map list.
/// </summary>
public static class AvailabilityRules
{
    /// <summary>Doc name of the list that applies to every map.</summary>
    public const string GlobalDoc = "_global";

    public static Availability Of(ICollection<string> mapOff, ICollection<string> globalOff, string key) =>
        globalOff.Contains(key) ? Availability.OffEverywhere : mapOff.Contains(key) ? Availability.OffThisMap : Availability.On;

    public static Availability Next(Availability a) => a switch
    {
        Availability.On => Availability.OffThisMap,
        Availability.OffThisMap => Availability.OffEverywhere,
        _ => Availability.On
    };

    /// <summary>Put <paramref name="key"/> in the lists so it reads as <paramref name="a"/>.</summary>
    public static void Apply(List<string> mapOff, List<string> globalOff, string key, Availability a)
    {
        mapOff.RemoveAll(k => k == key);
        globalOff.RemoveAll(k => k == key);
        if (a == Availability.OffThisMap) mapOff.Add(key);
        else if (a == Availability.OffEverywhere) globalOff.Add(key);
    }

    /// <summary>Phrase key of the menu tag ("[ON]", "[OFF - this map]", "[OFF - all maps]").</summary>
    public static string TagKey(Availability a) => a switch
    {
        Availability.OffThisMap => "toggle.off_map",
        Availability.OffEverywhere => "toggle.off_all",
        _ => "toggle.on"
    };
}
