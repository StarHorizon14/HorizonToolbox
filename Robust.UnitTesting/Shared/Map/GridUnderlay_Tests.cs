using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Map;

/// <summary>
/// Horizon: tests for <see cref="ITileDefinition.GridUnderlay"/> tiles (landing pads).
/// </summary>
[Parallelizable(ParallelScope.All | ParallelScope.Fixtures)]
[TestFixture]
public sealed class GridUnderlay_Tests
{
    private ISimulation GetSim(out Tile floor, out Tile pad)
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        sim.Resolve<IConfigurationManager>().SetCVar(CVars.GridSplitting, true);

        var tileDefs = sim.Resolve<ITileDefinitionManager>();
        tileDefs.Register(new UnderlayTestTileDef("space"));
        var floorDef = new UnderlayTestTileDef("floor");
        var padDef = new UnderlayTestTileDef("pad") { GridUnderlay = true };
        tileDefs.Register(floorDef);
        tileDefs.Register(padDef);

        floor = new Tile(floorDef.TileId);
        pad = new Tile(padDef.TileId);
        return sim;
    }

    /// <summary>
    /// An underlay tile has no collision, but it still holds the grid together and counts for its bounds.
    /// </summary>
    [Test]
    public void UnderlayHasNoFixtureAndDoesNotSplit()
    {
        var sim = GetSim(out var floor, out var pad);
        var entManager = sim.Resolve<IEntityManager>();
        var mapManager = sim.Resolve<IMapManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var mapId = sim.CreateMap().MapId;
        var grid = mapManager.CreateGridEntity(mapId);
        var fixtures = entManager.GetComponent<FixturesComponent>(grid);

        mapSystem.SetTiles(grid, new List<(Vector2i GridIndices, Tile Tile)>
        {
            (new Vector2i(0, 0), floor),
            (new Vector2i(1, 0), pad),
            (new Vector2i(2, 0), floor),
        });

        Assert.Multiple(() =>
        {
            // One fixture on each side of the pad, none for the pad.
            Assert.That(fixtures.FixtureCount, Is.EqualTo(2));
            Assert.That(grid.Comp.LocalAABB, Is.EqualTo(new Box2(0f, 0f, 3f, 1f)));
            Assert.That(mapManager.GetAllGrids(mapId).Count(), Is.EqualTo(1));
        });

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            var aabb = fixture.Shape.ComputeAABB(Transform.Empty, 0);
            Assert.That(aabb.Contains(new Vector2(1.5f, 0.5f)), Is.False, "Pad tile is covered by a grid fixture");
        }

        // A grid made only of pad tiles has no collision at all.
        var padGrid = mapManager.CreateGridEntity(mapId);
        mapSystem.SetTile(padGrid, Vector2i.Zero, pad);
        Assert.That(entManager.GetComponent<FixturesComponent>(padGrid).FixtureCount, Is.EqualTo(0));
        Assert.That(padGrid.Comp.LocalAABB, Is.EqualTo(new Box2(0f, 0f, 1f, 1f)));
    }

    /// <summary>
    /// Replacing a normal tile with an underlay tile (and back) updates collision even though the shape is the same.
    /// </summary>
    [Test]
    public void SwappingTileRegeneratesCollision()
    {
        var sim = GetSim(out var floor, out var pad);
        var entManager = sim.Resolve<IEntityManager>();
        var mapManager = sim.Resolve<IMapManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var mapId = sim.CreateMap().MapId;
        var grid = mapManager.CreateGridEntity(mapId);
        var fixtures = entManager.GetComponent<FixturesComponent>(grid);

        for (var x = 0; x < 3; x++)
        {
            mapSystem.SetTile(grid, new Vector2i(x, 0), floor);
        }

        Assert.That(fixtures.FixtureCount, Is.EqualTo(1));

        mapSystem.SetTile(grid, new Vector2i(1, 0), pad);
        Assert.That(fixtures.FixtureCount, Is.EqualTo(2));
        Assert.That(mapManager.GetAllGrids(mapId).Count(), Is.EqualTo(1));

        mapSystem.SetTile(grid, new Vector2i(1, 0), floor);
        Assert.That(fixtures.FixtureCount, Is.EqualTo(1));
    }

    /// <summary>
    /// Something solid anchored on an underlay tile (a wall on a landing pad) makes that tile collide again,
    /// and stops doing so when it is unanchored or deleted. Things with no collision layer (cables) change nothing.
    /// </summary>
    [Test]
    public void AnchoredBlockerMakesUnderlaySolid()
    {
        var sim = GetSim(out _, out var pad);
        var entManager = sim.Resolve<IEntityManager>();
        var mapManager = sim.Resolve<IMapManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var xformSystem = entManager.System<SharedTransformSystem>();
        var fixtureSystem = entManager.System<FixtureSystem>();
        var mapId = sim.CreateMap().MapId;
        var grid = mapManager.CreateGridEntity(mapId);
        var fixtures = entManager.GetComponent<FixturesComponent>(grid);

        for (var x = 0; x < 3; x++)
        {
            mapSystem.SetTile(grid, new Vector2i(x, 0), pad);
        }

        Assert.That(fixtures.FixtureCount, Is.EqualTo(0));

        EntityUid Spawn(float x, int layer)
        {
            var uid = entManager.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(x, 0.5f)));
            entManager.AddComponent<PhysicsComponent>(uid);
            Assert.That(fixtureSystem.TryCreateFixture(uid, new PhysShapeAabb(), "fix", collisionLayer: layer));
            return uid;
        }

        // A cable: a fixture with no layer.
        var cable = Spawn(0.5f, 0);
        Assert.That(xformSystem.AnchorEntity(cable));
        Assert.That(fixtures.FixtureCount, Is.EqualTo(0), "An entity with no collision layer made the pad solid");

        // A wall.
        var wall = Spawn(1.5f, 2);
        Assert.That(xformSystem.AnchorEntity(wall));
        Assert.That(fixtures.FixtureCount, Is.EqualTo(1), "A wall on the pad did not make its tile solid");

        var aabb = fixtures.Fixtures.Values.First().Shape.ComputeAABB(Transform.Empty, 0);
        Assert.Multiple(() =>
        {
            Assert.That(aabb.Contains(new Vector2(1.5f, 0.5f)), "The fixture is not under the wall");
            Assert.That(aabb.Contains(new Vector2(0.5f, 0.5f)), Is.False, "The fixture also covers the cable's tile");
            Assert.That(aabb.Contains(new Vector2(2.5f, 0.5f)), Is.False, "The fixture also covers a bare pad tile");
        });

        xformSystem.Unanchor(wall);
        Assert.That(fixtures.FixtureCount, Is.EqualTo(0), "The tile stayed solid after the wall was unanchored");

        Assert.That(xformSystem.AnchorEntity(wall));
        Assert.That(fixtures.FixtureCount, Is.EqualTo(1));

        entManager.DeleteEntity(wall);
        Assert.That(fixtures.FixtureCount, Is.EqualTo(0), "The tile stayed solid after the wall was deleted");
        Assert.That(mapManager.GetAllGrids(mapId).Count(), Is.EqualTo(1));
    }

    /// <summary>
    /// Where a normal tile of one grid overlaps an underlay tile of another, the normal tile's grid is found,
    /// whichever grid was made first.
    /// </summary>
    [Test]
    public void FindGridAtPrefersNormalTile([Values] bool padGridFirst)
    {
        var sim = GetSim(out var floor, out var pad);
        var entManager = sim.Resolve<IEntityManager>();
        var mapManager = sim.Resolve<IMapManager>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var map = sim.CreateMap();

        Entity<MapGridComponent> host;
        Entity<MapGridComponent> shuttle;

        if (padGridFirst)
        {
            host = mapManager.CreateGridEntity(map.MapId);
            shuttle = mapManager.CreateGridEntity(map.MapId);
        }
        else
        {
            shuttle = mapManager.CreateGridEntity(map.MapId);
            host = mapManager.CreateGridEntity(map.MapId);
        }

        // Host: pad at (0,0) and (1,0). Shuttle: floor at (0,0) only. Both at the origin.
        mapSystem.SetTile(host, new Vector2i(0, 0), pad);
        mapSystem.SetTile(host, new Vector2i(1, 0), pad);
        mapSystem.SetTile(shuttle, new Vector2i(0, 0), floor);

        Assert.Multiple(() =>
        {
            // Overlap: the shuttle is on top.
            Assert.That(mapManager.TryFindGridAt(map.Uid, new Vector2(0.5f, 0.5f), out var found, out _));
            Assert.That(found, Is.EqualTo(shuttle.Owner));

            // Bare pad: the host.
            Assert.That(mapManager.TryFindGridAt(map.Uid, new Vector2(1.5f, 0.5f), out found, out _));
            Assert.That(found, Is.EqualTo(host.Owner));

            // Nothing there.
            Assert.That(mapManager.TryFindGridAt(map.Uid, new Vector2(5.5f, 0.5f), out _, out _), Is.False);
        });
    }
}

/// <summary>
/// Tile definition for <see cref="GridUnderlay_Tests"/>.
/// </summary>
[Prototype("testUnderlayTileDef")]
public sealed partial class UnderlayTestTileDef : ITileDefinition
{
    public UnderlayTestTileDef()
    {
    }

    public UnderlayTestTileDef(string id)
    {
        ID = id;
    }

    public ushort TileId { get; set; }
    public string Name => ID;

    [IdDataField]
    public string ID { get; private set; } = default!;

    public ResPath? Sprite => null;
    public Dictionary<Direction, ResPath> EdgeSprites => new();
    public int EdgeSpritePriority => 0;
    public float Friction => 0;
    public byte Variants => 0;
    public bool GridUnderlay { get; set; }
    public void AssignTileId(ushort id) => TileId = id;
}
