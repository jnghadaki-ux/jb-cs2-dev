using System.Reflection;
using System.Reflection.Emit;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "CommandList".</summary>
public sealed class CommandListConfig
{
    public bool Enabled { get; set; } = true;
}

/// <summary>One chat command of the plugin: its names, help text and the permission it is gated by (if any).</summary>
internal sealed class CommandEntry
{
    public required string Name;                        // "beacon" (chat form is "!beacon")
    public string[] Aliases = Array.Empty<string>();
    public string Description = "";
    public string? Capability;                          // Perms capability / flag found in the command body ("slay", "@css/ban")
    public string[] AllFlags = Array.Empty<string>();   // [RequiresPermissions]
    public string[] AnyFlags = Array.Empty<string>();   // [RequiresPermissionsOr]
    public bool ServerOnly;

    public bool IsGated => Capability != null || AllFlags.Length > 0 || AnyFlags.Length > 0;

    public bool CanUse(CCSPlayerController? player)
    {
        if (player == null) return true; // server console may run everything
        if (ServerOnly) return false;
        if (Capability != null && !Perms.Has(player, Capability)) return false;
        if (AllFlags.Length > 0 && !AllFlags.All(f => Perms.Has(player, f))) return false;
        if (AnyFlags.Length > 0 && !AnyFlags.Any(f => Perms.Has(player, f))) return false;
        return true;
    }

    /// <summary>"!beacon (userid|name) [0/1] - toggle a beacon ring" (no angle brackets: safe in the centre-HTML menu).</summary>
    public string Display()
    {
        var text = Description.Trim();
        // Descriptions start with the original usage ("sm_beacon <#userid|name> [0/1] - ..."): keep it, but show the chat form.
        foreach (var prefix in new[] { "css_" + Name, "sm_" + Name, "!" + Name })
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            text = "!" + Name + text[prefix.Length..];
            return Sanitize(text);
        }

        return Sanitize(text.Length == 0 ? "!" + Name : "!" + Name + " - " + text);
    }

    private static string Sanitize(string s) => s.Replace('<', '(').Replace('>', ')').Replace("&", "and");
}

/// <summary>
/// Builds the command catalogue by reflection (engine-free, unit-tested) from the plugin's [ConsoleCommand] methods.
/// Permission gates are read from [RequiresPermissions]/[RequiresPermissionsOr] and, because this plugin checks most
/// permissions in code (Require(caller, info, "slay")), from the first capability string literal in the command body.
/// A command whose check lives in a helper or a lambda is listed as open: harmless, the command still enforces it.
/// </summary>
internal static class CommandCatalog
{
    public static List<CommandEntry> Build(IEnumerable<Type> types)
    {
        var entries = new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attrs = method.GetCustomAttributes<ConsoleCommandAttribute>(false).ToList();
                if (attrs.Count == 0) continue;
                var names = attrs.Select(a => ChatName(a.Command)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var description = attrs.Select(a => a.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "";
                var entry = new CommandEntry
                {
                    Name = names[0],
                    Aliases = names.Skip(1).ToArray(),
                    Description = description,
                    Capability = method.IsDefined(typeof(OpenCommandAttribute), false) ? null : FindCapability(method),
                    ServerOnly = method.GetCustomAttributes<CommandHelperAttribute>(false).Any(h => h.WhoCanExcecute == CommandUsage.SERVER_ONLY)
                };
                var all = method.GetCustomAttributes<RequiresPermissions>(false).SelectMany(a => a.Permissions).ToArray();
                var any = method.GetCustomAttributes<RequiresPermissionsOr>(false).SelectMany(a => a.Permissions).ToArray();
                entry.AllFlags = all;
                entry.AnyFlags = any;
                entries.TryAdd(entry.Name, entry);
            }
        }

        return entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ChatName(string command) => command.StartsWith("css_", StringComparison.OrdinalIgnoreCase) ? command[4..] : command;

    // -------------------------------------------------------------------------------- IL scan for the gate literal

    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static CommandCatalog()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = (ushort)op.Value;
            if (v < 0x100) OneByte[v] = op;
            else if ((v & 0xFF00) == 0xFE00) TwoByte[v & 0xFF] = op;
        }
    }

    /// <summary>
    /// First string literal that names a Perms capability or a CSS flag/group, in IL order, in the method body and in
    /// the helper methods of the same class it calls (thin wrappers like CmdMute => CommCommand(...) keep the check in
    /// the helper) and Perms.IsAdmin. Lambdas and other classes are not followed.
    /// </summary>
    internal static string? FindCapability(MethodBase method) => FindCapability(method, new HashSet<MethodBase>(), 0);

    private static string? FindCapability(MethodBase method, HashSet<MethodBase> visited, int depth)
    {
        if (!visited.Add(method)) return null;
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { return null; }
        if (il == null) return null;

        var module = method.Module;
        var i = 0;
        while (i < il.Length)
        {
            var op = il[i] == 0xFE && i + 1 < il.Length ? TwoByte[il[i + 1]] : OneByte[il[i]];
            i += il[i] == 0xFE ? 2 : 1;
            int size;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch:
                    if (i + 4 > il.Length) return null;
                    size = 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                default: size = 4; break;
            }

            if (i + 4 <= il.Length && op.OperandType == OperandType.InlineString)
            {
                string? s;
                try { s = module.ResolveString(BitConverter.ToInt32(il, i)); }
                catch { s = null; }
                if (s != null && IsCapability(s) && !IsComparedString(module, il, i + 4)) return s;
            }
            else if (depth < 3 && i + 4 <= il.Length && op.OperandType == OperandType.InlineMethod)
            {
                MethodBase? callee;
                try { callee = module.ResolveMethod(BitConverter.ToInt32(il, i)); }
                catch { callee = null; }
                if (callee != null && callee.DeclaringType != null && !ReferenceEquals(callee, method)
                    && (callee.DeclaringType == method.DeclaringType
                        || (callee.DeclaringType == typeof(Perms) && callee.Name == nameof(Perms.IsAdmin))))
                {
                    var found = FindCapability(callee, visited, depth + 1);
                    if (found != null) return found;
                }
            }

            i += size;
        }

        return null;
    }

    /// <summary>
    /// A literal that is only compared (a subcommand switch: <c>case "kick":</c> compiles to ldstr + call string.op_Equality)
    /// is not a permission check, even when it spells a capability name.
    /// </summary>
    private static bool IsComparedString(Module module, byte[] il, int next)
    {
        if (next + 5 > il.Length || il[next] is not (0x28 or 0x6F)) return false; // call / callvirt
        try
        {
            var m = module.ResolveMethod(BitConverter.ToInt32(il, next + 1));
            return m?.DeclaringType == typeof(string) && m.Name is "op_Equality" or "op_Inequality" or "Equals" or "StartsWith";
        }
        catch { return false; }
    }

    private static bool IsCapability(string s) =>
        // Ordinal: capability keys are lowercase; "Days"/"Teams" are config section names, not permissions.
        (s.Length > 0 && char.IsLower(s[0]) && Perms.Defaults.ContainsKey(s)) || s.StartsWith("@css/", StringComparison.Ordinal) || s.StartsWith("#css/", StringComparison.Ordinal);
}

/// <summary>
/// SMPlugins CommandList (sm_commands): a menu of every command the player may use. CS2 version: !commands opens the
/// list as a menu (6 per page); "!commands console" (or typing css_commands in the console) prints the whole list to
/// the player's own console, which is easier to read for admins who have ~100 commands.
/// </summary>
public sealed class CommandListModule : JbModule
{
    private CommandListConfig Cfg => Section<CommandListConfig>("CommandList");

    public override string Name => "CommandList";
    public override int Order => 130;
    public override bool Enabled => Cfg.Enabled;

    private List<CommandEntry>? _catalogue;

    public override void OnUnload(bool hotReload) => _catalogue = null;

    /// <summary>Built on first use, once every module is loaded (disabled modules register no commands).</summary>
    private List<CommandEntry> Catalogue()
    {
        if (_catalogue != null) return _catalogue;
        var types = Plugin.Modules.Select(m => m.GetType()).Append(typeof(JailbreakPlugin)).Distinct();
        _catalogue = CommandCatalog.Build(types);
        return _catalogue;
    }

    [ConsoleCommand("css_commands", "sm_commands - list the commands you can use (!commands console prints them to your console)")]
    public void CmdCommands(CCSPlayerController? caller, CommandInfo info)
    {
        var usable = Catalogue().Where(e => e.CanUse(caller)).ToList();
        if (usable.Count == 0)
        {
            Chat.ReplyT(caller, info, "cmdlist.none");
            return;
        }

        var toConsole = caller == null
                        || info.CallingContext == CommandCallingContext.Console
                        || (info.ArgCount > 1 && info.GetArg(1).Equals("console", StringComparison.OrdinalIgnoreCase));
        if (toConsole)
        {
            PrintToConsole(caller, usable);
            if (caller != null && info.CallingContext != CommandCallingContext.Console)
                Chat.Tell(caller, "cmdlist.console", usable.Count);
            return;
        }

        var menu = new JbMenu(Lang.T("cmdlist.title")) { Timeout = -1, ExitButton = true, Subtitle = Lang.T("cmdlist.hint") };
        foreach (var e in usable) menu.AddInfo("<font class='fontSize-s'>" + e.Display() + "</font>");
        menu.Open(caller);
    }

    private static void PrintToConsole(CCSPlayerController? caller, List<CommandEntry> usable)
    {
        void Line(string text)
        {
            if (caller == null) Server.PrintToConsole(text);
            else caller.PrintToConsole(text);
        }

        Line($"---- {Colors.Strip(Lang.T("cmdlist.title"))} ({usable.Count}) ----");
        foreach (var e in usable)
        {
            var aliases = e.Aliases.Length > 0 ? "  (also: " + string.Join(", ", e.Aliases.Select(a => "!" + a)) + ")" : "";
            Line(e.Display() + aliases);
        }
    }
}
