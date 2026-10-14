using System.Net.Http;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Admin;

/// <summary>Section "AdminActivity" (SMPlugins LogAdminActivity.sp).</summary>
public sealed class AdminActivityConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Days of daily files kept under data/admin_activity/ (0 = keep forever).</summary>
    public int KeepDays { get; set; } = 60;

    /// <summary>Optional Discord webhook URL; admin actions are posted there in batches. Empty = off.</summary>
    public string DiscordWebhookUrl { get; set; } = "";

    /// <summary>Seconds between Discord posts (Discord rate-limits webhooks).</summary>
    public float DiscordBatchSeconds { get; set; } = 10f;
}

/// <summary>
/// Port of LogAdminActivity.sp. The original wrote every admin action (OnLogAction) to the gs_admin_activity MySQL
/// table for the website. Here every admin-type log event from any module (Jb.Log LogType.Admin: bans, kicks, slays,
/// CT bans, warden removals, !fkslay, ...) is appended to a daily file data/admin_activity/yyyy-MM-dd.log with map,
/// admin and target SteamIDs, optionally posted to a Discord webhook, and readable in game with !adminlog.
/// </summary>
public sealed class AdminActivityModule : JbModule
{
    private AdminActivityConfig Cfg => Section<AdminActivityConfig>("AdminActivity");

    public override string Name => "AdminActivity";
    public override int Order => 2;
    public override bool Enabled => Cfg.Enabled;

    private static AdminActivityModule? _instance;
    private string _dir = "";
    private readonly Queue<string> _discord = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public override void OnLoad(bool hotReload)
    {
        _instance = this;
        _dir = Path.Combine(Storage.DataDirectory, "admin_activity");
        try
        {
            Directory.CreateDirectory(_dir);
            Prune();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[AdminActivity] cannot use {Dir}", _dir);
        }

        if (!string.IsNullOrWhiteSpace(Cfg.DiscordWebhookUrl))
            Plugin.AddTimer(Math.Max(3f, Cfg.DiscordBatchSeconds), FlushDiscord, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
    }

    public override void OnUnload(bool hotReload)
    {
        FlushDiscord();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    /// <summary>Called by the logger for every LogType.Admin event. No-op while this module is off.</summary>
    internal static void Record(string message, JbPlayer? admin, JbPlayer? target) => _instance?.Write(message, admin, target);

    /// <summary>Engine-free (unit-tested): one log line.</summary>
    internal static string Format(DateTime utc, string map, string message, string? admin, ulong adminId, string? target, ulong targetId)
    {
        var sb = new StringBuilder();
        sb.Append(utc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).Append("Z [").Append(map).Append("] ");
        sb.Append(Colors.Strip(message).Replace('\n', ' ').Replace('\r', ' '));
        if (admin != null) sb.Append(" | by ").Append(admin).Append(" (").Append(adminId == 0 ? "console" : adminId.ToString()).Append(')');
        if (target != null) sb.Append(" | target ").Append(target).Append(" (").Append(targetId == 0 ? "bot" : targetId.ToString()).Append(')');
        return sb.ToString();
    }

    private void Write(string message, JbPlayer? admin, JbPlayer? target)
    {
        string map;
        try { map = Server.MapName ?? "?"; }
        catch { map = "?"; }
        var line = Format(DateTime.UtcNow, map, message, admin?.Name, admin?.SteamId64 ?? 0, target?.Name, target?.SteamId64 ?? 0);
        try
        {
            File.AppendAllText(Path.Combine(_dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log"), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[AdminActivity] could not write the log");
        }

        if (!string.IsNullOrWhiteSpace(Cfg.DiscordWebhookUrl))
        {
            _discord.Enqueue(line);
            while (_discord.Count > 200) _discord.Dequeue();
        }
    }

    private void Prune()
    {
        if (Cfg.KeepDays <= 0) return;
        var cutoff = DateTime.UtcNow.Date.AddDays(-Cfg.KeepDays);
        foreach (var f in Directory.GetFiles(_dir, "*.log"))
            if (DateTime.TryParse(Path.GetFileNameWithoutExtension(f), out var day) && day < cutoff)
                File.Delete(f);
    }

    private void FlushDiscord()
    {
        var url = Cfg.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url) || _discord.Count == 0) return;
        var sb = new StringBuilder();
        while (_discord.Count > 0 && sb.Length + _discord.Peek().Length < 1800) sb.AppendLine(_discord.Dequeue());
        if (sb.Length == 0)
        {
            var first = _discord.Dequeue(); // one line longer than a Discord message: send it cut
            sb.AppendLine(first[..Math.Min(1800, first.Length)]);
        }
        var body = JsonSerializer.Serialize(new { content = "```\n" + sb + "```", allowed_mentions = new { parse = Array.Empty<string>() } });
        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                await Http.PostAsync(url, content);
            }
            catch
            {
                // Discord down or URL wrong: the file log still has everything
            }
        });
    }

    // ================================================================= !adminlog

    [ConsoleCommand("css_adminlog", "sm_adminlog [lines] [filter] - recent admin activity (console)")]
    public void CmdAdminLog(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "ban")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        var count = 30;
        var filter = "";
        if (info.ArgCount > 1 && !int.TryParse(info.GetArg(1), out count)) { filter = info.GetArg(1); count = 30; }
        if (info.ArgCount > 2) filter = info.GetArg(2);
        count = Math.Clamp(count, 1, 200);

        var lines = new List<string>();
        foreach (var day in new[] { DateTime.UtcNow.Date.AddDays(-1), DateTime.UtcNow.Date })
        {
            var f = Path.Combine(_dir, day.ToString("yyyy-MM-dd") + ".log");
            if (File.Exists(f)) lines.AddRange(File.ReadAllLines(f));
        }

        var pick = lines.Where(l => filter.Length == 0 || l.Contains(filter, StringComparison.OrdinalIgnoreCase)).TakeLast(count).ToList();
        foreach (var l in pick) Chat.PrintToConsole(caller, l);
        Chat.ReplyT(caller, info, pick.Count == 0 ? "adminlog.none" : "adminlog.printed", pick.Count);
    }
}
