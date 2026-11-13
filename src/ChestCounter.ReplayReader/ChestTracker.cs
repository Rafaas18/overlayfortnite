namespace ChestCounter;

/// <summary>
/// Turns the stream of container, loot and player updates found in a replay into per-class counts.
/// Has no dependency on the replay parser, so the rules can be unit tested.
/// </summary>
public sealed class ChestTracker
{
    /// <summary>Loot that appears within this many seconds of a container opening can belong to it.</summary>
    public const float LootWindowSeconds = 2f;

    /// <summary>A player this close (Unreal units, cm) to where the loot popped out opened the container.</summary>
    public const double NearDistance = 600;

    /// <summary>A position sample older or newer than this (seconds) says nothing about where the player was.</summary>
    public const float PositionMaxAgeSeconds = 3f;

    private const float PositionSampleSeconds = 0.25f;

    private sealed class ContainerState
    {
        public string ClassPath = "";
        public bool Opened;
        public uint? LastSearcherPawn;
    }

    private sealed record OpenEvent(float Time, string ClassPath, bool Live, uint? SearcherPawn, uint? SearcherState);

    private readonly record struct Sample(float Time, double X, double Y, double Z);

    private sealed class LootDrop
    {
        public float Time;
        public double X, Y, Z;
        public bool Claimed;
    }

    private readonly Dictionary<uint, ContainerState> _containers = new();
    private readonly List<OpenEvent> _events = new();
    private readonly List<LootDrop> _loot = new();
    private readonly Dictionary<uint, List<Sample>> _pawnPositions = new();
    private readonly Dictionary<uint, uint> _pawnToState = new();
    private readonly Dictionary<uint, int> _stateTeam = new();
    private float _time;

    // Filled by Build, for diagnostics.
    public int PositionSamplesMe { get; private set; }
    public int PositionSamplesSquad { get; private set; }
    public int LootDrops => _loot.Count;

    /// <summary>Replay clock, in seconds since the recording started.</summary>
    public void OnTime(float seconds)
    {
        if (seconds > _time)
        {
            _time = seconds;
        }
    }

    /// <summary>
    /// A container actor received properties.
    /// </summary>
    /// <param name="actorId">Network id of the container, stable for the whole replay.</param>
    /// <param name="classPath">Unreal class path of the container.</param>
    /// <param name="openingBunch">
    /// True when the update came with the message that first made the actor visible.
    /// </param>
    /// <param name="searched">New value of bAlreadySearched, null when the update did not touch it.</param>
    /// <param name="searchingPawn">Pawn currently searching, null when the update did not touch it.</param>
    public void OnContainerUpdate(uint actorId, string classPath, bool openingBunch, bool? searched, uint? searchingPawn)
    {
        if (!_containers.TryGetValue(actorId, out var state))
        {
            state = new ContainerState();
            _containers[actorId] = state;
        }

        state.ClassPath = classPath;

        if (searchingPawn is > 0)
        {
            state.LastSearcherPawn = searchingPawn;
        }

        if (searched == true && !state.Opened)
        {
            state.Opened = true;

            var live = !openingBunch;
            var pawn = live ? state.LastSearcherPawn : null;
            uint? searcherState = pawn is { } p && _pawnToState.TryGetValue(p, out var s) ? s : null;

            _events.Add(new OpenEvent(_time, classPath, live, pawn, searcherState));
        }
        else if (searched == false && state.Opened)
        {
            // Restocked container (some modes refill them), it can be opened again.
            state.Opened = false;
            state.LastSearcherPawn = searchingPawn is > 0 ? searchingPawn : null;
        }
    }

    /// <summary>A player pawn told us which player state (player) owns it.</summary>
    public void OnPawn(uint pawnActorId, uint playerStateActorId) => _pawnToState[pawnActorId] = playerStateActorId;

    /// <summary>A player state told us its team.</summary>
    public void OnPlayerTeam(uint playerStateActorId, int teamIndex) => _stateTeam[playerStateActorId] = teamIndex;

    public int? TeamOfState(uint playerStateActorId) =>
        _stateTeam.TryGetValue(playerStateActorId, out var team) ? team : null;

    /// <summary>A player pawn moved.</summary>
    public void OnPawnPosition(uint pawnActorId, double x, double y, double z)
    {
        if (!_pawnPositions.TryGetValue(pawnActorId, out var samples))
        {
            samples = new List<Sample>();
            _pawnPositions[pawnActorId] = samples;
        }

        if (samples.Count == 0 || _time - samples[^1].Time >= PositionSampleSeconds)
        {
            samples.Add(new Sample(_time, x, y, z));
        }
    }

    /// <summary>An item popped out of a container at this position (the container's position).</summary>
    public void OnLootDrop(double x, double y, double z) => _loot.Add(new LootDrop { Time = _time, X = x, Y = y, Z = z });

    /// <summary>
    /// Builds the per-class totals.
    /// </summary>
    /// <param name="recorderTeam">Team of the player who recorded the replay.</param>
    /// <param name="recorderState">Player state id of the player who recorded the replay.</param>
    public List<ClassCount> Build(int? recorderTeam, uint? recorderState = null)
    {
        var result = new Dictionary<string, ClassCount>();

        var myPawns = new List<List<Sample>>();
        var squadPawns = new List<List<Sample>>();
        foreach (var (pawn, samples) in _pawnPositions)
        {
            if (!_pawnToState.TryGetValue(pawn, out var state))
            {
                continue;
            }

            var isMe = recorderState is not null && state == recorderState;
            if (isMe)
            {
                myPawns.Add(samples);
            }

            if (isMe || (recorderTeam is not null && _stateTeam.TryGetValue(state, out var team) && team == recorderTeam))
            {
                squadPawns.Add(samples);
            }
        }

        PositionSamplesMe = myPawns.Sum(s => s.Count);
        PositionSamplesSquad = squadPawns.Sum(s => s.Count);

        foreach (var e in _events.OrderBy(e => e.Time))
        {
            if (!result.TryGetValue(e.ClassPath, out var count))
            {
                count = new ClassCount { ClassPath = e.ClassPath, ShortName = ShortNameOf(e.ClassPath) };
                result[e.ClassPath] = count;
            }

            var (nearSquad, nearMe) = ClaimLoot(e.Time, squadPawns, myPawns);
            if (nearSquad)
            {
                count.NearSquad++;
            }

            if (nearMe)
            {
                count.NearMe++;
            }

            if (!e.Live)
            {
                count.AlreadyOpen++;
                continue;
            }

            count.Live++;

            // The pawn -> player link can arrive after the chest was opened, so retry here.
            var searcherState = e.SearcherState;
            if (searcherState is null && e.SearcherPawn is { } pawn && _pawnToState.TryGetValue(pawn, out var s))
            {
                searcherState = s;
            }

            if (searcherState is { } st && _stateTeam.TryGetValue(st, out var searcherTeam))
            {
                count.LiveAttributed++;
                if (recorderTeam is not null && searcherTeam == recorderTeam)
                {
                    count.LiveBySquad++;
                }
            }
        }

        return result.Values.OrderByDescending(c => c.Live + c.AlreadyOpen).ThenBy(c => c.ShortName).ToList();
    }

    /// <summary>
    /// Finds loot that popped out around <paramref name="time"/> next to a squad member and marks it as used,
    /// so that one pile of loot proves one container only.
    /// </summary>
    private (bool NearSquad, bool NearMe) ClaimLoot(float time, List<List<Sample>> squadPawns, List<List<Sample>> myPawns)
    {
        LootDrop? best = null;
        var bestDistance = double.MaxValue;

        foreach (var drop in _loot)
        {
            if (drop.Claimed || Math.Abs(drop.Time - time) > LootWindowSeconds)
            {
                continue;
            }

            var distance = NearestDistance(squadPawns, drop);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = drop;
            }
        }

        if (best is null || bestDistance > NearDistance)
        {
            return (false, false);
        }

        var nearMe = NearestDistance(myPawns, best) <= NearDistance;

        // Everything that came out of the same container shares the same starting point.
        foreach (var drop in _loot)
        {
            if (!drop.Claimed && Math.Abs(drop.Time - time) <= LootWindowSeconds
                && Distance(drop.X, drop.Y, drop.Z, best.X, best.Y, best.Z) <= 100)
            {
                drop.Claimed = true;
            }
        }

        return (true, nearMe);
    }

    private static double NearestDistance(List<List<Sample>> pawns, LootDrop drop)
    {
        var nearest = double.MaxValue;
        foreach (var samples in pawns)
        {
            if (SampleAt(samples, drop.Time) is { } sample)
            {
                nearest = Math.Min(nearest, Distance(sample.X, sample.Y, sample.Z, drop.X, drop.Y, drop.Z));
            }
        }

        return nearest;
    }

    private static Sample? SampleAt(List<Sample> samples, float time)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        // First sample at or after the requested time.
        int low = 0, high = samples.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (samples[mid].Time < time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        Sample? best = null;
        foreach (var index in new[] { low - 1, low })
        {
            if (index < 0 || index >= samples.Count)
            {
                continue;
            }

            if (best is null || Math.Abs(samples[index].Time - time) < Math.Abs(best.Value.Time - time))
            {
                best = samples[index];
            }
        }

        return best is { } b && Math.Abs(b.Time - time) <= PositionMaxAgeSeconds ? b : null;
    }

    private static double Distance(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        double dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Diagnostics: every pile of loot that popped out next to the recording player, with the time
    /// distance to the closest container that was seen opening. Call after <see cref="Build"/>.
    /// </summary>
    public List<string> DescribeLootNearRecorder(uint? recorderState)
    {
        var lines = new List<string>();
        var mine = _pawnPositions
            .Where(p => recorderState is not null && _pawnToState.TryGetValue(p.Key, out var st) && st == recorderState)
            .Select(p => p.Value).ToList();

        var seen = new List<LootDrop>();
        foreach (var drop in _loot.OrderBy(d => d.Time))
        {
            if (seen.Any(o => Math.Abs(o.Time - drop.Time) <= 5 && Distance(o.X, o.Y, o.Z, drop.X, drop.Y, drop.Z) <= 100))
            {
                continue;
            }

            seen.Add(drop);
            var distance = NearestDistance(mine, drop);
            if (distance > 1500)
            {
                continue;
            }

            var items = _loot.Count(o => Math.Abs(o.Time - drop.Time) <= 5 && Distance(o.X, o.Y, o.Z, drop.X, drop.Y, drop.Z) <= 100);
            var closest = _events.OrderBy(e => Math.Abs(e.Time - drop.Time)).FirstOrDefault();
            var delta = closest is null ? "sin contenedores" : $"{closest.Time - drop.Time:+0.0;-0.0}s {ShortNameOf(closest.ClassPath)}{(closest.Live ? " (en vivo)" : "")}";
            lines.Add($"t={drop.Time,7:0.0}s  {items} objetos a {distance / 100:0.0} m de mi  |  contenedor mas cercano en el tiempo: {delta}");
        }

        return lines;
    }

    public static string ShortNameOf(string classPath)
    {
        var i = classPath.LastIndexOfAny(new[] { '.', '/' });
        return i >= 0 && i < classPath.Length - 1 ? classPath[(i + 1)..] : classPath;
    }
}
