// Regenerates the README command list: dotnet build src/SwbsJailbreak && dotnet run --project tools/command-list -- commands.md
using System.Reflection;
using System.Text;
using SwbsJailbreak.Core;
var asm = typeof(SwbsJailbreak.JailbreakPlugin).Assembly;
var catalog = asm.GetType("SwbsJailbreak.Modules.Extras.CommandCatalog")!;
var build = catalog.GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
var types = asm.GetTypes().Where(t => typeof(JbModule).IsAssignableFrom(t) && !t.IsAbstract).OrderBy(t => t.Namespace).ThenBy(t => t.Name).ToList();
types.Insert(0, typeof(SwbsJailbreak.JailbreakPlugin));

string Tier(string? cap, string[] all, string[] any)
{
    var flags = new List<string>();
    if (cap != null) flags.Add(Perms.Defaults.TryGetValue(cap, out var f) ? f : cap);
    flags.AddRange(all); if (any.Length > 0) flags.Add(any[0]);
    if (flags.Count == 0) return "Everyone";
    if (flags.Any(f => f == "@css/root" || f == "@css/rcon")) return "Root";
    foreach (var l in AdminLevels.All) if (flags.All(f => l.Flags.Contains(f))) return l.Name;
    return "Root";
}

string Clean(string d)
{
    d = d.Trim();
    // "sm_bhc <#userid|name> - bhop log" -> "<#userid|name> - bhop log" style: drop the leading sm_/css_ name
    var m = System.Text.RegularExpressions.Regex.Match(d, @"^(?:sm_|css_)\S+\s*(.*)$");
    if (m.Success) d = m.Groups[1].Value.Trim();
    if (d.StartsWith("- ")) d = d[2..];
    return d.Replace("|", "\\|").Replace("<", "&lt;").Replace(">", "&gt;");
}

static string Section(Type t)
{
    var ns = t.Namespace ?? "";
    var part = ns.Split('.').Last();
    return t == typeof(SwbsJailbreak.JailbreakPlugin) ? "Core" : part switch
    {
        "LastRequest" => "Last requests", "LastGuard" => "Last guard", "PlayerExtras" => "Store, points and player extras",
        "WeaponSelection" => "Weapons", "Extras" => "Extras", "Warden" => "Warden", "Days" => "Days", "Games" => "Warden games",
        "Admin" => "Admin", "Cells" => "Cells", "Zones" => "Zones", "Crews" => "Crews", "Teams" => "Teams", "Mute" => "Voice",
        "Commands" => "General commands", "Settings" => "Settings", "Jihad" => "Jihad", "Logger" => "Logs", "Effects" => "Effects",
        _ => part
    };
}

var sb = new StringBuilder();
var bySection = new SortedDictionary<string, List<(string name, string aliases, string who, string desc)>>();
var total = 0;
foreach (var t in types)
{
    var list = (System.Collections.IEnumerable)build.Invoke(null, new object[] { new[] { t } })!;
    foreach (var e in list)
    {
        var et = e.GetType();
        string F(string n) => (string)(et.GetField(n)!.GetValue(e) ?? "");
        var name = F("Name");
        var aliases = (string[])et.GetField("Aliases")!.GetValue(e)!;
        var cap = (string?)et.GetField("Capability")!.GetValue(e);
        var all = (string[])et.GetField("AllFlags")!.GetValue(e)!;
        var any = (string[])et.GetField("AnyFlags")!.GetValue(e)!;
        var server = (bool)et.GetField("ServerOnly")!.GetValue(e)!;
        var who = server ? "Server console" : Tier(cap, all, any);
        var sec = Section(t);
        if (!bySection.TryGetValue(sec, out var rows)) bySection[sec] = rows = new();
        rows.Add(("!" + name, string.Join(", ", aliases.Select(a => "!" + a)), who, Clean(F("Description"))));
        total++;
    }
}
sb.AppendLine("## Command list");
sb.AppendLine();
sb.AppendLine($"Every command of the plugin ({total} commands, generated from the code). Type them in chat with `!` (or `/` to hide the message) or in the console with `css_` (`!bhc` = `css_bhc`). **Who** is the lowest admin tier that can use the command (see Admin tiers above); \"Everyone\" commands may still be limited to the warden, guards or prisoners by the game rules.");
sb.AppendLine();
foreach (var (sec, rows) in bySection)
{
    sb.AppendLine($"<details><summary><b>{sec}</b> ({rows.Count})</summary>");
    sb.AppendLine();
    sb.AppendLine("| Command | Aliases | Who | Description |");
    sb.AppendLine("|---|---|---|---|");
    foreach (var r in rows.OrderBy(r => r.name)) sb.AppendLine($"| `{r.name}` | {(r.aliases.Length > 0 ? r.aliases : "")} | {r.who} | {r.desc} |");
    sb.AppendLine();
    sb.AppendLine("</details>");
    sb.AppendLine();
}
File.WriteAllText(args[0], sb.ToString());
Console.WriteLine($"{total} commands in {bySection.Count} sections");
