﻿using Obsidian.API.BlockStates;
using Obsidian.API.BlockStates.Builders;
using Obsidian.Utilities.Registry;

namespace Obsidian.WorldData;

internal static class BlockUpdates
{
    internal static async Task<bool> HandleFallingBlock(BlockUpdate blockUpdate)
    {
        if (blockUpdate.Block is null) { return false; }

        var world = blockUpdate.world;
        var location = blockUpdate.position;
        var material = blockUpdate.Block.Material;
        if (await world.GetBlockAsync(location + Vector.Down) is IBlock below &&
            (TagsRegistry.Blocks.ReplaceableByWater.Entries.Contains(below.RegistryId) || below.IsLiquid))
        {
            await world.SetBlockAsync(location, BlocksRegistry.Air);
            world.SpawnFallingBlock(location, material);
            return true;
        }

        return false;
    }

    internal static async Task<bool> HandleLiquidPhysicsAsync(BlockUpdate blockUpdate)
    {
        if (blockUpdate.Block is null) { return false; }

        var block = blockUpdate.Block;
        var world = blockUpdate.world;
        var location = blockUpdate.position;
        int waterLevel = block.State is WaterState water ? water.Level : 0;
        Vector belowPos = location + Vector.Down;

        // Handle the initial search for closet path downwards.
        // Just going to do a crappy pathfind for now. We can do
        // proper pathfinding some other time.
        if (waterLevel == 0)
        {
            var validPaths = new List<Vector>();
            var paths = new List<Vector>() {
                    {location + Vector.Forwards},
                    {location + Vector.Backwards},
                    {location + Vector.Left},
                    {location + Vector.Right}
                };

            foreach (var pathLoc in paths)
            {
                if (await world.GetBlockAsync(pathLoc) is IBlock pathSide &&
                    (TagsRegistry.Blocks.ReplaceableByWater.Entries.Contains(pathSide.RegistryId) || pathSide.IsLiquid))
                {
                    var pathBelow = await world.GetBlockAsync(pathLoc + Vector.Down);
                    if (pathBelow is IBlock pBelow &&
                        (TagsRegistry.Blocks.ReplaceableByWater.Entries.Contains(pBelow.RegistryId) || pBelow.IsLiquid))
                    {
                        validPaths.Add(pathLoc);
                    }
                }
            }

            // if all directions are valid, or none are, use normal liquid spread physics instead
            if (validPaths.Count != 4 && validPaths.Count != 0)
            {
                var path = validPaths[0];
                var newBlock = BlocksRegistry.Get(Material.Water, new WaterStateBuilder().WithLevel(waterLevel + 1).Build());
                await world.SetBlockAsync(path, newBlock);
                var neighborUpdate = new BlockUpdate(world, path, newBlock);
                await world.ScheduleBlockUpdateAsync(neighborUpdate);
                return false;
            }
        }

        if (waterLevel >= 8) // Falling water
        {
            // If above me is no longer water, than I should disappear too
            if (await world.GetBlockAsync(location + Vector.Up) is IBlock up && !up.IsLiquid)
            {
                await world.SetBlockAsync(location, BlocksRegistry.Air);
                await world.ScheduleBlockUpdateAsync(new BlockUpdate(world, belowPos));
                return false;
            }

            // Keep falling
            if (await world.GetBlockAsync(belowPos) is IBlock below && TagsRegistry.Blocks.ReplaceableByWater.Entries.Contains(below.RegistryId))
            {
                var newBlock = BlocksRegistry.Get(Material.Water, new WaterStateBuilder().WithLevel(waterLevel).Build());
                await world.SetBlockAsync(belowPos, newBlock);
                await world.ScheduleBlockUpdateAsync(new BlockUpdate(world, belowPos, newBlock));
                return false;
            }
            else
            {
                // Falling water has hit something solid. Change state to spread.
                waterLevel = 1;
                await world.SetBlockUntrackedAsync(location, BlocksRegistry.Get(Material.Water, new WaterStateBuilder().WithLevel(waterLevel).Build()));
            }
        }

        if (waterLevel < 8)
        {
            var horizontalNeighbors = new Dictionary<Vector, IBlock?>() {
                    {location + Vector.Forwards, await world.GetBlockAsync(location + Vector.Forwards)},
                    {location + Vector.Backwards, await world.GetBlockAsync(location + Vector.Backwards)},
                    {location + Vector.Left, await world.GetBlockAsync(location + Vector.Left)},
                    {location + Vector.Right, await world.GetBlockAsync(location + Vector.Right)}
                };

            // Check infinite source blocks
            if (waterLevel == 1 && block.IsLiquid)
            {
                // if 2 neighbors are source blocks (state = 0), then become source block
                int sourceNeighborCount = 0;
                foreach (var (loc, neighborBlock) in horizontalNeighbors)
                {
                    if (neighborBlock is IBlock neighbor && neighbor.Is(block.Material) &&
                        ((WaterState)neighbor.State).Level == 0)
                    {
                        sourceNeighborCount++;
                    }
                }

                if (sourceNeighborCount > 1)
                {
                    await world.SetBlockAsync(location, BlocksRegistry.Water);
                    return true;
                }
            }

            if (waterLevel > 0)
            {
                // On some side of the block, there should be another water block with a lower state.
                int lowestState = waterLevel;
                foreach (var (loc, neighborBlock) in horizontalNeighbors)
                {
                    var neighborState = neighborBlock.State is WaterState neighborWater ? neighborWater.Level : 0; 

                    if (neighborBlock.Material == block.Material)
                        lowestState = Math.Min(lowestState, neighborState);
                }

                // If not, turn to air and update neighbors.
                var bUp = await world.GetBlockAsync(location + Vector.Up);
                if (lowestState >= waterLevel && bUp.Material != block.Material)
                {
                    await world.SetBlockAsync(location, BlocksRegistry.Air);
                    return true;
                }
            }

            if (await world.GetBlockAsync(belowPos) is IBlock below)
            {
                if (below.Material == block.Material) { return false; }

                if (TagsRegistry.Blocks.ReplaceableByWater.Entries.Contains(below.RegistryId))
                {
                    var newBlock = BlocksRegistry.Get(Material.Water, new WaterStateBuilder().WithLevel(waterLevel + 8).Build());
                    await world.SetBlockAsync(belowPos, newBlock);
                    var neighborUpdate = new BlockUpdate(world, belowPos, newBlock);
                    await world.ScheduleBlockUpdateAsync(neighborUpdate);
                    return false;
                }
            }

            // the lowest level of water can only go down, so bail now.
            if (waterLevel == 7) { return false; }

            foreach (var (loc, neighborBlock) in horizontalNeighbors)
            {
                if (neighborBlock is null) { continue; }

                var neighborState = neighborBlock.State is WaterState neighborWater ? neighborWater.Level : 0;

                if (TagsRegistry.Blocks.ReplaceableByWater.Entries.Contains(neighborBlock.RegistryId) ||
                    (neighborBlock.IsLiquid && neighborState > waterLevel + 1))
                {
                    var newBlock = BlocksRegistry.Get(Material.Water, new WaterStateBuilder().WithLevel(waterLevel + 1).Build());
                    await world.SetBlockAsync(loc, newBlock);
                    var neighborUpdate = new BlockUpdate(world, loc, newBlock);
                    await world.ScheduleBlockUpdateAsync(neighborUpdate);
                }
            }
        }

        return false;
    }
}
