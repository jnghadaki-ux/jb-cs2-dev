using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using SwbsJailbreak.Core;

namespace SwbsJailbreak.Modules.Extras;

/// <summary>Section "Demos": record every game with SourceTV, keep its console log, store and upload both.</summary>
public sealed class DemoConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Folder under game/csgo the demos are written to.</summary>
    public string Folder { get; set; } = "demos";
    /// <summary>Short server name used in file names and uploads (empty = hostname).</summary>
    public string ServerName { get; set; } = "";
    /// <summary>Seconds after a map starts before recording begins (SourceTV must be in the server).</summary>
    public float StartDelaySeconds { get; set; } = 10f;
    /// <summary>Delete a game's demo when no human player was on during it.</summary>
    public bool DeleteEmptyGames { get; set; } = true;
    /// <summary>Write a .log next to each demo (chat, kills, rounds, jail log lines, all stamped with the demo time).</summary>
    public bool WriteLogFile { get; set; } = true;
    /// <summary>Local demos older than this are deleted (0 = keep forever).</summary>
    public int KeepDays { get; set; } = 14;
    /// <summary>Do not start a recording when the disk has less free space than this.</summary>
    public double MinFreeDiskGb { get; set; } = 2;
    /// <summary>
    /// Demo database / storage: each finished game (demo + log, zipped) is POSTed here as multipart/form-data with
    /// server, map, started, ended, duration and players fields. Empty = keep the demos on the server only.
    /// </summary>
    public string UploadUrl { get; set; } = "";
    /// <summary>Sent as "Authorization: Bearer &lt;token&gt;" with uploads.</summary>
    public string UploadToken { get; set; } = "";
    /// <summary>Delete the local files once an upload succeeded.</summary>
    public bool DeleteAfterUpload { get; set; } = false;
}

/// <summary>Demo naming and housekeeping rules (engine-free, unit-tested).</summary>
internal static class DemoRules
{
    /// <summary>"swbs1_jb_san_palmora_2026-10-16_21-04-05": safe for file names and tv_record.</summary>
    public static string FileName(string server, string map, DateTime utc)
    {
        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s.Trim()) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? char.ToLowerInvariant(ch) : '_');
            var r = sb.ToString().Trim('_');
            while (r.Contains("__")) r = r.Replace("__", "_");
            return r.Length > 0 ? r : "server";
        }

        return $"{Clean(server)}_{Clean(map)}_{utc:yyyy-MM-dd_HH-mm-ss}";
    }

    /// <summary>Demo time: "12:41" or "1:02:03".</summary>
    public static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static bool Expired(DateTime lastWriteUtc, DateTime nowUtc, int keepDays) => keepDays > 0 && nowUtc - lastWriteUtc > TimeSpan.FromDays(keepDays);
}

/// <summary>One finished game in demos/index.json.</summary>
internal sealed class DemoEntry
{
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public List<string> Players { get; set; } = new();
    public long Bytes { get; set; }
    public bool Uploaded { get; set; }
}

/// <summary>
/// Records every game with SourceTV (tv_record at map start, stop at the end of the game), the way the SMPlugins servers
/// did: the demo plays in CS2's own viewer (playdemo), the jail log is printed into it every round by the logger, and a
/// .log with chat, kills, rounds and jail log lines (stamped with the demo time) is saved next to it. Finished games are
/// zipped and uploaded to the demo database (Demos.UploadUrl); old local demos are cleaned up.
/// </summary>
public sealed class DemoModule : JbModule
{
    private DemoConfig Cfg => Section<DemoConfig>("Demos");
    public override string Name => "Demos";
    public override int Order => 96;
    public override bool Enabled => Cfg.Enabled;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private string? _current;
    private string _map = "";
    private DateTime _startedUtc;
    private float _startTime;
    private readonly List<string> _log = new();
    private readonly HashSet<string> _players = new();
    private bool _stopping;

    private string CsgoDir => Path.GetFullPath(Path.Combine(Plugin.ModuleDirectory, "..", "..", "..", ".."));
    private string DemoDir => Path.Combine(CsgoDir, Cfg.Folder.Trim().Trim('/', '\\'));

    public override void OnLoad(bool hotReload)
    {
        global::SwbsJailbreak.Modules.Logger.LoggerModule.LineLogged += OnJailLog;
        Plugin.AddCommandListener("say", OnSay, HookMode.Post);
        Plugin.AddCommandListener("say_team", OnSay, HookMode.Post);
    }

    public override void OnUnload(bool hotReload)
    {
        global::SwbsJailbreak.Modules.Logger.LoggerModule.LineLogged -= OnJailLog;
        StopRecording(true);
    }

    public override void OnMapStart(string mapName)
    {
        _map = mapName;
        Cleanup();
        Plugin.AddTimer(Math.Clamp(Cfg.StartDelaySeconds, 2f, 60f), StartRecording, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    public override void OnMapEnd() => StopRecording(true);

    /// <summary>The game is over (end-of-match scoreboard): keep recording a few seconds of it, then stop.</summary>
    [GameEventHandler]
    public HookResult OnMatchEnd(EventCsWinPanelMatch ev, GameEventInfo info)
    {
        Line("=== game over ===");
        Plugin.AddTimer(5f, () => StopRecording(false), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    // ================================================================= recording

    private void StartRecording()
    {
        if (_current != null) return;
        if (Cvars.Get("tv_enable") is not ("1" or "true"))
        {
            Logger.LogWarning("[Demos] SourceTV is off: put \"tv_enable 1\" in server.cfg and restart the server (it cannot start mid-map). Setting it now for the next restart.");
            Server.ExecuteCommand("tv_enable 1");
            return;
        }

        try
        {
            Directory.CreateDirectory(DemoDir);
            var free = new DriveInfo(Path.GetPathRoot(DemoDir) ?? "/").AvailableFreeSpace / 1024d / 1024d / 1024d;
            if (free < Cfg.MinFreeDiskGb) { Logger.LogWarning("[Demos] only {Free:0.0} GB free: not recording", free); return; }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Demos] demo folder {Dir} not usable", DemoDir);
            return;
        }

        var server = Cfg.ServerName.Trim().Length > 0 ? Cfg.ServerName : Cvars.Get("hostname") ?? "server";
        _startedUtc = DateTime.UtcNow;
        _current = DemoRules.FileName(server, _map, _startedUtc);
        _startTime = Server.CurrentTime;
        _log.Clear();
        _players.Clear();
        _stopping = false;
        foreach (var c in Players.Humans()) _players.Add($"{c.PlayerName} ({c.SteamID})");
        Server.ExecuteCommand("tv_stoprecord");
        Server.ExecuteCommand($"tv_record \"{Cfg.Folder.Trim().Trim('/', '\\')}/{_current}\"");
        Line($"=== recording {_current}.dem on {_map} ===");
        Logger.LogInformation("[Demos] recording {Demo}", _current);
    }

    private void StopRecording(bool mapEnding)
    {
        if (_current == null || _stopping) return;
        _stopping = true;
        Server.ExecuteCommand("tv_stoprecord");
        var name = _current;
        var entry = new DemoEntry { Name = name, Map = _map, StartedUtc = _startedUtc, EndedUtc = DateTime.UtcNow, Players = _players.ToList() };
        var log = _log.ToList();
        var dir = DemoDir;
        _current = null;
        Logger.LogInformation("[Demos] stopped {Demo} ({Players} players)", name, entry.Players.Count);
        // The demo file is closed by the engine; finish the rest off the game thread.
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Finish(dir, entry, log);
        });
    }

    private async Task Finish(string dir, DemoEntry entry, List<string> log)
    {
        var dem = Path.Combine(dir, entry.Name + ".dem");
        try
        {
            if (!File.Exists(dem)) { Log(l => l.LogWarning("[Demos] {Demo} was not written (is SourceTV in the server?)", dem)); return; }
            if (entry.Players.Count == 0 && Cfg.DeleteEmptyGames) { File.Delete(dem); return; }
            var logPath = Path.Combine(dir, entry.Name + ".log");
            if (Cfg.WriteLogFile) await File.WriteAllLinesAsync(logPath, log);
            entry.Bytes = new FileInfo(dem).Length;

            if (Cfg.UploadUrl.Trim().Length > 0)
            {
                var zip = Path.Combine(dir, entry.Name + ".zip");
                using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    z.CreateEntryFromFile(dem, entry.Name + ".dem", CompressionLevel.Optimal);
                    if (File.Exists(logPath)) z.CreateEntryFromFile(logPath, entry.Name + ".log", CompressionLevel.Optimal);
                }

                entry.Uploaded = await Upload(zip, entry);
                File.Delete(zip);
                if (entry.Uploaded && Cfg.DeleteAfterUpload) { File.Delete(dem); if (File.Exists(logPath)) File.Delete(logPath); }
            }

            AddToIndex(dir, entry);
        }
        catch (Exception ex)
        {
            Log(l => l.LogError(ex, "[Demos] finishing {Demo} failed", entry.Name));
        }
    }

    private async Task<bool> Upload(string zip, DemoEntry entry)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(Cfg.ServerName.Trim().Length > 0 ? Cfg.ServerName : "server"), "server");
            form.Add(new StringContent(entry.Map), "map");
            form.Add(new StringContent(entry.Name), "name");
            form.Add(new StringContent(entry.StartedUtc.ToString("o")), "started");
            form.Add(new StringContent(entry.EndedUtc.ToString("o")), "ended");
            form.Add(new StringContent(((int)(entry.EndedUtc - entry.StartedUtc).TotalSeconds).ToString()), "duration");
            form.Add(new StringContent(JsonSerializer.Serialize(entry.Players)), "players");
            var file = new StreamContent(File.OpenRead(zip));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "file", entry.Name + ".zip");
            using var req = new HttpRequestMessage(HttpMethod.Post, Cfg.UploadUrl.Trim()) { Content = form };
            if (Cfg.UploadToken.Trim().Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Cfg.UploadToken.Trim());
            using var res = await Http.SendAsync(req);
            if (res.IsSuccessStatusCode) { Log(l => l.LogInformation("[Demos] uploaded {Demo}", entry.Name)); return true; }
            Log(l => l.LogWarning("[Demos] upload of {Demo} failed: HTTP {Status}", entry.Name, (int)res.StatusCode));
        }
        catch (Exception ex)
        {
            Log(l => l.LogWarning("[Demos] upload of {Demo} failed: {Error}", entry.Name, ex.Message));
        }

        return false;
    }

    private static readonly object IndexLock = new();

    private static void AddToIndex(string dir, DemoEntry entry)
    {
        lock (IndexLock)
        {
            var path = Path.Combine(dir, "index.json");
            List<DemoEntry> list;
            try { list = File.Exists(path) ? JsonSerializer.Deserialize<List<DemoEntry>>(File.ReadAllText(path)) ?? new() : new(); }
            catch { list = new(); }
            list.Add(entry);
            File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Delete local demos / logs older than KeepDays.</summary>
    private void Cleanup()
    {
        if (Cfg.KeepDays <= 0 || !Directory.Exists(DemoDir)) return;
        var now = DateTime.UtcNow;
        var removed = 0;
        foreach (var f in Directory.GetFiles(DemoDir).Where(f => f.EndsWith(".dem") || f.EndsWith(".log") || f.EndsWith(".zip")))
        {
            try
            {
                if (!DemoRules.Expired(File.GetLastWriteTimeUtc(f), now, Cfg.KeepDays)) continue;
                File.Delete(f);
                removed++;
            }
            catch { /* in use */ }
        }

        if (removed > 0) Logger.LogInformation("[Demos] removed {Count} files older than {Days} days", removed, Cfg.KeepDays);
    }

    /// <summary>Log from a background thread on the game thread.</summary>
    private void Log(Action<ILogger> write) => Server.NextFrame(() => write(Logger));

    // ================================================================= the game's .log

    private string Now => DemoRules.Stamp(Server.CurrentTime - _startTime);

    private void Line(string text)
    {
        if (_current == null || !Cfg.WriteLogFile) return;
        _log.Add($"[{Now}] {text}");
    }

    private void OnJailLog(string message, LogType type) => Line($"[{type}] {message}");

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || _current == null) return HookResult.Continue;
        var text = info.GetArg(1).Trim();
        if (text.Length > 0) Line($"{(info.GetArg(0) == "say_team" ? "(team) " : "")}{player.PlayerName}: {text}");
        return HookResult.Continue;
    }

    public override void OnPlayerConnected(JbPlayer player)
    {
        if (player.Controller is not { IsValid: true, IsBot: false } c) return;
        _players.Add($"{c.PlayerName} ({c.SteamID})");
        Line($"{c.PlayerName} ({c.SteamID}) connected");
    }

    public override void OnPlayerDisconnect(JbPlayer player)
    {
        if (player.Controller is { IsValid: true, IsBot: false } c) Line($"{c.PlayerName} disconnected");
    }

    public override void OnRoundStart() => Line("--- round start ---");

    public override void OnRoundEnd(CsTeam winner, int reason) => Line($"--- round end: {winner} won ---");

    public override void OnPlayerDeath(JbPlayer victim, JbPlayer? attacker, EventPlayerDeath ev)
    {
        var v = victim.Controller;
        var a = attacker?.Controller;
        if (v == null) return;
        Line(a == null || a == v ? $"{v.PlayerName} died" : $"{a.PlayerName} killed {v.PlayerName} with {ev.Weapon}{(ev.Headshot ? " (headshot)" : "")}");
    }

    // ================================================================= commands

    [ConsoleCommand("css_demo", "sm_demo - the demo being recorded and the time in it (quote it in reports)")]
    [OpenCommand]
    public void CmdDemo(CCSPlayerController? caller, CommandInfo info)
    {
        if (_current == null) { Chat.ReplyT(caller, info, "demos.not_recording"); return; }
        Chat.ReplyT(caller, info, "demos.current", _current, Now);
    }

    [ConsoleCommand("css_demos", "sm_demos - (admin) the last recorded games (console)")]
    public void CmdDemos(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Perms.Has(caller, "logs")) { Chat.ReplyT(caller, info, "core.no_access"); return; }
        var path = Path.Combine(DemoDir, "index.json");
        List<DemoEntry> list;
        try { list = File.Exists(path) ? JsonSerializer.Deserialize<List<DemoEntry>>(File.ReadAllText(path)) ?? new() : new(); }
        catch { list = new(); }
        Chat.PrintToConsole(caller, $"---- Last games ({Cfg.Folder}/) ----");
        foreach (var e in list.TakeLast(15).Reverse())
            Chat.PrintToConsole(caller, $"{e.StartedUtc:yyyy-MM-dd HH:mm} UTC  {e.Map,-24} {e.Bytes / 1048576.0,6:0.0} MB  {e.Players.Count,2} players  {(e.Uploaded ? "uploaded" : "local")}  {e.Name}.dem");
        if (_current != null) Chat.PrintToConsole(caller, $"recording now: {_current}.dem ({Now})");
        if (caller != null && info.CallingContext == CommandCallingContext.Chat) Chat.Tell(caller, "demos.see_console");
    }
}
