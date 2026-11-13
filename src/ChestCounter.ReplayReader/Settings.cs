using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter;

/// <summary>
/// User settings, stored as settings.json next to the match history.
/// </summary>
public sealed class Settings
{
    /// <summary>Folder where Fortnite saves replays. Empty means the default location.</summary>
    public string ReplayFolder { get; set; } = "";

    /// <summary>Regular expression matched against the container class name to decide what is a chest.</summary>
    public string ChestPattern { get; set; } = Counting.DefaultChestPattern;

    public CountMode Mode { get; set; } = CountMode.Auto;

    /// <summary>Chests opened before the overlay started counting (added to the total).</summary>
    public int Baseline { get; set; }

    /// <summary>How often the replay of the match in progress is read again, in seconds. 0 disables it.</summary>
    public int LiveRefreshSeconds { get; set; } = 45;

    public double WindowLeft { get; set; } = 40;
    public double WindowTop { get; set; } = 40;
    public double Opacity { get; set; } = 0.9;
    public bool ClickThrough { get; set; }

    [JsonIgnore]
    public string EffectiveReplayFolder =>
        string.IsNullOrWhiteSpace(ReplayFolder) ? DefaultReplayFolder : Environment.ExpandEnvironmentVariables(ReplayFolder);

    public static string DefaultReplayFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverlayCofres");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Settings Load(string? folder = null)
    {
        var path = Path.Combine(folder ?? DataFolder, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
            }
        }
        catch (Exception)
        {
            // A broken settings file must not stop the overlay, start from defaults.
        }

        return new Settings();
    }

    public void Save(string? folder = null)
    {
        folder ??= DataFolder;
        Directory.CreateDirectory(folder);
        MatchStore.WriteAtomic(Path.Combine(folder, "settings.json"), JsonSerializer.Serialize(this, Json));
    }
}
