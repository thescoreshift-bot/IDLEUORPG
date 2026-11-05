using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server.Commands;
using Server.Engines.CharacterCreation;
using Server.Engines.Spawners;
using Server.Logging;
using Server.Mobiles;
using Server.Text;

namespace Server.Custom.Idle;

/// <summary>
/// [IdleStarterReport: for each starting city the shard offers, the straight-line distance to the nearest tree, mine,
/// water, forge, and bank, so it is clear which cities need closer idle spots. Writes Reports/idle-starter-report.csv.
/// </summary>
public static class IdleStarterReport
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(IdleStarterReport));

    /// <summary>Distances above this are flagged as far in the report.</summary>
    private const int FarDistance = 100;

    /// <summary>How far from a city to look for a banker or banker spawner.</summary>
    private const int BankSearchRange = 400;

    /// <summary>Spots per task checked for one that is reachable on foot when the nearest is not.</summary>
    private const int ReachableSearch = 25;

    private static readonly (string Label, string TaskId)[] _columns =
    [
        ("Tree", "lumberjacking"),
        ("Mine", "mining"),
        ("Water", "fishing"),
        ("Forge", "smelting")
    ];

    public static readonly string CsvPath = Path.Combine(Core.BaseDirectory, "Reports", "idle-starter-report.csv");

    public record Cell(string Label, string SpotId, int Distance, string Direction, bool? Reachable, int? ReachableDistance, string Source);

    public record Row(string City, Map Map, Point3D Location, List<Cell> Cells);

    public static void Configure()
    {
        CommandSystem.Register("IdleStarterReport", AccessLevel.GameMaster, OnCommand);
        ConsoleInputHandler.RegisterCommand(
            ["idlestarter"],
            "Idle starter report: distance from each starting city to the nearest tree, mine, water, forge, and bank",
            _ => Core.LoopContext.Post(() => Run(null))
        );
    }

    [Usage("IdleStarterReport")]
    [Description(
        "For each starting city: straight-line distance to the nearest tree, mine, water, forge, and bank, with " +
        "reachability on foot. Writes Reports/idle-starter-report.csv."
    )]
    private static void OnCommand(CommandEventArgs e) => Run(e.Mobile);

    public static List<Row> Build()
    {
        var rows = new List<Row>();
        var spawnData = new Dictionary<Map, List<Point3D>>();

        foreach (var city in CharacterCreation.GetStartingCities())
        {
            if (city.Map == null || city.Map == Map.Internal)
            {
                continue;
            }

            var cells = new List<Cell>();
            foreach (var (label, taskId) in _columns)
            {
                cells.Add(SpotCell(label, taskId, city.Map, city.Location));
            }

            cells.Add(BankCell(city.Map, city.Location, spawnData));
            rows.Add(new Row(city.City, city.Map, city.Location, cells));
        }

        return rows;
    }

    private static Cell SpotCell(string label, string taskId, Map map, Point3D from)
    {
        var results = new List<(IdleLocation Location, int Distance)>();
        IdleSpotIndex.FindNearest(map, from, taskId, ReachableSearch, 0, results);

        if (results.Count == 0)
        {
            return new Cell(label, null, -1, null, null, null, "none");
        }

        var (nearest, distance) = results[0];
        var reachable = (nearest as IdleSpot)?.Reachable;
        int? reachableDistance = null;

        // Hand-written locations carry no reachability tag; they are treated as reachable.
        foreach (var (loc, d) in results)
        {
            if ((loc as IdleSpot)?.Reachable != false)
            {
                reachableDistance = d;
                break;
            }
        }

        var source = nearest is IdleSpot { Generated: true } ? "generated" : "hand-written";
        return new Cell(
            label, nearest.Id, distance, IdleWorkHere.DirectionName(from, IdleSpotIndex.StandPoint(nearest)), reachable,
            reachableDistance, source
        );
    }

    private static Cell BankCell(Map map, Point3D from, Dictionary<Map, List<Point3D>> spawnData)
    {
        Point3D? best = null;
        var bestDistance = int.MaxValue;
        string source = null;

        void Consider(Point3D p, string origin)
        {
            var d = IdleSpotIndex.Distance(from, p);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = p;
                source = origin;
            }
        }

        foreach (var banker in map.GetMobilesInRange<Banker>(from, BankSearchRange))
        {
            Consider(banker.Location, "banker");
        }

        if (best == null)
        {
            foreach (var spawner in map.GetItemsInRange<BaseSpawner>(from, BankSearchRange))
            {
                foreach (var entry in spawner.Entries)
                {
                    if (entry.SpawnedName.InsensitiveEquals("Banker"))
                    {
                        Consider(spawner.Location, "banker spawner (none spawned)");
                        break;
                    }
                }
            }
        }

        if (best == null)
        {
            foreach (var p in SpawnDataBankers(map, spawnData))
            {
                if (Utility.InRange(from, p, BankSearchRange))
                {
                    Consider(p, "spawn data only (no banker in the world)");
                }
            }
        }

        return best == null
            ? new Cell("Bank", null, -1, null, null, null, "none")
            : new Cell("Bank", null, bestDistance, IdleWorkHere.DirectionName(from, best.Value), null, null, source);
    }

    /// <summary>Banker spawner positions from the era-neutral spawn files, for worlds where spawners were never placed.</summary>
    private static List<Point3D> SpawnDataBankers(Map map, Dictionary<Map, List<Point3D>> cache)
    {
        if (cache.TryGetValue(map, out var list))
        {
            return list;
        }

        cache[map] = list = [];
        var path = Path.Combine(Core.BaseDirectory, "Data", "Spawns", "shared", map.Name.ToLowerInvariant(), "Vendors.json");

        if (!File.Exists(path))
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var spawner in doc.RootElement.EnumerateArray())
            {
                if (!spawner.TryGetProperty("entries", out var entries) || !spawner.TryGetProperty("location", out var loc))
                {
                    continue;
                }

                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty("name", out var name) && name.GetString().InsensitiveEquals("Banker"))
                    {
                        list.Add(new Point3D(loc[0].GetInt32(), loc[1].GetInt32(), loc[2].GetInt32()));
                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            logger.Warning(e, "Could not read banker spawns from {Path}", path);
        }

        return list;
    }

    private static void Run(Mobile from)
    {
        var rows = Build();

        void Say(string line)
        {
            if (from == null)
            {
                logger.Information("{Line}", line);
            }
            else
            {
                from.SendMessage(line);
            }
        }

        if (rows.Count == 0)
        {
            Say("No starting cities are available.");
            return;
        }

        Say($"Idle starter report: straight-line tiles from each starting inn; ! = over {FarDistance} tiles.");

        foreach (var row in rows)
        {
            using var line = ValueStringBuilder.Create();
            line.Append($"{row.City}:");

            foreach (var cell in row.Cells)
            {
                line.Append($" {cell.Label} {CellText(cell)};");
            }

            Say(line.ToString());
        }

        try
        {
            WriteCsv(rows);
            Say($"Saved to Reports/idle-starter-report.csv ({rows.Count} cities).");
        }
        catch (Exception e)
        {
            logger.Error(e, "Could not write {Path}", CsvPath);
            Say("Could not write the CSV file; see the server log.");
        }
    }

    /// <summary>"17", "140!", or "40 (not on foot; 95 on foot)".</summary>
    private static string CellText(Cell cell)
    {
        if (cell.Distance < 0)
        {
            return "none";
        }

        using var sb = ValueStringBuilder.Create();
        sb.Append($"{cell.Distance}");

        if (cell.Distance > FarDistance)
        {
            sb.Append('!');
        }

        if (cell.Reachable == false)
        {
            if (cell.ReachableDistance is { } d)
            {
                sb.Append($" (not on foot; {d} on foot)");
            }
            else
            {
                sb.Append(" (not on foot)");
            }
        }

        return sb.ToString();
    }

    private static void WriteCsv(List<Row> rows)
    {
        using var csv = ValueStringBuilder.Create();
        csv.Append("City,Map,X,Y");

        foreach (var cell in rows[0].Cells)
        {
            if (cell.Label == "Bank")
            {
                csv.Append(",Bank tiles,Bank direction,Bank source");
            }
            else
            {
                csv.Append(
                    $",{cell.Label} tiles,{cell.Label} direction,{cell.Label} reachable on foot," +
                    $"{cell.Label} nearest on foot (tiles),{cell.Label} spot"
                );
            }
        }

        csv.Append("\r\n");

        foreach (var row in rows)
        {
            csv.Append($"{row.City},{row.Map.Name},{row.Location.X},{row.Location.Y}");

            foreach (var cell in row.Cells)
            {
                var tiles = cell.Distance < 0 ? "" : cell.Distance.ToString();

                if (cell.Label == "Bank")
                {
                    csv.Append($",{tiles},{cell.Direction},{cell.Source}");
                    continue;
                }

                var reachable = cell.Reachable switch
                {
                    true  => "yes",
                    false => "no",
                    _     => cell.SpotId == null ? "" : "unknown"
                };

                csv.Append($",{tiles},{cell.Direction},{reachable},{cell.ReachableDistance},{cell.SpotId}");
            }

            csv.Append("\r\n");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(CsvPath)!);
        File.WriteAllText(CsvPath, csv.ToString());
    }
}
