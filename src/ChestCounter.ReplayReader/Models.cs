namespace ChestCounter;

/// <summary>
/// Counts for one container class (chest, ammo box, cooler...) inside one replay.
/// </summary>
public sealed class ClassCount
{
    /// <summary>Full Unreal class path, e.g. /Game/Building/.../Tiered_Chest_Athena.Tiered_Chest_Athena_C</summary>
    public string ClassPath { get; set; } = "";

    /// <summary>Short class name, e.g. Tiered_Chest_Athena_C. Settings filter on this.</summary>
    public string ShortName { get; set; } = "";

    /// <summary>Containers that flipped to "searched" while the recording player had them in view.</summary>
    public int Live { get; set; }

    /// <summary>Containers that were first seen already searched (opened before they came into view).</summary>
    public int AlreadyOpen { get; set; }

    /// <summary>Live openings where the replay says which player did it.</summary>
    public int LiveAttributed { get; set; }

    /// <summary>Live openings done by someone on the recording player's team.</summary>
    public int LiveBySquad { get; set; }
}

/// <summary>
/// Everything we keep about one parsed replay file.
/// </summary>
public sealed class MatchResult
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime FileLastWriteUtc { get; set; }
    public int ParserVersion { get; set; }

    /// <summary>When the match was recorded (from the replay itself, falls back to the file date).</summary>
    public DateTime StartedUtc { get; set; }
    public uint LengthMs { get; set; }
    public string? GameVersion { get; set; }
    public string? Playlist { get; set; }
    public int? RecorderTeam { get; set; }
    public List<string> Squad { get; set; } = new();

    /// <summary>True while Fortnite is still writing the file (match in progress).</summary>
    public bool InProgress { get; set; }

    public List<ClassCount> Classes { get; set; } = new();

    /// <summary>Set when the replay could not be read. Counts are then empty.</summary>
    public string? Error { get; set; }
}

public enum CountMode
{
    /// <summary>Squad when the replay says who opened each chest, otherwise Live.</summary>
    Auto,

    /// <summary>Only openings attributed to the recording player's team.</summary>
    Squad,

    /// <summary>Every opening that happened in view, whoever did it.</summary>
    Live,

    /// <summary>Live plus chests that were already open when first seen.</summary>
    All
}

/// <summary>
/// Numbers for one match after applying the user's filter.
/// </summary>
public readonly record struct MatchCount(int Counted, int Live, int AlreadyOpen, int Squad, bool SquadKnown, CountMode ModeUsed);
