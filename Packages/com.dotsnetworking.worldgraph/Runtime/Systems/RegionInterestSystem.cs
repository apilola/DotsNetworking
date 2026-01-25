using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DotsNetworking.WorldGraph
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(WorldGraphSystem))] // Decide load/unload before graph processes them
    public partial struct RegionInterestSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldGraphRequests>();
            state.RequireForUpdate<WorldGraphFeedback>();
            
            if (!state.EntityManager.HasComponent<MovementRegionNotifications>(state.SystemHandle))
            {
                state.EntityManager.AddComponentData(state.SystemHandle, new MovementRegionNotifications
                {
                    Q = new NativeQueue<EntityRegionChange>(Allocator.Persistent)
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.EntityManager.HasComponent<MovementRegionNotifications>(state.SystemHandle))
            {
                var notifs = state.EntityManager.GetComponentData<MovementRegionNotifications>(state.SystemHandle);
                if (notifs.Q.IsCreated) notifs.Q.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            var requests = SystemAPI.GetSingleton<WorldGraphRequests>();
            var feedback = SystemAPI.GetSingleton<WorldGraphFeedback>();
            var notifications = SystemAPI.GetSingleton<MovementRegionNotifications>();

            // 1. Process Movement Notifications
            // In a real system, you would update a local map of "KeepAlive" regions based on entity positions.
            while (notifications.Q.TryDequeue(out EntityRegionChange change))
            {
                // Note: The logic here depends on policy (e.g., Load new region, Unload old after delay).
                // Simplest policy: Request Load New, Request Unload Old immediately.
                
                // Example Policy:
                // If NewRegionKey != OldRegionKey:
                //   RequestLoad(NewRegionKey)
                //   RequestUnload(OldRegionKey)
                
                // Note: RegionKey int3(0,0,0) might be valid, need a sentinel for "invalid/null".
                // Assuming standard keys.
             
                requests.RequestLoadQ.Enqueue(change.NewRegionKey);
                requests.RequestUnloadQ.Enqueue(change.OldRegionKey);
            }

            // 2. Process Feedback (Deferrals)
            while (feedback.UnloadDeferredQ.TryDequeue(out int3 regionKey))
            {
                 // Optional: Read reason
                 UnloadDeferReason reason = UnloadDeferReason.Locked;
                 if(feedback.UnloadDeferredReasonQ.TryDequeue(out var r)) reason = r;

                 // Logic: Wait and retry later? 
                 // For now, just re-enqueue unload to try again next frame (warning: strict loop if always pinned)
                 requests.RequestUnloadQ.Enqueue(regionKey);
            }
        }
    }
}
