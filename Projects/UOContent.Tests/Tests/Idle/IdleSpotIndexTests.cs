using System;
using System.Collections.Generic;
using Server;
using Server.Custom.Idle;
using Xunit;

namespace UOContent.Tests.Idle;

[Collection("Sequential UOContent Tests")]
public class IdleSpotIndexTests : IDisposable
{
    public IdleSpotIndexTests() => IdleSpotIndex.Clear();

    public void Dispose() => IdleSpotIndex.Clear();

    private static IdleSpot Spot(string id, string task, int x, int y, Point3D? stand = null) =>
        new() { Id = id, TaskId = task, Map = Map.Felucca, Location = new Point3D(x, y, 0), Stand = stand };

    [Fact]
    public void FindsNearestFirstAcrossCells()
    {
        IdleSpotIndex.Add(Spot("far", "mining", 1000, 1000));
        IdleSpotIndex.Add(Spot("near", "mining", 1530, 1530));
        IdleSpotIndex.Add(Spot("mid", "mining", 1700, 1500));
        IdleSpotIndex.Add(Spot("tree", "lumberjacking", 1501, 1501));

        var results = new List<(IdleLocation Location, int Distance)>();
        IdleSpotIndex.FindNearest(Map.Felucca, new Point3D(1500, 1500, 0), "Mining", 2, 0, results);

        Assert.Equal(2, results.Count);
        Assert.Equal("near", results[0].Location.Id);
        Assert.Equal(42, results[0].Distance);
        Assert.Equal("mid", results[1].Location.Id);
    }

    [Fact]
    public void MeasuresToTheStandingTileAndHonorsMaxDistance()
    {
        IdleSpotIndex.Add(Spot("cliff", "mining", 1500, 1510, new Point3D(1500, 1600, 0)));

        var results = new List<(IdleLocation Location, int Distance)>();
        IdleSpotIndex.FindNearest(Map.Felucca, new Point3D(1500, 1500, 0), "mining", 5, 50, results);
        Assert.Empty(results);

        IdleSpotIndex.FindNearest(Map.Felucca, new Point3D(1500, 1500, 0), "mining", 5, 0, results);
        Assert.Equal(100, Assert.Single(results).Distance);
    }

    [Fact]
    public void OtherMapsAndTasksAreSeparate()
    {
        IdleSpotIndex.Add(Spot("tree", "lumberjacking", 10, 10));

        var results = new List<(IdleLocation Location, int Distance)>();
        IdleSpotIndex.FindNearest(Map.Trammel, new Point3D(10, 10, 0), "lumberjacking", 5, 0, results);
        Assert.Empty(results);

        IdleSpotIndex.FindNearest(Map.Felucca, new Point3D(10, 10, 0), "fishing", 5, 0, results);
        Assert.Empty(results);
    }
}
