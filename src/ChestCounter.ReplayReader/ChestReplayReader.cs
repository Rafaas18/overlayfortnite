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
}

/// <summary>
/// Reads a Fortnite replay and counts searched containers.
/// Create one instance per replay.
/// </summary>
public sealed class ChestReplayReader : FortniteReplayReader.ReplayReader
{
    /// <summary>Bump when the counting rules change so cached results are parsed again.</summary>
    public const int ParserVersion = 1;

    private readonly ChestTracker _tracker = new();
    private readonly Dictionary<NetFieldExportGroup, NetFieldExportGroup> _aliases = new();
    private readonly HashSet<NetFieldExportGroup> _notContainers = new();
    private bool _containerExportSeen;
    private bool _inOpeningBunch;
    private string? _currentContainerPath;

    public ChestReplayReader() : base(null, ParseMode.Minimal)
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

            var reader = new ChestReplayReader();
            reader.Fill(bytes, result);
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return result;
    }

    private void Fill(byte[] bytes, MatchResult result)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var replay = ReadReplay(stream);

        if (replay.Info.Timestamp > new DateTime(2017, 1, 1))
        {
            // The replay stores UTC; only convert when the value says it is local time.
            var timestamp = replay.Info.Timestamp;
            result.StartedUtc = timestamp.Kind == DateTimeKind.Local
                ? timestamp.ToUniversalTime()
                : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
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

        result.Classes = _tracker.Build(team);
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
                    _tracker.OnContainerUpdate(containerId, _currentContainerPath, _inOpeningBunch,
                        container.bAlreadySearched, container.SearchingPawn);
                }
                return;

            case PlayerPawn pawn:
                if (pawn.PlayerState is > 0 && Channels[channelIndex]?.ActorId is { } pawnId)
                {
                    _tracker.OnPawn(pawnId, pawn.PlayerState.Value);
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
