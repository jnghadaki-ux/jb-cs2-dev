using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SwbsJailbreak.Core;

/// <summary>Core / shared settings (section "General").</summary>
public enum MenuStyle { Center, Chat, Both }

public sealed class GeneralConfig
{
    [JsonPropertyName("ChatPrefix")] public string ChatPrefix { get; set; } = "{green}[{lightred}SWBS JB{green}]{default}";

    /// <summary>Phrase folder under lang/ (falls back to en per phrase).</summary>
    [JsonPropertyName("Language")] public string Language { get; set; } = "en";

    /// <summary>Enable W/S cursor + E select + R back for menus (number keys !1-!9 always work).</summary>
    [JsonPropertyName("MenuWasd")] public bool MenuWasd { get; set; } = true;

    [JsonPropertyName("MenuTimeoutSeconds")] public int MenuTimeoutSeconds { get; set; } = 30;

    /// <summary>Also print menus as numbered chat lines (same as MenuStyle "Both").</summary>
    [JsonPropertyName("MenuChatEcho")] public bool MenuChatEcho { get; set; } = false;

    /// <summary>
    /// Where menus are shown: "Center" (centre HUD, W/S/E/R or !1-!9), "Chat" (numbered chat lines, !1-!9 only; use it
    /// if the centre HUD misbehaves on your CS2 build) or "Both".
    /// </summary>
    [JsonPropertyName("MenuStyle")] public MenuStyle MenuStyle { get; set; } = MenuStyle.Both;

    /// <summary>Config migrations already applied (managed by the plugin; do not edit).</summary>
    [JsonPropertyName("ConfigRevision")] public int ConfigRevision { get; set; }

    /// <summary>Capability → CSS permission flag/group (see docs/ADMIN.md).</summary>
    [JsonPropertyName("Permissions")] public Dictionary<string, string> Permissions { get; set; } = new(Perms.Defaults);

    [JsonPropertyName("DataFlushSeconds")] public int DataFlushSeconds { get; set; } = 30;

    [JsonPropertyName("Debug")] public bool Debug { get; set; } = false;

    /// <summary>Stop the centre HUD / menus flashing every second (holds CCSGameRules.GameRestart while no restart is pending).</summary>
    [JsonPropertyName("FixHudFlashing")] public bool FixHudFlashing { get; set; } = true;

    /// <summary>
    /// TEST SERVERS ONLY. CSS admin checks need a Steam-verified SteamID, which never arrives on a server started with
    /// -insecure or without a GSLT (+sv_setsteamaccount). True matches admins.json against the unverified SteamID instead,
    /// which a spoofed client could fake: keep false on any public server.
    /// </summary>
    [JsonPropertyName("TrustUnverifiedSteamIds")] public bool TrustUnverifiedSteamIds { get; set; } = false;
}

/// <summary>
/// Single config file: addons/counterstrikesharp/configs/plugins/SwbsJailbreak/SwbsJailbreak.json.
/// Every module owns one top-level section, created with defaults on first run and re-written on load so new
/// options always appear in the file. Unknown keys are preserved per section only if the section type knows them.
/// </summary>
public sealed class JailbreakConfig
{
    private readonly Dictionary<string, object> _sections = new(StringComparer.OrdinalIgnoreCase);
    private JsonObject _root = new();
    private bool _dirty;

    public string FilePath { get; private set; } = "";

    public GeneralConfig General => Section<GeneralConfig>("General");

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Load(string path, ILogger logger)
    {
        FilePath = path;
        _sections.Clear();
        _root = new JsonObject();
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (node is JsonObject o) _root = o;
            }
            else
            {
                _dirty = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Config {Path} is invalid JSON - using defaults (your file was backed up)", path);
            try { File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); }
            catch { /* ignore */ }
            _root = new JsonObject();
            _dirty = true;
        }
    }

    /// <summary>Get (and create with defaults if missing) a config section.</summary>
    public T Section<T>(string name) where T : class, new()
    {
        if (_sections.TryGetValue(name, out var cached) && cached is T t) return t;
        T value;
        var existing = _root[name];
        try
        {
            value = existing != null ? existing.Deserialize<T>(Options) ?? new T() : new T();
        }
        catch (Exception ex)
        {
            JailbreakPlugin.Instance?.Logger.LogError(ex, "Config section {Section} is invalid - using defaults", name);
            value = new T();
        }

        var normalized = JsonSerializer.SerializeToNode(value, Options);
        if (existing == null || !JsonNode.DeepEquals(existing, normalized))
        {
            _root[name] = normalized;
            _dirty = true;
        }

        _sections[name] = value;
        return value;
    }

    /// <summary>Change a section in code (migrations) and write it back to the file on the next save.</summary>
    public void Update<T>(string name, Action<T> change) where T : class, new()
    {
        var value = Section<T>(name);
        change(value);
        _root[name] = JsonSerializer.SerializeToNode(value, Options);
        _dirty = true;
    }

    public void SaveIfDirty(ILogger logger)
    {
        if (!_dirty || string.IsNullOrEmpty(FilePath)) return;
        try
        {
            File.WriteAllText(FilePath, _root.ToJsonString(Options));
            _dirty = false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write config {Path}", FilePath);
        }
    }
}
