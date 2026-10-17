using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SwbsJailbreak.Tests;

/// <summary>
/// src/Swbs.Common is identical in every SWBS repo. An edit made here without running src/Swbs.Common/sync.sh (which
/// refreshes MANIFEST and copies the folder to the other repos) fails this test.
/// </summary>
public class CommonManifestTests
{
    [Fact]
    public void SharedFolder_MatchesItsManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Swbs.Common"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var common = Path.Combine(dir!.FullName, "src", "Swbs.Common");

        var expected = File.ReadAllLines(Path.Combine(common, "MANIFEST"))
            .Where(l => l.Length > 0)
            .Select(l => (Hash: l[..64], File: l[66..]))
            .ToList();

        var actual = Directory.EnumerateFiles(common, "*", SearchOption.AllDirectories)
            .Select(f => "./" + Path.GetRelativePath(common, f).Replace('\\', '/'))
            .Where(f => f != "./MANIFEST" && !f.StartsWith("./bin/") && !f.StartsWith("./obj/"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected.Select(e => e.File), actual);
        foreach (var (hash, file) in expected)
        {
            // Hash with LF line endings, like sync.sh on a Linux checkout (a Windows checkout may have CRLF).
            var text = File.ReadAllText(Path.Combine(common, file[2..])).Replace("\r\n", "\n");
            var bytes = new UTF8Encoding(false).GetBytes(text);
            Assert.True(hash == Convert.ToHexStringLower(SHA256.HashData(bytes)),
                $"src/Swbs.Common/{file[2..]} changed without src/Swbs.Common/sync.sh (keep it identical in every SWBS repo)");
        }
    }
}
