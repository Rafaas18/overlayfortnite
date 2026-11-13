namespace ChestCounter;

/// <summary>
/// Turns the stream of container updates found in a replay into per-class counts.
/// Has no dependency on the replay parser, so the rules can be unit tested.
/// </summary>
public sealed class ChestTracker
{
    private sealed class ContainerState
    {
        public string ClassPath = "";
        public bool Opened;
        public uint? LastSearcherPawn;
    }

    private sealed record OpenEvent(string ClassPath, bool Live, uint? SearcherPawn, uint? SearcherState);

    private readonly Dictionary<uint, ContainerState> _containers = new();
    private readonly List<OpenEvent> _events = new();
    private readonly Dictionary<uint, uint> _pawnToState = new();
    private readonly Dictionary<uint, int> _stateTeam = new();

    /// <summary>
    /// A container actor received properties.
    /// </summary>
    /// <param name="actorId">Network id of the container, stable for the whole replay.</param>
    /// <param name="classPath">Unreal class path of the container.</param>
    /// <param name="openingBunch">
    /// True when the update came with the message that first made the actor visible.
    /// A container that is already searched in that message was opened before we saw it.
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

            _events.Add(new OpenEvent(classPath, live, pawn, searcherState));
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

    /// <summary>
    /// Builds the per-class totals. <paramref name="recorderTeam"/> is the team of the player who recorded the replay.
    /// </summary>
    public List<ClassCount> Build(int? recorderTeam)
    {
        var result = new Dictionary<string, ClassCount>();

        foreach (var e in _events)
        {
            if (!result.TryGetValue(e.ClassPath, out var count))
            {
                count = new ClassCount { ClassPath = e.ClassPath, ShortName = ShortNameOf(e.ClassPath) };
                result[e.ClassPath] = count;
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

            if (searcherState is { } state && _stateTeam.TryGetValue(state, out var team))
            {
                count.LiveAttributed++;
                if (recorderTeam is not null && team == recorderTeam)
                {
                    count.LiveBySquad++;
                }
            }
        }

        return result.Values.OrderByDescending(c => c.Live + c.AlreadyOpen).ThenBy(c => c.ShortName).ToList();
    }

    public static string ShortNameOf(string classPath)
    {
        var i = classPath.LastIndexOfAny(new[] { '.', '/' });
        return i >= 0 && i < classPath.Length - 1 ? classPath[(i + 1)..] : classPath;
    }
}
