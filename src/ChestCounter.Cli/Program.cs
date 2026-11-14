using System.Diagnostics;
using System.Text.Json;
using ChestCounter;

// Diagnostic tool: reads replays and prints what the counter sees in each one.
//   ChestCounter.Cli                      -> every replay in Fortnite's replay folder
//   ChestCounter.Cli <file-or-folder>...  -> those replays
//   --json                                -> machine readable output
//   --debug                               -> also list the loot that popped out next to the recording player
//   --pattern <regex>                     -> which container classes count as chests (default: Chest)

var json = false;
var debug = false;
var pattern = Counting.DefaultChestPattern;
var inputs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            json = true;
            break;
        case "--debug":
            debug = true;
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
        if (match.NotFinalized)
        {
            Console.WriteLine("   Fortnite todavia no cerro este replay (lo hace al salir de la partida). Vuelve a la sala y ejecuta de nuevo.");
        }
        continue;
    }

    var count = Counting.Count(match, filter, CountMode.Auto);
    total += count.Counted;

    Console.WriteLine($"   {match.StartedUtc:yyyy-MM-dd HH:mm} UTC, {match.LengthMs / 60000} min, {match.GameVersion}, modo {match.Playlist ?? "?"}");
    Console.WriteLine($"   Equipo {match.RecorderTeam?.ToString() ?? "?"}: {(match.Squad.Count > 0 ? string.Join(", ", match.Squad) : "?")}");
    Console.WriteLine($"   COFRES contados: {count.Counted} (regla {count.ModeUsed})  |  mios {count.Mine}, de mi equipo {count.Team}, con autor conocido {count.Attributed}, abiertos a la vista {count.Live}, vistos abiertos {count.Live + count.AlreadyOpen}");
    Console.WriteLine($"   Datos: {match.PositionSamplesMe} posiciones mias, {match.PositionSamplesSquad} del squad, {match.LootDrops} objetos soltados  |  pilas de botin junto al squad {match.PilesNearSquad}, junto a mi {match.PilesNearMe}{(match.Note is null ? "" : "  |  " + match.Note)}");
    Console.WriteLine("   Tipos de contenedor (mios / equipo / con autor / a la vista / vistos abiertos):");
    foreach (var c in match.Classes)
    {
        var mark = filter.IsMatch(c.ShortName) ? "*" : " ";
        Console.WriteLine($"    {mark} {c.ShortName,-52} {c.Mine,4} / {c.Team,4} / {c.Attributed,4} / {c.Live,4} / {c.Live + c.AlreadyOpen,4}");
    }

    if (match.Classes.Count == 0)
    {
        Console.WriteLine("      (ninguna)");
    }

    if (debug && ChestReplayReader.LastDiagnostics is { } lines)
    {
        Console.WriteLine("   Diagnostico (posiciones, botin y contenedores cerca de mi):");
        foreach (var line in lines)
        {
            Console.WriteLine("      " + line);
        }
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
