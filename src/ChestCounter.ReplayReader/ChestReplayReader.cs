using System.Diagnostics.CodeAnalysis;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ChestCounter;

/// <summary>
/// The only two container properties we care about. Every container class found in a replay
/// (anything that replicates bAlreadySearched) is parsed as this type, so new chest classes
/// added by a season work without listing their paths.
/// </summary>
[NetFieldExportGroup(SearchableContainer.Path, minimalParseMode: ParseMode.Minimal)]
public class SearchableContainer : INetFieldExportGroup
{
    public const string Path = "/ChestCounter/SearchableContainer";
    public const string SearchedProperty = "bAlreadySearched";

    [NetFieldExport(SearchedProperty, RepLayoutCmdType.PropertyBool)]
    public bool? bAlreadySearched { get; set; }

    [NetFieldExport("SearchingPawn", RepLayoutCmdType.PropertyObject)]
    public uint? SearchingPawn { get; set; }

    [NetFieldExport("bDestroyed", RepLayoutCmdType.PropertyBool)]
    public bool? bDestroyed { get; set; }
}

/// <summary>
/// Reads a Fortnite replay and counts searched containers.
/// Create one instance per replay.
/// </summary>
public sealed class ChestReplayReader : FortniteReplayReader.ReplayReader
{
    /// <summary>Bump when the counting rules change so cached results are parsed again.</summary>
    public const int ParserVersion = 4;

    /// <summary>Diagnostics of the last file read on this thread, for the command line tool.</summary>
    [ThreadStatic]
    public static List<string>? LastDiagnostics;

    private readonly ChestTracker _tracker = new();
    private readonly Dictionary<NetFieldExportGroup, NetFieldExportGroup> _aliases = new();
    private readonly HashSet<NetFieldExportGroup> _notContainers = new();
    private bool _containerExportSeen;
    private bool _inOpeningBunch;
    private string? _currentContainerPath;
    private int _fileLength = int.MaxValue;

    /// <param name="mode">
    /// Full also reads player positions and dropped items, needed to tell who opened a chest.
    /// Minimal reads only the containers.
    /// </param>
    public ChestReplayReader(ParseMode mode = ParseMode.Full) : base(null!, mode)
    {
    }

    /// <summary>
    /// Parses <paramref name="path"/>. Never throws for a bad replay: the error is returned in the result.
    /// </summary>
    public static MatchResult ReadFile(string path)
    {
        var file = new FileInfo(path);
        var result = new MatchResult
        {
            FileName = file.Name,
            FileSize = file.Length,
            FileLastWriteUtc = file.LastWriteTimeUtc,
            StartedUtc = file.LastWriteTimeUtc,
            ParserVersion = ParserVersion
        };

        try
        {
            // Fortnite keeps the file open while the match is running, so share it.
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            result.FileSize = bytes.Length;

            try
            {
                new ChestReplayReader(ParseMode.Full).Fill(bytes, result);
            }
            catch (Exception ex)
            {
                // The detailed read touches many more parts of the replay; if a game update breaks one
                // of them, the containers alone can usually still be read.
                new ChestReplayReader(ParseMode.Minimal).Fill(bytes, result);
                result.Note = $"Lectura detallada fallida ({ex.GetType().Name}: {ex.Message}); sin posiciones.";
            }
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            result.NotFinalized = ex.Message.Contains("not yet marked as completed", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private void Fill(byte[] bytes, MatchResult result)
    {
        _fileLength = bytes.Length;
        using var stream = new MemoryStream(bytes, writable: false);
        var replay = ReadReplay(stream);

        if (replay.Info.Timestamp > new DateTime(2017, 1, 1))
        {
            // Fortnite writes the recording player's local time (it matches the time in the file name).
            var timestamp = replay.Info.Timestamp;
            result.StartedUtc = timestamp.Kind == DateTimeKind.Utc
                ? timestamp
                : DateTime.SpecifyKind(timestamp, DateTimeKind.Local).ToUniversalTime();
        }

        result.LengthMs = replay.Info.LengthInMs;
        result.InProgress = replay.Info.IsLive;
        result.GameVersion = replay.Header?.Branch;
        result.Playlist = replay.GameData?.CurrentPlaylist;

        var owner = replay.PlayerData?.FirstOrDefault(p => p.IsReplayOwner);
        int? team = owner?.TeamIndex;
        if (team is null && replay.GameData?.RecorderId is { } recorderId)
        {
            team = _tracker.TeamOfState(recorderId);
        }

        result.RecorderTeam = team;

        if (team is not null && replay.PlayerData is not null)
        {
            result.Squad = replay.PlayerData
                .Where(p => p.TeamIndex == team)
                .Select(p => p.PlayerName ?? p.PlayerId ?? "?")
                .Distinct()
                .ToList();
        }

        result.Classes = _tracker.Build(team, replay.GameData?.RecorderId);
        result.PositionSamplesMe = _tracker.PositionSamplesMe;
        result.PositionSamplesSquad = _tracker.PositionSamplesSquad;
        result.LootDrops = _tracker.LootDrops;
        result.PilesNearSquad = _tracker.PilesNearSquad;
        result.PilesNearMe = _tracker.PilesNearMe;
        result.HasPositions = _tracker.PositionSamplesSquad > 0;
        LastDiagnostics = _tracker.DescribeLootNearRecorder(replay.GameData?.RecorderId);
    }

    // ---- Replays still being recorded ------------------------------------------------------------
    // Based on the ChestCounter project by LED-N (https://github.com/xdoofy92/ChestCounter, MIT license),
    // patches 10 and 11 of its lib/PATCHES.md: accept a "live" encrypted replay when its key is already present,
    // and stop at the last complete chunk instead of failing.

    /// <summary>True when the file ended in the middle of a chunk (replay still being written).</summary>
    public bool IsPartialReplay { get; private set; }

    public override void ReadReplayInfo(FArchive archive)
    {
        var magicNumber = archive.ReadUInt32();
        if (magicNumber != FileMagic)
        {
            throw new Unreal.Core.Exceptions.InvalidReplayException("Invalid replay file");
        }

        var fileVersion = archive.ReadUInt32AsEnum<ReplayVersionHistory>();
        archive.ReplayVersion = fileVersion;

        if (archive.ReplayVersion >= ReplayVersionHistory.HISTORY_CUSTOM_VERSIONS)
        {
            var customVersionCount = archive.ReadInt32();
            archive.SkipBytes(customVersionCount * 20);
        }

        var info = new ReplayInfo
        {
            FileVersion = fileVersion,
            LengthInMs = archive.ReadUInt32(),
            NetworkVersion = archive.ReadUInt32(),
            Changelist = archive.ReadUInt32(),
            FriendlyName = archive.ReadFString(),
            IsLive = archive.ReadUInt32AsBoolean()
        };

        if (fileVersion >= ReplayVersionHistory.HISTORY_RECORDED_TIMESTAMP)
        {
            info.Timestamp = DateTime.FromBinary(archive.ReadInt64());
        }

        if (fileVersion >= ReplayVersionHistory.HISTORY_COMPRESSION)
        {
            info.IsCompressed = archive.ReadUInt32AsBoolean();
        }

        if (fileVersion >= ReplayVersionHistory.HISTORY_ENCRYPTION)
        {
            info.IsEncrypted = archive.ReadUInt32AsBoolean();
            var size = archive.ReadUInt32();
            info.EncryptionKey = archive.ReadBytes(size).ToArray();
        }

        // Without a key nothing can be decrypted. A key made only of zeros is a placeholder too.
        var keyMissing = info.EncryptionKey.Length == 0 || info.EncryptionKey.Span.IndexOfAnyExcept((byte)0) < 0;
        if (info.IsEncrypted && keyMissing)
        {
            throw new Unreal.Core.Exceptions.InvalidReplayException(info.IsLive
                ? "Replay is marked encrypted but not yet marked as completed!"
                : "Completed replay is marked encrypted but has no key!");
        }

        Replay.Info = info;
    }

    public override void ReadReplayChunks(FArchive archive)
    {
        while (!archive.AtEnd())
        {
            if (_fileLength - archive.Position < 8)
            {
                IsPartialReplay = true;
                return;
            }

            var chunkType = archive.ReadUInt32AsEnum<ReplayChunkType>();
            var chunkSize = archive.ReadInt32();
            var offset = archive.Position;

            if (chunkSize > 0 && (long)offset + chunkSize > _fileLength)
            {
                IsPartialReplay = true;
                return;
            }

            if (chunkSize <= 0 || (long)chunkSize + offset > int.MaxValue)
            {
                archive.SetError();
                return;
            }

            try
            {
                if (chunkType == ReplayChunkType.ReplayData && _parseMode > ParseMode.EventsOnly)
                {
                    ReadReplayData(archive, chunkSize);
                }
                else if (chunkType == ReplayChunkType.Event)
                {
                    ReadEvent(archive);
                }
                else if (chunkType == ReplayChunkType.Header)
                {
                    ReadReplayHeader(archive);
                }
            }
            catch (Exception ex) when (Replay.Info?.IsLive == true && ex is not OutOfMemoryException)
            {
                // A chunk of a replay being recorded that cannot be read yet: keep what was read so far.
                IsPartialReplay = true;
                return;
            }

            if (archive.Position != offset + chunkSize)
            {
                archive.Seek(offset + chunkSize, SeekOrigin.Begin);
            }
        }
    }

    public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
    {
        // The frame starts with its time; peek at it so events can be placed on a clock.
        var start = archive.Position;
        if (archive.NetworkVersion >= NetworkVersionHistory.HISTORY_MULTIPLE_LEVELS)
        {
            archive.ReadInt32();
        }

        var seconds = archive.ReadSingle();
        archive.Seek(start, SeekOrigin.Begin);

        if (!float.IsNaN(seconds))
        {
            _tracker.OnTime(seconds);
        }

        base.ReadDemoFrameIntoPlaybackPackets(archive);
    }

    public override NetFieldExport? ReadNetFieldExport(FArchive archive)
    {
        var export = base.ReadNetFieldExport(archive);
        if (export?.Name == SearchableContainer.SearchedProperty)
        {
            // A class that replicates bAlreadySearched was (re)announced, look for new container classes.
            _containerExportSeen = true;
            _notContainers.Clear();
        }

        return export;
    }

    public override void ProcessBunch(DataBunch bunch)
    {
        var channel = bunch.ChIndex < Channels.Length ? Channels[bunch.ChIndex] : null;
        var previous = _inOpeningBunch;
        _inOpeningBunch = channel is not null && channel.Actor is null;

        try
        {
            base.ProcessBunch(bunch);
        }
        finally
        {
            _inOpeningBunch = previous;
        }
    }

    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex,
        [NotNullWhen(returnValue: true)] out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
    {
        if (!netDeltaUpdate && TryGetAlias(group, out var alias))
        {
            // The export table of a class can grow while the replay is read, keep the alias in sync.
            alias.NetFieldExports = group.NetFieldExports;
            alias.NetFieldExportsLength = group.NetFieldExportsLength;

            _currentContainerPath = group.PathName;
            try
            {
                return base.ReceiveProperties(archive, alias, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
            }
            finally
            {
                _currentContainerPath = null;
            }
        }

        return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
    }

    private bool TryGetAlias(NetFieldExportGroup group, [NotNullWhen(returnValue: true)] out NetFieldExportGroup? alias)
    {
        if (_aliases.TryGetValue(group, out alias))
        {
            return true;
        }

        if (!_containerExportSeen || _notContainers.Contains(group))
        {
            return false;
        }

        // Classes the parser library already understands keep their normal handling.
        if (!_netFieldParser.WillReadType(group.PathName) && HasSearchedProperty(group))
        {
            alias = new NetFieldExportGroup
            {
                PathName = SearchableContainer.Path,
                PathNameIndex = group.PathNameIndex,
                NetFieldExportsLength = group.NetFieldExportsLength,
                NetFieldExports = group.NetFieldExports
            };
            _aliases[group] = alias;
            return true;
        }

        _notContainers.Add(group);
        return false;
    }

    private static bool HasSearchedProperty(NetFieldExportGroup group)
    {
        var exports = group.NetFieldExports;
        if (exports is null)
        {
            return false;
        }

        foreach (var export in exports)
        {
            if (export?.Name == SearchableContainer.SearchedProperty)
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
        switch (exportGroup)
        {
            case SearchableContainer container:
                if (Channels[channelIndex]?.ActorId is { } containerId && _currentContainerPath is not null)
                {
                    // Containers placed by the map have no position in the replay; spawned ones do.
                    var spot = Channels[channelIndex]?.Actor?.Location;
                    (double, double, double)? at = spot is not null && (spot.X != 0 || spot.Y != 0 || spot.Z != 0)
                        ? (spot.X, spot.Y, spot.Z)
                        : null;
                    _tracker.OnContainerUpdate(containerId, _currentContainerPath, _inOpeningBunch,
                        container.bAlreadySearched, container.SearchingPawn, at, container.bDestroyed);
                }
                return;

            case PlayerPawn pawn:
                if (Channels[channelIndex]?.ActorId is { } pawnId)
                {
                    if (pawn.PlayerState is > 0)
                    {
                        _tracker.OnPawn(pawnId, pawn.PlayerState.Value);
                    }

                    if (pawn.ReplicatedMovement?.Location is { } at)
                    {
                        _tracker.OnPawnPosition(pawnId, at.X, at.Y, at.Z);
                    }
                }
                break;

            case FortPickup pickup:
                // A new item that starts its flight somewhere and was not dropped by a player came out of a container.
                if (_inOpeningBunch && pickup.LootInitialPosition is { } from && pickup.PawnWhoDroppedPickup is not > 0)
                {
                    _tracker.OnLootDrop(from.X, from.Y, from.Z, pickup.bTossedFromContainer == true);
                }
                break;

            case FortPlayerState state:
                if (state.TeamIndex is > 0 && Channels[channelIndex]?.ActorId is { } stateId)
                {
                    _tracker.OnPlayerTeam(stateId, state.TeamIndex.Value);
                }
                break;
        }

        base.OnExportRead(channelIndex, exportGroup);
    }
}
