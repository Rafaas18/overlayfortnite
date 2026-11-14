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

    /// <summary>Containers the replay says the recording player opened (SearchingPawn is one of their pawns).</summary>
    public int Mine { get; set; }

    /// <summary>Containers the replay says a teammate (not the recording player) opened.</summary>
    public int Team { get; set; }

    /// <summary>Containers whose opener is known to the replay, whoever it was.</summary>
    public int Attributed { get; set; }

    /// <summary>Containers whose loot popped out next to someone on the recording player's team.</summary>
    public int NearSquad { get; set; }

    /// <summary>Containers whose loot popped out next to the recording player.</summary>
    public int NearMe { get; set; }
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

    /// <summary>True when the replay had player positions, which NearSquad / NearMe need.</summary>
    public bool HasPositions { get; set; }

    /// <summary>Diagnostics: position samples of the recording player and of the whole team, loot drops seen.</summary>
    public int PositionSamplesMe { get; set; }
    public int PositionSamplesSquad { get; set; }
    public int LootDrops { get; set; }

    /// <summary>Diagnostics: piles of loot (containers, eliminations...) that popped out next to a squad member / the recording player.</summary>
    public int PilesNearSquad { get; set; }
    public int PilesNearMe { get; set; }

    /// <summary>Diagnostics: why the detailed read was not used, when it was not.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// True when Fortnite has not closed the replay yet. Until it does, the file has no decryption key
    /// and cannot be read; that happens when the player leaves the match.
    /// </summary>
    public bool NotFinalized { get; set; }

    /// <summary>Set when the replay could not be read. Counts are then empty.</summary>
    public string? Error { get; set; }
}

public enum CountMode
{
    /// <summary>Mine when the replay says who opened the chests, otherwise All.</summary>
    Auto,

    /// <summary>Chests the replay says the recording player opened.</summary>
    Mine,

    /// <summary>Chests the replay says the recording player or a teammate opened.</summary>
    MineAndTeam,

    /// <summary>Chests whose loot popped out next to someone on the recording player's team.</summary>
    NearSquad,

    /// <summary>Chests whose loot popped out next to the recording player.</summary>
    NearMe,

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
public readonly record struct MatchCount(int Counted, int Live, int AlreadyOpen, int Squad, bool SquadKnown, int NearSquad, int NearMe, bool PositionsKnown, CountMode ModeUsed,
    int Mine = 0, int Team = 0, int Attributed = 0);
