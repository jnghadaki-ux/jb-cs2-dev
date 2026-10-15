using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

public sealed partial class AdminModule
{
    /// <summary>configs/admin_groups.json of CounterStrikeSharp (two folders above the plugin's config folder).</summary>
    private string AdminGroupsPath => Path.GetFullPath(Path.Combine(Plugin.ConfigDirectory, "..", "..", "admin_groups.json"));

    /// <summary>Adds the SWBS admin tiers to admin_groups.json when they are missing (existing groups are never changed).</summary>
    private void InstallAdminLevels()
    {
        if (!Cfg.InstallAdminLevels) return;
        try
        {
            var path = AdminGroupsPath;
            var text = File.Exists(path) ? File.ReadAllText(path) : null;
            var merged = AdminLevels.MergeInto(text);
            if (merged == null) return;
            File.WriteAllText(path, merged);
            Logger.LogInformation("[Admin] added the SWBS admin groups (#swbs/level1, level2, level3, lead) to {Path}; restart the server to load them", path);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Admin] could not add the SWBS admin groups to admin_groups.json");
        }
    }

    [ConsoleCommand("css_adminlevels", "sm_adminlevels - the SWBS admin tiers and what each can do")]
    public void CmdAdminLevels(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Require(caller, info, "admin")) return;
        Chat.ReplyT(caller, info, "admin.levels.title");
        foreach (var l in AdminLevels.All)
            Chat.ReplyT(caller, info, "admin.levels.line", l.Name, l.Group, l.Immunity, l.Summary);
        Chat.ReplyT(caller, info, "admin.levels.root");
    }

    [ConsoleCommand("css_mylevel", "sm_mylevel - your admin tier")]
    public void CmdMyLevel(CCSPlayerController? caller, CommandInfo info)
    {
        var level = AdminLevels.NameOf(caller);
        if (level == null) { Chat.ReplyT(caller, info, "admin.levels.none"); return; }
        Chat.ReplyT(caller, info, "admin.levels.mine", level);
    }
}
