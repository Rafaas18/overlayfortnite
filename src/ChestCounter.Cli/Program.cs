using System.Diagnostics;
using System.Text.Json;
using ChestCounter;

// Diagnostic tool: reads replays and prints what the counter sees in each one.
//   ChestCounter.Cli                      -> every replay in Fortnite's replay folder
//   ChestCounter.Cli <file-or-folder>...  -> those replays
//   --json                                -> machine readable output
//   --pattern <regex>                     -> which container classes count as chests (default: Chest)

var json = false;
var pattern = Counting.DefaultChestPattern;
var inputs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            json = true;
            break;
        case "--pattern" when i + 1 < args.Length:
            pattern = args[++i];
            break;
        default:
            inputs.Add(args[i]);
            break;
    }
}

if (inputs.Count == 0)
{
    inputs.Add(Settings.Load().EffectiveReplayFolder);
}

var files = new List<string>();
foreach (var input in inputs)
{
    if (Directory.Exists(input))
    {
        files.AddRange(Directory.EnumerateFiles(input, "*.replay").OrderBy(f => File.GetLastWriteTimeUtc(f)));
    }
    else if (File.Exists(input))
    {
        files.Add(input);
    }
    else
    {
        Console.Error.WriteLine($"No existe: {input}");
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("No se encontraron archivos .replay.");
    return 1;
}

var filter = Counting.BuildFilter(pattern);
var results = new List<MatchResult>();
var failed = 0;
var total = 0;

foreach (var file in files)
{
    var watch = Stopwatch.StartNew();
    var match = ChestReplayReader.ReadFile(file);
    watch.Stop();
    results.Add(match);

    if (match.Error is not null)
    {
        failed++;
    }

    if (json)
    {
        continue;
    }

    Console.WriteLine($"== {match.FileName}  ({match.FileSize / 1024} KB, leido en {watch.ElapsedMilliseconds} ms)");
    if (match.Error is not null)
    {
        Console.WriteLine($"   ERROR: {match.Error}");
        continue;
    }

    var count = Counting.Count(match, filter, CountMode.Auto);
    total += count.Counted;

    Console.WriteLine($"   {match.StartedUtc:yyyy-MM-dd HH:mm} UTC, {match.LengthMs / 60000} min, {match.GameVersion}, modo {match.Playlist ?? "?"}");
    Console.WriteLine($"   Equipo {match.RecorderTeam?.ToString() ?? "?"}: {(match.Squad.Count > 0 ? string.Join(", ", match.Squad) : "?")}");
    Console.WriteLine($"   COFRES contados: {count.Counted} (regla {count.ModeUsed})  |  en vivo {count.Live}, del squad {(count.SquadKnown ? count.Squad.ToString() : "sin dato")}, ya abiertos {count.AlreadyOpen}");
    Console.WriteLine("   Clases de contenedor vistas (en vivo / con autor / del squad / ya abiertos):");
    foreach (var c in match.Classes)
    {
        var mark = filter.IsMatch(c.ShortName) ? "*" : " ";
        Console.WriteLine($"    {mark} {c.ShortName,-58} {c.Live,4} / {c.LiveAttributed,4} / {c.LiveBySquad,4} / {c.AlreadyOpen,4}");
    }

    if (match.Classes.Count == 0)
    {
        Console.WriteLine("      (ninguna)");
    }
}

if (json)
{
    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
}
else
{
    Console.WriteLine();
    Console.WriteLine($"Total: {total} cofres en {results.Count - failed} partidas ({failed} con error). Las clases con * son las que cuentan con el patron \"{pattern}\".");
}

return failed == results.Count ? 2 : 0;
