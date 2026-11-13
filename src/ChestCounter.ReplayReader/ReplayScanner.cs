namespace ChestCounter;

/// <summary>
/// Looks at the replay folder and parses files that are new or have changed.
/// </summary>
public sealed class ReplayScanner
{
    private readonly MatchStore _store;
    private readonly Func<string, MatchResult> _parse;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, DateTime> _lastAttemptUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A file written to more recently than this is treated as a match in progress.</summary>
    public static readonly TimeSpan InProgressWindow = TimeSpan.FromSeconds(30);

    public ReplayScanner(MatchStore store, Func<string, MatchResult>? parse = null, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _parse = parse ?? ChestReplayReader.ReadFile;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>File currently being parsed, for the status line.</summary>
    public string? CurrentFile { get; private set; }

    /// <summary>
    /// Parses what needs parsing. Returns true when the history changed.
    /// </summary>
    /// <param name="folder">Replay folder.</param>
    /// <param name="liveRefreshSeconds">Minimum time between two reads of a replay that is still being written. 0 skips those files until they are finished.</param>
    public bool ScanOnce(string folder, int liveRefreshSeconds)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var changed = false;
        var now = _utcNow();

        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.replay").OrderBy(f => f.LastWriteTimeUtc))
        {
            var known = _store.Get(file.Name);
            var beingWritten = now - file.LastWriteTimeUtc < InProgressWindow;

            if (known is not null
                && known.ParserVersion == ChestReplayReader.ParserVersion
                && known.FileSize == file.Length
                && known.FileLastWriteUtc == file.LastWriteTimeUtc
                && (known.Error is null || !beingWritten)
                && (!known.InProgress || beingWritten))
            {
                continue;
            }

            if (beingWritten)
            {
                if (liveRefreshSeconds <= 0)
                {
                    continue;
                }

                if (_lastAttemptUtc.TryGetValue(file.Name, out var last) && (now - last).TotalSeconds < liveRefreshSeconds)
                {
                    continue;
                }
            }

            _lastAttemptUtc[file.Name] = now;
            CurrentFile = file.Name;

            MatchResult result;
            try
            {
                result = _parse(file.FullName);
            }
            finally
            {
                CurrentFile = null;
            }

            result.InProgress = beingWritten;

            // While a match is running a failed read is normal (half-written file): keep the last good numbers.
            if (result.Error is not null && beingWritten && known is { Error: null })
            {
                continue;
            }

            _store.Put(result);
            changed = true;
        }

        return changed;
    }
}
