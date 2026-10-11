using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;

namespace SwbsJailbreak.Core;

/// <summary>
/// Simple phrase loader. Every module ships its own file at lang/&lt;language&gt;/&lt;module&gt;.json (flat
/// "key": "text" objects). Files are merged; the configured language falls back to "en" per key.
/// Placeholders are string.Format style: {0}, {1}... Colour tags such as {green} are expanded by
/// <see cref="Colors"/> when the text is sent to chat.
/// </summary>
public static class Lang
{
    private static readonly Dictionary<string, string> Phrases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Fallback = new(StringComparer.OrdinalIgnoreCase);

    public static int Count => Phrases.Count + Fallback.Count;

    public static void Load(string moduleDirectory, string language)
    {
        Phrases.Clear();
        Fallback.Clear();
        LoadDir(Path.Combine(moduleDirectory, "lang", "en"), Fallback);
        if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            LoadDir(Path.Combine(moduleDirectory, "lang", language), Phrases);
    }

    private static void LoadDir(string dir, Dictionary<string, string> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        into[prop.Name] = prop.Value.GetString() ?? "";
            }
            catch (Exception ex)
            {
                JailbreakPlugin.Instance?.Logger.LogLangError(file, ex);
            }
        }
    }

    public static bool Has(string key) => Phrases.ContainsKey(key) || Fallback.ContainsKey(key);

    /// <summary>Translate a key. Unknown keys are returned verbatim so missing phrases are obvious but harmless.</summary>
    public static string T(string key, params object?[] args)
    {
        if (!Phrases.TryGetValue(key, out var text) && !Fallback.TryGetValue(key, out text))
            text = key;
        if (args.Length == 0) return text;
        try
        {
            // Colour tags like {olive} are not format items: escape them so string.Format only fills {0}, {1}, ...
            // (without this, every phrase mixing colours and arguments threw and was shown with raw "{0}").
            return string.Format(ColourTag.Replace(text, "{{$1}}"), args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ColourTag =
        new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", System.Text.RegularExpressions.RegexOptions.Compiled);
}

/// <summary>Chat colour tag expansion ({green}, {red}, ...).</summary>
public static class Colors
{
    private static readonly (string Tag, char Code)[] Tags =
    {
        ("{default}", ChatColors.Default),
        ("{white}", ChatColors.White),
        ("{darkred}", ChatColors.DarkRed),
        ("{green}", ChatColors.Green),
        ("{lightyellow}", ChatColors.LightYellow),
        ("{lightblue}", ChatColors.LightBlue),
        ("{olive}", ChatColors.Olive),
        ("{lime}", ChatColors.Lime),
        ("{red}", ChatColors.Red),
        ("{lightpurple}", ChatColors.LightPurple),
        ("{purple}", ChatColors.Purple),
        ("{grey}", ChatColors.Grey),
        ("{gray}", ChatColors.Grey),
        ("{yellow}", ChatColors.Yellow),
        ("{gold}", ChatColors.Gold),
        ("{silver}", ChatColors.Silver),
        ("{blue}", ChatColors.Blue),
        ("{darkblue}", ChatColors.DarkBlue),
        ("{bluegrey}", ChatColors.BlueGrey),
        ("{magenta}", ChatColors.Magenta),
        ("{lightred}", ChatColors.LightRed),
        ("{orange}", ChatColors.Orange),
        // team colours used by the original UltJB phrases
        ("{prisoner}", ChatColors.Gold),
        ("{guard}", ChatColors.LightBlue),
    };

    public static string Apply(string text)
    {
        if (text.IndexOf('{') < 0) return text;
        var sb = new StringBuilder(text);
        foreach (var (tag, code) in Tags)
            sb.Replace(tag, code.ToString());
        return sb.ToString();
    }

    /// <summary>Remove colour tags (for console / logs / HTML).</summary>
    public static string Strip(string text)
    {
        if (text.IndexOf('{') < 0) return StripCodes(text);
        var sb = new StringBuilder(text);
        foreach (var (tag, _) in Tags)
            sb.Replace(tag, "");
        return StripCodes(sb.ToString());
    }

    private static string StripCodes(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch >= ' ' || ch == '\n')
                sb.Append(ch);
        return sb.ToString();
    }

    public static char TeamColor(CsTeam team) => team switch
    {
        CsTeam.Terrorist => ChatColors.Gold,
        CsTeam.CounterTerrorist => ChatColors.LightBlue,
        _ => ChatColors.Grey
    };
}

internal static class LangLoggerExtensions
{
    public static void LogLangError(this Microsoft.Extensions.Logging.ILogger logger, string file, Exception ex)
    {
        Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex, "Failed to load phrase file {File}", file);
    }
}
