using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

namespace DotsNetworking.WorldGraph
{
    public struct MovementRegionNotifications : IComponentData
    {
        public NativeQueue<EntityRegionChange> Q;
    }

    public struct EntityRegionChange
    {
        public Entity Entity;
        public int3 OldRegionKey;
        public int3 NewRegionKey;
    }
}
