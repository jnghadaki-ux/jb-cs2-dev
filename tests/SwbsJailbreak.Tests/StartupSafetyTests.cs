using System.Text.RegularExpressions;
using Xunit;

namespace SwbsJailbreak.Tests;

/// <summary>
/// On a cold server start the plugin loads before any map exists. Server.MapName / CurrentTime / TickCount read the engine
/// globals natively, which are not set yet, so touching them in OnLoad / OnAllModulesLoaded outside a hotReload guard crashes
/// the server ("Loading plugin SwbsJailbreak" is the last console line). Map-dependent setup belongs in OnMapStart.
/// </summary>
public class StartupSafetyTests
{
    private static readonly Regex LoadMethod = new(@"override void (OnLoad|OnAllModulesLoaded)\(bool hotReload\)\s*\{");
    private static readonly Regex EngineGlobals = new(@"Server\.(MapName|CurrentTime|TickCount)\b");

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwbsJailbreak.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SwbsJailbreak");
    }

    private static string Body(string src, int open)
    {
        var depth = 1;
        var i = open;
        while (depth > 0) depth += src[i++] switch { '{' => 1, '}' => -1, _ => 0 };
        return src[open..(i - 1)];
    }

    /// <summary>Removes what only runs on a hot reload: "if (hotReload) x;", "if (hotReload) { ... }", "hotReload ? a : b"
    /// and everything after "if (!hotReload) return;".</summary>
    private static string ColdStartPath(string body)
    {
        var bail = body.IndexOf("if (!hotReload) return;", StringComparison.Ordinal);
        if (bail >= 0) body = body[..bail];
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < body.Length)
        {
            var guard = body.IndexOf("if (hotReload)", i, StringComparison.Ordinal);
            if (guard < 0) { result.Append(body, i, body.Length - i); break; }
            result.Append(body, i, guard - i);
            var j = guard + "if (hotReload)".Length;
            while (j < body.Length && (char.IsWhiteSpace(body[j]) || body[j] == '/'))
                j = body[j] == '/' ? body.IndexOf('\n', j) + 1 : j + 1; // skip a trailing // comment
            if (body[j] == '{') j += Body(body, j + 1).Length + 2;
            else j = body.IndexOf(';', j) + 1;
            i = j;
        }

        return Regex.Replace(result.ToString(), @"hotReload\s*\?[^:;]*:", "");
    }

    [Fact]
    public void ColdStart_LoadPathsDoNotReadEngineGlobals()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var src = File.ReadAllText(file);
            foreach (Match m in LoadMethod.Matches(src))
            {
                var hits = EngineGlobals.Matches(ColdStartPath(Body(src, m.Index + m.Length)));
                foreach (Match h in hits) offenders.Add($"{Path.GetFileName(file)} {m.Groups[1].Value}: {h.Value}");
            }
        }

        Assert.True(offenders.Count == 0, "Engine globals read on a cold start:\n" + string.Join("\n", offenders));
    }
}
