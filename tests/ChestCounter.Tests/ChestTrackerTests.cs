using ChestCounter;
using Xunit;

namespace ChestCounter.Tests;

public class ChestTrackerTests
{
    private const string Chest = "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C";
    private const string Ammo = "/Game/Building/ActorBlueprints/Containers/Tiered_Ammo_Athena.Tiered_Ammo_Athena_C";

    [Fact]
    public void Chest_opened_in_view_counts_as_live()
    {
        var tracker = new ChestTracker();
        tracker.OnContainerUpdate(11, Chest, openingBunch: true, searched: null, searchingPawn: null);
        tracker.OnContainerUpdate(11, Chest, openingBunch: false, searched: true, searchingPawn: null);

        var chest = Assert.Single(tracker.Build(recorderTeam: 3));
        Assert.Equal("Tiered_Chest_Athena_C", chest.ShortName);
        Assert.Equal(1, chest.Live);
        Assert.Equal(0, chest.AlreadyOpen);
    }

    [Fact]
    public void Chest_first_seen_already_searched_is_not_live()
    {
        var tracker = new ChestTracker();
        tracker.OnContainerUpdate(11, Chest, openingBunch: true, searched: true, searchingPawn: null);

        var chest = Assert.Single(tracker.Build(recorderTeam: 3));
        Assert.Equal(0, chest.Live);
        Assert.Equal(1, chest.AlreadyOpen);
    }

    [Fact]
    public void Same_chest_is_counted_once_even_if_it_is_announced_again()
    {
        var tracker = new ChestTracker();
        tracker.OnContainerUpdate(11, Chest, false, true, null);
        tracker.OnContainerUpdate(11, Chest, false, true, null);
        tracker.OnContainerUpdate(11, Chest, true, true, null);

        var chest = Assert.Single(tracker.Build(null));
        Assert.Equal(1, chest.Live);
        Assert.Equal(0, chest.AlreadyOpen);
    }

    [Fact]
    public void Restocked_chest_can_be_counted_again()
    {
        var tracker = new ChestTracker();
        tracker.OnContainerUpdate(11, Chest, false, true, null);
        tracker.OnContainerUpdate(11, Chest, false, false, null);
        tracker.OnContainerUpdate(11, Chest, false, true, null);

        Assert.Equal(2, Assert.Single(tracker.Build(null)).Live);
    }

    [Fact]
    public void Openings_are_attributed_to_the_squad_through_pawn_and_team()
    {
        var tracker = new ChestTracker();
        tracker.OnPlayerTeam(playerStateActorId: 100, teamIndex: 3); // teammate
        tracker.OnPlayerTeam(playerStateActorId: 200, teamIndex: 7); // enemy
        tracker.OnPawn(pawnActorId: 1000, playerStateActorId: 100);

        // Teammate: searcher announced first, opening confirmed later.
        tracker.OnContainerUpdate(11, Chest, true, null, 1000);
        tracker.OnContainerUpdate(11, Chest, false, true, null);

        // Enemy whose pawn is only linked to a player after the chest was opened.
        tracker.OnContainerUpdate(12, Chest, false, true, 2000);
        tracker.OnPawn(pawnActorId: 2000, playerStateActorId: 200);

        // Nobody known.
        tracker.OnContainerUpdate(13, Chest, false, true, null);

        var chest = Assert.Single(tracker.Build(recorderTeam: 3));
        Assert.Equal(3, chest.Live);
        Assert.Equal(2, chest.LiveAttributed);
        Assert.Equal(1, chest.LiveBySquad);
    }

    [Fact]
    public void Classes_are_reported_separately()
    {
        var tracker = new ChestTracker();
        tracker.OnContainerUpdate(11, Chest, false, true, null);
        tracker.OnContainerUpdate(12, Ammo, false, true, null);
        tracker.OnContainerUpdate(13, Ammo, false, true, null);

        var classes = tracker.Build(null);
        Assert.Equal(2, classes.Count);
        Assert.Equal("Tiered_Ammo_Athena_C", classes[0].ShortName);
        Assert.Equal(2, classes[0].Live);
    }
}

public class CountingTests
{
    private static MatchResult Match(params ClassCount[] classes) => new() { FileName = "a.replay", Classes = classes.ToList() };

    private static ClassCount Class(string name, int live, int already = 0, int attributed = 0, int squad = 0) =>
        new() { ClassPath = "/Game/" + name, ShortName = name, Live = live, AlreadyOpen = already, LiveAttributed = attributed, LiveBySquad = squad };

    [Fact]
    public void Only_classes_matching_the_pattern_count()
    {
        var match = Match(Class("Tiered_Chest_Athena_C", 5, 2), Class("Tiered_Ammo_Athena_C", 9, 4));

        var count = Counting.Count(match, Counting.BuildFilter("Chest"), CountMode.Live);

        Assert.Equal(5, count.Counted);
        Assert.Equal(2, count.AlreadyOpen);
    }

    [Fact]
    public void Auto_uses_squad_only_when_most_openings_have_an_author()
    {
        var filter = Counting.BuildFilter(null);

        var known = Counting.Count(Match(Class("Chest_A", 10, 0, attributed: 8, squad: 6)), filter, CountMode.Auto);
        Assert.Equal(CountMode.Squad, known.ModeUsed);
        Assert.Equal(6, known.Counted);

        var unknown = Counting.Count(Match(Class("Chest_A", 10, 0, attributed: 2, squad: 2)), filter, CountMode.Auto);
        Assert.Equal(CountMode.Live, unknown.ModeUsed);
        Assert.Equal(10, unknown.Counted);
    }

    [Fact]
    public void All_mode_adds_chests_that_were_already_open()
    {
        var count = Counting.Count(Match(Class("Chest_A", 4, 3)), Counting.BuildFilter("chest"), CountMode.All);
        Assert.Equal(7, count.Counted);
    }

    [Fact]
    public void Invalid_pattern_falls_back_to_the_default()
    {
        var count = Counting.Count(Match(Class("Chest_A", 4)), Counting.BuildFilter("(("), CountMode.Live);
        Assert.Equal(4, count.Counted);
    }
}

public class ReplayScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chestcounter-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _replays;
    private readonly string _data;

    public ReplayScannerTests()
    {
        _replays = Path.Combine(_root, "Demos");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_replays);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string AddReplay(string name, DateTime lastWriteUtc, int size = 10)
    {
        var path = Path.Combine(_replays, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static MatchResult FakeParse(string path, List<string> calls, string? error = null)
    {
        calls.Add(Path.GetFileName(path));
        var file = new FileInfo(path);
        return new MatchResult
        {
            FileName = file.Name,
            FileSize = file.Length,
            FileLastWriteUtc = file.LastWriteTimeUtc,
            StartedUtc = file.LastWriteTimeUtc,
            ParserVersion = ChestReplayReader.ParserVersion,
            Error = error,
            Classes = { new ClassCount { ClassPath = "/Game/Chest_C", ShortName = "Chest_C", Live = 3 } }
        };
    }

    [Fact]
    public void Finished_replays_are_parsed_once_and_survive_a_restart()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        AddReplay("a.replay", now.AddHours(-2));
        AddReplay("b.replay", now.AddHours(-1));
        var calls = new List<string>();

        var scanner = new ReplayScanner(new MatchStore(_data), p => FakeParse(p, calls), () => now);
        Assert.True(scanner.ScanOnce(_replays, 45));
        Assert.False(scanner.ScanOnce(_replays, 45));
        Assert.Equal(new[] { "a.replay", "b.replay" }, calls);

        // New process: history is read from disk, nothing is parsed again.
        var store = new MatchStore(_data);
        Assert.False(new ReplayScanner(store, p => FakeParse(p, calls), () => now).ScanOnce(_replays, 45));
        Assert.Equal(2, calls.Count);
        Assert.Equal("b.replay", store.All()[0].FileName);
    }

    [Fact]
    public void History_keeps_matches_after_fortnite_deletes_the_replay()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var path = AddReplay("a.replay", now.AddHours(-2));
        var store = new MatchStore(_data);
        var scanner = new ReplayScanner(store, p => FakeParse(p, new List<string>()), () => now);
        scanner.ScanOnce(_replays, 45);

        File.Delete(path);
        scanner.ScanOnce(_replays, 45);

        Assert.Single(store.All());
    }

    [Fact]
    public void Replay_in_progress_is_refreshed_no_more_often_than_configured_and_once_more_when_it_ends()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var path = AddReplay("live.replay", now.AddSeconds(-2));
        var calls = new List<string>();
        var store = new MatchStore(_data);
        var scanner = new ReplayScanner(store, p => FakeParse(p, calls), () => now);

        Assert.True(scanner.ScanOnce(_replays, 45));
        Assert.True(store.Get("live.replay")!.InProgress);

        // File grows 10 seconds later: too soon to read again.
        now = now.AddSeconds(10);
        File.WriteAllBytes(path, new byte[20]);
        File.SetLastWriteTimeUtc(path, now);
        Assert.False(scanner.ScanOnce(_replays, 45));

        now = now.AddSeconds(40);
        File.SetLastWriteTimeUtc(path, now);
        Assert.True(scanner.ScanOnce(_replays, 45));
        Assert.Equal(2, calls.Count);

        // Match over: the file stops changing, it is read one last time and marked finished.
        now = now.AddMinutes(5);
        Assert.True(scanner.ScanOnce(_replays, 45));
        Assert.False(store.Get("live.replay")!.InProgress);
        Assert.False(scanner.ScanOnce(_replays, 45));
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public void Failed_read_of_a_replay_in_progress_keeps_the_last_good_numbers()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var path = AddReplay("live.replay", now.AddSeconds(-2));
        var store = new MatchStore(_data);
        string? error = null;
        var scanner = new ReplayScanner(store, p => FakeParse(p, new List<string>(), error), () => now);
        scanner.ScanOnce(_replays, 1);

        error = "half written";
        now = now.AddSeconds(5);
        File.WriteAllBytes(path, new byte[30]);
        File.SetLastWriteTimeUtc(path, now);

        Assert.False(scanner.ScanOnce(_replays, 1));
        Assert.Null(store.Get("live.replay")!.Error);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var settings = new Settings { Baseline = 40000, Mode = CountMode.All, ChestPattern = "Chest|Treasure" };
        settings.Save(_data);

        var loaded = Settings.Load(_data);
        Assert.Equal(40000, loaded.Baseline);
        Assert.Equal(CountMode.All, loaded.Mode);
        Assert.Equal("Chest|Treasure", loaded.ChestPattern);
    }
}
