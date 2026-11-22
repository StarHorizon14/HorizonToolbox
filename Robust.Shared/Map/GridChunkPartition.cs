using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Robust.Shared.Map
{
    /// <summary>
    /// Partitions a grid chunk into a set of non-overlapping rectangles.
    /// </summary>
    internal static class GridChunkPartition
    {
        /// <summary>
        ///
        /// </summary>
        /// <param name="chunk"></param>
        /// <param name="bounds">The overall bounds that covers every rectangle.</param>
        /// <param name="rectangles">Each individual rectangle comprising the chunk's bounds</param>
        public static void PartitionChunk(MapChunk chunk, out Box2i bounds, out List<Box2i> rectangles)
        {
            PartitionChunk(chunk, null, out bounds, out rectangles);
        }

        /// <summary>
        /// Horizon: returns true if the tile is a <see cref="ITileDefinition.GridUnderlay"/> tile.
        /// </summary>
        public static bool IsGridUnderlay(ITileDefinitionManager tileDefs, Tile tile)
        {
            return !tile.IsEmpty &&
                   tileDefs.TryGetDefinition(tile.TypeId, out var def) &&
                   def.GridUnderlay;
        }

        /// <summary>
        /// Horizon: returns true if the chunk has at least one <see cref="ITileDefinition.GridUnderlay"/> tile.
        /// </summary>
        public static bool HasGridUnderlay(MapChunk chunk, ITileDefinitionManager tileDefs)
        {
            if (chunk.HasUnderlayCache is { } cached)
                return cached;

            var found = false;

            for (ushort y = 0; y < chunk.ChunkSize && !found; y++)
            {
                for (ushort x = 0; x < chunk.ChunkSize; x++)
                {
                    if (!IsGridUnderlay(tileDefs, chunk.GetTile(x, y)))
                        continue;

                    found = true;
                    break;
                }
            }

            chunk.HasUnderlayCache = found;
            return found;
        }

        /// <summary>
        /// Horizon: does this anchored entity make the underlay tile it stands on solid for other grids?
        /// True for anything other things can bump into: an entity with a hard fixture that has a collision layer.
        /// Whether it can collide right now does not matter, so a door blocks open or closed.
        /// </summary>
        public static bool IsGridBlocker(EntityUid uid, EntityQuery<FixturesComponent> fixturesQuery)
        {
            if (!fixturesQuery.TryComp(uid, out var fixtures))
                return false;

            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (fixture.Hard && fixture.CollisionLayer != 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Horizon: is there a <see cref="IsGridBlocker">blocker</see> anchored on this tile of the chunk?
        /// </summary>
        public static bool HasGridBlocker(MapChunk chunk, ushort x, ushort y, EntityQuery<FixturesComponent> fixturesQuery)
        {
            var anchored = chunk.GetSnapGrid(x, y);

            if (anchored == null)
                return false;

            foreach (var uid in anchored)
            {
                if (IsGridBlocker(uid, fixturesQuery))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Horizon: partitions the chunk the same way, but if <paramref name="collisionTileDefs"/> is given
        /// then <see cref="ITileDefinition.GridUnderlay"/> tiles count as empty.
        /// Used for collision only: bounds and grid splitting must keep using every tile.
        /// </summary>
        public static void PartitionChunk(
            MapChunk chunk,
            ITileDefinitionManager? collisionTileDefs,
            out Box2i bounds,
            out List<Box2i> rectangles)
        {
            PartitionChunk(chunk, collisionTileDefs, null, out bounds, out rectangles);
        }

        /// <summary>
        /// Horizon: as above, but if <paramref name="blockerQuery"/> is given then an underlay tile
        /// with a <see cref="IsGridBlocker">blocker</see> anchored on it (a wall on a landing pad) stays solid.
        /// </summary>
        public static void PartitionChunk(
            MapChunk chunk,
            ITileDefinitionManager? collisionTileDefs,
            EntityQuery<FixturesComponent>? blockerQuery,
            out Box2i bounds,
            out List<Box2i> rectangles)
        {
            rectangles = new List<Box2i>();

            // TODO: Use the existing PartitionChunk version because that one is likely faster and you can Span that shit.
            // Convert each line into boxes as long as they can be.
            for (ushort y = 0; y < chunk.ChunkSize; y++)
            {
                var origin = 0;
                var running = false;

                for (ushort x = 0; x < chunk.ChunkSize; x++)
                {
                    var tile = chunk.GetTile(x, y);

                    if (!tile.IsEmpty &&
                        (collisionTileDefs == null ||
                         !IsGridUnderlay(collisionTileDefs, tile) ||
                         blockerQuery != null && HasGridBlocker(chunk, x, y, blockerQuery.Value)))
                    {
                        running = true;
                        continue;
                    }

                    // Still empty
                    if (running)
                    {
                        rectangles.Add(new Box2i(origin, y, x, y + 1));
                    }

                    origin = x + 1;
                    running = false;
                }

                if (running)
                    rectangles.Add(new Box2i(origin, y, chunk.ChunkSize, y + 1));
            }

            // Patch them together as available
            for (var i = rectangles.Count - 1; i >= 0; i--)
            {
                var box = rectangles[i];
                for (var j = i - 1; j >= 0; j--)
                {
                    var other = rectangles[j];

                    // Gone down as far as we can go.
                    if (other.Top < box.Bottom) break;

                    if (box.Left == other.Left && box.Right == other.Right)
                    {
                        box = new Box2i(box.Left, other.Bottom, box.Right, box.Top);
                        rectangles[i] = box;
                        rectangles.RemoveAt(j);
                        i -= 1;
                        continue;
                    }
                }
            }

            bounds = new Box2i();

            foreach (var rectangle in rectangles)
            {
                bounds = bounds.IsEmpty() ? rectangle : bounds.Union(rectangle);
            }
        }
    }
}
