using System.Text.Json;

namespace ChestCounter;

/// <summary>
/// History of parsed matches, kept in history.json. Fortnite deletes old unsaved replays,
/// so the totals have to live outside the replay folder.
/// </summary>
public sealed class MatchStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, MatchResult> _matches = new(StringComparer.OrdinalIgnoreCase);

    public MatchStore(string? folder = null)
    {
        folder ??= Settings.DataFolder;
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "history.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<MatchResult>>(File.ReadAllText(_path), Settings.Json);
                if (list is not null)
                {
                    _matches = list.Where(m => !string.IsNullOrEmpty(m.FileName))
                        .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception)
        {
            // Keep the unreadable file for inspection and start a new history.
            try { File.Copy(_path, _path + ".roto", overwrite: true); } catch (Exception) { }
        }
    }

    public MatchResult? Get(string fileName)
    {
        lock (_lock)
        {
            return _matches.GetValueOrDefault(fileName);
        }
    }

    public void Put(MatchResult match)
    {
        lock (_lock)
        {
            _matches[match.FileName] = match;
            Save();
        }
    }

    public bool Remove(string fileName)
    {
        lock (_lock)
        {
            if (!_matches.Remove(fileName))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _matches.Clear();
            Save();
        }
    }

    /// <summary>All matches, newest first.</summary>
    public List<MatchResult> All()
    {
        lock (_lock)
        {
            return _matches.Values.OrderByDescending(m => m.StartedUtc).ToList();
        }
    }

    private void Save() => WriteAtomic(_path, JsonSerializer.Serialize(_matches.Values.OrderBy(m => m.StartedUtc).ToList(), Settings.Json));

    internal static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
