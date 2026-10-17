using System.Text.Json;

namespace Swbs.Common;

/// <summary>
/// addons/counterstrikesharp/configs/swbs_shared.json: one file read by every SWBS plugin on a server install, so all
/// game modes agree on where points and store items live. Put the same settings on every server that should share.
/// </summary>
public sealed class SharedDataConfig
{
    public const string FileName = "swbs_shared.json";

    /// <summary>"local" (each server keeps its own, the default), "folder" (same machine) or "mysql" (any machine).</summary>
    public string Backend { get; set; } = "local";

    /// <summary>Folder backend: a folder every server on this machine can read and write, e.g. /home/cs2/swbs-shared.</summary>
    public string Folder { get; set; } = "";

    public MySqlSettings MySql { get; set; } = new();

    /// <summary>
    /// The first time a player is seen with sharing on, add this server's existing points and store items to their
    /// shared record (once per server), so nothing earned before the switch is lost.
    /// </summary>
    public bool MergeExistingData { get; set; } = true;

    public sealed class MySqlSettings
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "swbs";
        public string User { get; set; } = "swbs";
        public string Password { get; set; } = "";
        public string Table { get; set; } = "swbs_player_data";
    }

    public const string Template = """
        {
          // Where players' store points and store items are kept. Every SWBS server (bhop, surf, kz, course, surf rpg,
          // minigames, deathmatch, pug, 1v1, jailbreak) reads this file, so give all of them the same settings.
          //
          //   "local"  - each server keeps its own points and items (how it worked before).
          //   "folder" - servers on the SAME machine share one folder: set Folder to a path all of them can write.
          //   "mysql"  - servers on ANY machine share one MySQL / MariaDB database: fill in MySql.
          "Backend": "local",

          "Folder": "",

          "MySql": {
            "Host": "127.0.0.1",
            "Port": 3306,
            "Database": "swbs",
            "User": "swbs",
            "Password": "",
            "Table": "swbs_player_data"
          },

          // When sharing is first switched on, add each server's existing points and items to the player's shared
          // record (once per server per player), so nothing is lost.
          "MergeExistingData": true
        }

        """;

    /// <summary>Read the file, writing the commented template first when it does not exist.</summary>
    public static SharedDataConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Template);
        }

        return Parse(File.ReadAllText(path));
    }

    public static SharedDataConfig Parse(string json) =>
        JsonSerializer.Deserialize<SharedDataConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? new SharedDataConfig();

    /// <summary>The backend these settings ask for, or null for "local" (or a misconfigured folder).</summary>
    public ISharedBackend? CreateBackend() => Backend.Trim().ToLowerInvariant() switch
    {
        "folder" when Folder.Trim().Length > 0 => new FolderBackend(Folder.Trim()),
        "folder" => throw new InvalidOperationException("Backend is \"folder\" but Folder is empty"),
        "mysql" => new MySqlBackend(MySql),
        "local" or "" => null,
        var other => throw new InvalidOperationException($"Unknown Backend \"{other}\" (use local, folder or mysql)")
    };
}
