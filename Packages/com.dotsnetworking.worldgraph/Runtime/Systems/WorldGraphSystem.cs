using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DotsNetworking.WorldGraph
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct WorldGraphSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldGraph>();
            state.RequireForUpdate<WorldGraphRequests>();
            
            // Initialize Singletons if they don't exist
            if (!state.EntityManager.HasComponent<WorldGraph>(state.SystemHandle))
            {
                var wg = new WorldGraph
                {
                    RegionIdByKey = new NativeParallelHashMap<int3, int>(64, Allocator.Persistent),
                    RegionsById = new NativeList<RegionEntry>(16, Allocator.Persistent),
                    RegionPinWords = new NativeList<int>(16, Allocator.Persistent),
                    FreeRegionIds = new NativeQueue<int>(Allocator.Persistent)
                };
                
                // Add a dummy entry for index 0 (NeverExisted)
                wg.RegionsById.Add(new RegionEntry { State = RegionState.NeverExisted });
                wg.RegionPinWords.Add(0);

                state.EntityManager.AddComponentData(state.SystemHandle, wg);
            }

            if (!state.EntityManager.HasComponent<WorldGraphRequests>(state.SystemHandle))
            {
                state.EntityManager.AddComponentData(state.SystemHandle, new WorldGraphRequests
                {
                    RequestLoadQ = new NativeQueue<int3>(Allocator.Persistent),
                    RequestUnloadQ = new NativeQueue<int3>(Allocator.Persistent),
                    PendingLoad = new NativeParallelHashSet<int3>(64, Allocator.Persistent),
                    PendingUnload = new NativeParallelHashSet<int3>(64, Allocator.Persistent)
                });
            }

            if (!state.EntityManager.HasComponent<WorldGraphFeedback>(state.SystemHandle))
            {
                state.EntityManager.AddComponentData(state.SystemHandle, new WorldGraphFeedback
                {
                    UnloadDeferredQ = new NativeQueue<int3>(Allocator.Persistent),
                    UnloadDeferredReasonQ = new NativeQueue<UnloadDeferReason>(Allocator.Persistent)
                });
            }
            
            if(!state.EntityManager.HasComponent<NavRuntime>(state.SystemHandle))
            {
                state.EntityManager.AddComponentData(state.SystemHandle, new NavRuntime
                {
                     NodeToOccId = new NativeParallelHashMap<ulong, int>(1024, Allocator.Persistent),
                     OccAtomicWord = new NativeList<int>(1024, Allocator.Persistent),
                     Reserved = new NativeList<uint>(1024, Allocator.Persistent),
                     NewNodeQ = new NativeQueue<ulong>(Allocator.Persistent)
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.EntityManager.HasComponent<WorldGraph>(state.SystemHandle))
            {
                var wg = state.EntityManager.GetComponentData<WorldGraph>(state.SystemHandle);
                if (wg.RegionIdByKey.IsCreated) wg.RegionIdByKey.Dispose();
                if (wg.RegionsById.IsCreated) wg.RegionsById.Dispose();
                if (wg.RegionPinWords.IsCreated) wg.RegionPinWords.Dispose();
                if (wg.FreeRegionIds.IsCreated) wg.FreeRegionIds.Dispose();
            }

            if (state.EntityManager.HasComponent<WorldGraphRequests>(state.SystemHandle))
            {
                var wgr = state.EntityManager.GetComponentData<WorldGraphRequests>(state.SystemHandle);
                if (wgr.RequestLoadQ.IsCreated) wgr.RequestLoadQ.Dispose();
                if (wgr.RequestUnloadQ.IsCreated) wgr.RequestUnloadQ.Dispose();
                if (wgr.PendingLoad.IsCreated) wgr.PendingLoad.Dispose();
                if (wgr.PendingUnload.IsCreated) wgr.PendingUnload.Dispose();
            }

            if (state.EntityManager.HasComponent<WorldGraphFeedback>(state.SystemHandle))
            {
                var wgf = state.EntityManager.GetComponentData<WorldGraphFeedback>(state.SystemHandle);
                if (wgf.UnloadDeferredQ.IsCreated) wgf.UnloadDeferredQ.Dispose();
                if (wgf.UnloadDeferredReasonQ.IsCreated) wgf.UnloadDeferredReasonQ.Dispose();
            }

             if(state.EntityManager.HasComponent<NavRuntime>(state.SystemHandle))
            {
                var nr = state.EntityManager.GetComponentData<NavRuntime>(state.SystemHandle);
                if(nr.NodeToOccId.IsCreated) nr.NodeToOccId.Dispose();
                if(nr.OccAtomicWord.IsCreated) nr.OccAtomicWord.Dispose();
                if(nr.Reserved.IsCreated) nr.Reserved.Dispose();
                if(nr.NewNodeQ.IsCreated) nr.NewNodeQ.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            var worldGraph = SystemAPI.GetSingleton<WorldGraph>();
            var requests = SystemAPI.GetSingleton<WorldGraphRequests>();
            var feedback = SystemAPI.GetSingleton<WorldGraphFeedback>();

            // 1. Sync Point: Apply New Region IDs / Structural Allocations
            // (In a real implementation, this would handle allocating IDs for new regions requested)
            
            // 2. Drain Load/Unload Request Queues
            while (requests.RequestLoadQ.TryDequeue(out int3 regionKey))
            {
                ProcessLoadRequest(ref worldGraph, regionKey);
            }

            while (requests.RequestUnloadQ.TryDequeue(out int3 regionKey))
            {
                ProcessUnloadRequest(ref worldGraph, regionKey);
            }

            // 3. Progress Loads
            // Iterate over RegionsById and check if any are Loading, then advance them.

            // 4. Progress Unloads
            // Iterate over RegionsById, check if Unloading, check pins, and dispose if safe.
        }

        private void ProcessLoadRequest(ref WorldGraph worldGraph, int3 regionKey)
        {
            if (!worldGraph.RegionIdByKey.TryGetValue(regionKey, out int regionId))
            {
                // Allocate new ID
                if (!worldGraph.FreeRegionIds.TryDequeue(out regionId))
                {
                    regionId = worldGraph.RegionsById.Length;
                    worldGraph.RegionsById.Add(new RegionEntry { State = RegionState.NeverExisted });
                    worldGraph.RegionPinWords.Add(0);
                }
                worldGraph.RegionIdByKey.Add(regionKey, regionId);
            }

            var entry = worldGraph.RegionsById[regionId];
            if (entry.State == RegionState.NotLoaded || entry.State == RegionState.NeverExisted)
            {
                entry.State = RegionState.RequestedLoad;
                worldGraph.RegionsById[regionId] = entry;
                // Trigger load logic here...
            }
        }

         private void ProcessUnloadRequest(ref WorldGraph worldGraph, int3 regionKey)
        {
             if (worldGraph.RegionIdByKey.TryGetValue(regionKey, out int regionId))
             {
                 var entry = worldGraph.RegionsById[regionId];
                 if(entry.State == RegionState.Loaded)
                 {
                     entry.State = RegionState.RequestedUnload;
                     worldGraph.RegionsById[regionId] = entry;
                 }
             }
        }
    }
}
