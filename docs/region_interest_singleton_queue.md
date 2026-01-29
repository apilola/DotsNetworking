# Region Interest Singleton Queue System

This document describes how to implement the queue-based communication between the **RegionInterestSystem** and **WorldGraphSystem** using BovineLabs Singleton Collections.

---

## Overview

The Region Interest System uses a **Many-To-One** queue pattern:

* **Many producers**: Movement systems, AI systems, player systems write region change notifications
* **One consumer**: RegionInterestSystem processes notifications and emits load/unload requests
* **One consumer**: WorldGraphSystem processes load/unload requests and emits feedback

This pattern maps directly to BovineLabs' `ISingletonCollection<T>` which provides:

* Lock-free concurrent writes from multiple systems/jobs
* Burst-compatible reading
* Zero per-frame allocations via rewindable allocators
* Automatic memory management

---

## Queue Topology

```
┌─────────────────────┐     ┌─────────────────────┐     ┌─────────────────────┐
│   Movement Systems  │     │ RegionInterestSystem│     │  WorldGraphSystem   │
│   (Many Writers)    │────▶│   (Consumer/Writer) │────▶│     (Consumer)      │
└─────────────────────┘     └─────────────────────┘     └─────────────────────┘
                                                                  │
                            ┌─────────────────────┐               │
                            │ RegionInterestSystem│◀──────────────┘
                            │   (Feedback Reader) │   (Unload Deferred)
                            └─────────────────────┘
```

---

## Singleton Definitions

### 1. Movement → Interest Notifications

Movement systems notify the interest system when entities change regions.

```csharp
/// <summary>
/// Singleton collection for entity region change notifications.
/// Multiple movement/AI systems can write concurrently.
/// RegionInterestSystem is the sole consumer.
/// </summary>
public struct RegionChangeNotificationsSingleton : ISingletonCollection<NativeList<EntityRegionChange>>
{
    unsafe UnsafeList<NativeList<EntityRegionChange>>* ISingletonCollection<NativeList<EntityRegionChange>>.Collections { get; set; }
    Allocator ISingletonCollection<NativeList<EntityRegionChange>>.Allocator { get; set; }
}

public struct EntityRegionChange
{
    public Entity Entity;
    public int3 OldRegionKey;
    public int3 NewRegionKey;
}
```

### 2. Interest → WorldGraph Load Requests

RegionInterestSystem requests region loads from WorldGraphSystem.

```csharp
/// <summary>
/// Singleton collection for region load requests.
/// RegionInterestSystem is the primary writer.
/// WorldGraphSystem is the sole consumer.
/// </summary>
public struct RegionLoadRequestsSingleton : ISingletonCollection<NativeList<int3>>
{
    unsafe UnsafeList<NativeList<int3>>* ISingletonCollection<NativeList<int3>>.Collections { get; set; }
    Allocator ISingletonCollection<NativeList<int3>>.Allocator { get; set; }
}
```

### 3. Interest → WorldGraph Unload Requests

RegionInterestSystem requests region unloads from WorldGraphSystem.

```csharp
/// <summary>
/// Singleton collection for region unload requests.
/// RegionInterestSystem is the primary writer.
/// WorldGraphSystem is the sole consumer.
/// </summary>
public struct RegionUnloadRequestsSingleton : ISingletonCollection<NativeList<int3>>
{
    unsafe UnsafeList<NativeList<int3>>* ISingletonCollection<NativeList<int3>>.Collections { get; set; }
    Allocator ISingletonCollection<NativeList<int3>>.Allocator { get; set; }
}
```

### 4. WorldGraph → Interest Feedback

WorldGraphSystem provides feedback when unloads are deferred.

```csharp
/// <summary>
/// Singleton collection for unload deferral feedback.
/// WorldGraphSystem is the writer.
/// RegionInterestSystem is the consumer.
/// </summary>
public struct UnloadDeferredFeedbackSingleton : ISingletonCollection<NativeList<UnloadDeferredEvent>>
{
    unsafe UnsafeList<NativeList<UnloadDeferredEvent>>* ISingletonCollection<NativeList<UnloadDeferredEvent>>.Collections { get; set; }
    Allocator ISingletonCollection<NativeList<UnloadDeferredEvent>>.Allocator { get; set; }
}

public struct UnloadDeferredEvent
{
    public int3 RegionKey;
    public UnloadDeferReason Reason;
}

public enum UnloadDeferReason : byte
{
    StillPinned = 1,
    StillLoading = 2,
    Locked = 3,
}
```

---

## Processing Systems

### RegionInterestSystem (Consumer of Notifications, Producer of Requests)

```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(WorldGraphSystemGroup))]
public partial struct RegionInterestSystem : ISystem
{
    // Util for consuming region change notifications
    private SingletonCollectionUtil<RegionChangeNotificationsSingleton, NativeList<EntityRegionChange>> notificationsUtil;
    
    // Util for consuming unload deferred feedback
    private SingletonCollectionUtil<UnloadDeferredFeedbackSingleton, NativeList<UnloadDeferredEvent>> feedbackUtil;

    // Interest tracking state
    private NativeParallelHashMap<int3, RegionInterestState> interestState;

    public void OnCreate(ref SystemState state)
    {
        notificationsUtil = new SingletonCollectionUtil<RegionChangeNotificationsSingleton, NativeList<EntityRegionChange>>(ref state);
        feedbackUtil = new SingletonCollectionUtil<UnloadDeferredFeedbackSingleton, NativeList<UnloadDeferredEvent>>(ref state);
        
        interestState = new NativeParallelHashMap<int3, RegionInterestState>(64, Allocator.Persistent);
    }

    public void OnDestroy(ref SystemState state)
    {
        notificationsUtil.Dispose();
        feedbackUtil.Dispose();
        interestState.Dispose();
    }

    [BurstCompile]
    public unsafe void OnUpdate(ref SystemState state)
    {
        // 1. Process incoming region change notifications
        var notifications = notificationsUtil.Containers;
        for (int i = 0; i < notifications.Length; i++)
        {
            ProcessNotifications(notifications.Ptr[i]);
        }
        notificationsUtil.ClearRewind();

        // 2. Process unload deferred feedback
        var feedback = feedbackUtil.Containers;
        for (int i = 0; i < feedback.Length; i++)
        {
            ProcessFeedback(feedback.Ptr[i]);
        }
        feedbackUtil.ClearRewind();

        // 3. Evaluate interest and emit load/unload requests
        EmitLoadUnloadRequests(ref state);
    }

    private void ProcessNotifications(NativeList<EntityRegionChange> changes)
    {
        for (int i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            
            // Decrement interest in old region
            if (interestState.TryGetValue(change.OldRegionKey, out var oldState))
            {
                oldState.EntityCount--;
                oldState.LastExitTime = /* current time */;
                interestState[change.OldRegionKey] = oldState;
            }
            
            // Increment interest in new region (and neighbors)
            UpdateRegionInterest(change.NewRegionKey, +1);
        }
    }

    private void ProcessFeedback(NativeList<UnloadDeferredEvent> events)
    {
        for (int i = 0; i < events.Length; i++)
        {
            var evt = events[i];
            
            if (interestState.TryGetValue(evt.RegionKey, out var regionState))
            {
                // Extend unload delay due to deferral
                regionState.UnloadDelayUntil = /* current time + hysteresis */;
                interestState[evt.RegionKey] = regionState;
            }
        }
    }

    private void EmitLoadUnloadRequests(ref SystemState state)
    {
        // Get writable lists from singletons
        var loadRequests = SystemAPI.GetSingleton<RegionLoadRequestsSingleton>()
            .CreateList<RegionLoadRequestsSingleton, int3>(16);
        var unloadRequests = SystemAPI.GetSingleton<RegionUnloadRequestsSingleton>()
            .CreateList<RegionUnloadRequestsSingleton, int3>(16);

        var keys = interestState.GetKeyArray(Allocator.Temp);
        for (int i = 0; i < keys.Length; i++)
        {
            var regionKey = keys[i];
            var regionState = interestState[regionKey];

            if (ShouldLoad(regionState))
            {
                loadRequests.Add(regionKey);
                regionState.RequestedLoad = true;
                interestState[regionKey] = regionState;
            }
            else if (ShouldUnload(regionState))
            {
                unloadRequests.Add(regionKey);
                regionState.RequestedUnload = true;
                interestState[regionKey] = regionState;
            }
        }
        keys.Dispose();
    }

    private bool ShouldLoad(RegionInterestState state) => 
        state.EntityCount > 0 && !state.IsLoaded && !state.RequestedLoad;

    private bool ShouldUnload(RegionInterestState state) => 
        state.EntityCount == 0 && state.IsLoaded && !state.RequestedUnload 
        && /* current time > state.UnloadDelayUntil */;
}

public struct RegionInterestState
{
    public int EntityCount;
    public bool IsLoaded;
    public bool RequestedLoad;
    public bool RequestedUnload;
    public double LastExitTime;
    public double UnloadDelayUntil;
}
```

### WorldGraphSystem (Consumer of Requests, Producer of Feedback)

```csharp
[UpdateInGroup(typeof(WorldGraphSystemGroup))]
public partial struct WorldGraphSystem : ISystem
{
    // Util for consuming load requests
    private SingletonCollectionUtil<RegionLoadRequestsSingleton, NativeList<int3>> loadRequestsUtil;
    
    // Util for consuming unload requests
    private SingletonCollectionUtil<RegionUnloadRequestsSingleton, NativeList<int3>> unloadRequestsUtil;

    public void OnCreate(ref SystemState state)
    {
        loadRequestsUtil = new SingletonCollectionUtil<RegionLoadRequestsSingleton, NativeList<int3>>(ref state);
        unloadRequestsUtil = new SingletonCollectionUtil<RegionUnloadRequestsSingleton, NativeList<int3>>(ref state);
    }

    public void OnDestroy(ref SystemState state)
    {
        loadRequestsUtil.Dispose();
        unloadRequestsUtil.Dispose();
    }

    [BurstCompile]
    public unsafe void OnUpdate(ref SystemState state)
    {
        // 1. Process load requests
        var loadRequests = loadRequestsUtil.Containers;
        for (int i = 0; i < loadRequests.Length; i++)
        {
            ProcessLoadRequests(loadRequests.Ptr[i], ref state);
        }
        loadRequestsUtil.ClearRewind();

        // 2. Process unload requests
        var unloadRequests = unloadRequestsUtil.Containers;
        for (int i = 0; i < unloadRequests.Length; i++)
        {
            ProcessUnloadRequests(unloadRequests.Ptr[i], ref state);
        }
        unloadRequestsUtil.ClearRewind();

        // 3. Drain completed async loads (main thread)
        DrainBlobReadyQueue(ref state);
    }

    private void ProcessLoadRequests(NativeList<int3> requests, ref SystemState state)
    {
        for (int i = 0; i < requests.Length; i++)
        {
            var regionKey = requests[i];
            // Initiate async load...
            InitiateAsyncLoad(regionKey);
        }
    }

    private void ProcessUnloadRequests(NativeList<int3> requests, ref SystemState state)
    {
        // Get feedback list for deferred unloads
        var feedbackList = SystemAPI.GetSingleton<UnloadDeferredFeedbackSingleton>()
            .CreateList<UnloadDeferredFeedbackSingleton, UnloadDeferredEvent>(8);

        for (int i = 0; i < requests.Length; i++)
        {
            var regionKey = requests[i];
            var result = TryUnloadRegion(regionKey);
            
            if (!result.Success)
            {
                // Emit deferral feedback
                feedbackList.Add(new UnloadDeferredEvent
                {
                    RegionKey = regionKey,
                    Reason = result.Reason
                });
            }
        }
    }
}
```

---

## Writing from Movement/Gameplay Systems

Any system can write region change notifications concurrently:

```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct MovementSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // Get a list from the singleton (allocated from rewindable allocator)
        var notifications = SystemAPI.GetSingleton<RegionChangeNotificationsSingleton>()
            .CreateList<RegionChangeNotificationsSingleton, EntityRegionChange>(64);

        // Schedule job that writes to the list
        state.Dependency = new DetectRegionChangesJob
        {
            Notifications = notifications.AsParallelWriter()
        }.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    private partial struct DetectRegionChangesJob : IJobEntity
    {
        public NativeList<EntityRegionChange>.ParallelWriter Notifications;

        public void Execute(Entity entity, in LocalTransform transform, ref RegionMembership membership)
        {
            var newRegionKey = CalculateRegionKey(transform.Position);
            
            if (!newRegionKey.Equals(membership.CurrentRegionKey))
            {
                Notifications.AddNoResize(new EntityRegionChange
                {
                    Entity = entity,
                    OldRegionKey = membership.CurrentRegionKey,
                    NewRegionKey = newRegionKey
                });
                
                membership.CurrentRegionKey = newRegionKey;
            }
        }
    }
}
```

---

## Key Benefits

### 1. Lock-Free Concurrent Access
Multiple systems can call `CreateList<T>()` simultaneously. Each gets its own list backed by thread-local storage.

### 2. Zero Per-Frame Allocations
The rewindable allocator reuses memory each frame. `ClearRewind()` resets containers without deallocating.

### 3. Burst Compatibility
All reading and writing operations are Burst-compatible. Only `OnCreate` and `OnDestroy` require managed code.

### 4. Clean Separation of Concerns
- **Producers** only write to queues
- **Consumers** own the `SingletonCollectionUtil` and call `ClearRewind()`
- No shared mutable state beyond the queue containers

### 5. Automatic Dependency Management
Each system that creates a list automatically participates in the dependency chain. The consuming system waits for all writers before reading.

---

## System Update Order

```
SimulationSystemGroup
├── MovementSystem              (writes RegionChangeNotifications)
├── AIMovementSystem            (writes RegionChangeNotifications)
├── PlayerMovementSystem        (writes RegionChangeNotifications)
├── RegionInterestSystem        (reads Notifications, reads Feedback, writes Load/Unload Requests)
└── WorldGraphSystemGroup
    └── WorldGraphSystem        (reads Load/Unload Requests, writes Feedback)
```

The `[UpdateBefore(typeof(WorldGraphSystemGroup))]` attribute on RegionInterestSystem ensures proper ordering.

---

## Hysteresis and Thrash Prevention

The RegionInterestSystem maintains per-region state to prevent rapid load/unload cycles:

```csharp
public struct RegionInterestState
{
    public int EntityCount;           // Active entities in region
    public bool IsLoaded;             // Current load state
    public bool RequestedLoad;        // Pending load request
    public bool RequestedUnload;      // Pending unload request
    public double LastExitTime;       // When last entity left
    public double UnloadDelayUntil;   // Minimum time before unload allowed
}
```

When an entity exits a region:
1. `EntityCount` decrements
2. `LastExitTime` is recorded
3. Unload is deferred until `UnloadDelayUntil` passes (hysteresis delay)

When unload is deferred by WorldGraphSystem:
1. Feedback arrives via `UnloadDeferredFeedbackSingleton`
2. `UnloadDelayUntil` is extended
3. Request is retried next frame

---

## Client vs Server Considerations

| Aspect | Client | Server |
|--------|--------|--------|
| Notification volume | Low (local player + nearby) | High (all players + AI) |
| Hysteresis delay | Short (aggressive unload) | Long (conservative) |
| Interest radius | Visual range | Gameplay range + buffer |
| Feedback handling | Retry immediately | Batch retries |

Both client and server use the same singleton queue infrastructure, but configure different policies in `RegionInterestSystem`.
