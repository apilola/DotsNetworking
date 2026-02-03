using BovineLabs.Core.SingletonCollection;
using DotsNetworking.SceneGraph.Components;
using Unity.Collections;
using Unity.Entities;

namespace DotsNetworking.SceneGraph
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct SceneGraphLoadingSystem : ISystem
    {
        private SingletonCollectionUtil<SceneGraphLoadRequests, NativeQueue<SectionAddress>> loadUtil;
        private SingletonCollectionUtil<SceneGraphUnloadRequests, NativeQueue<SectionAddress>> unloadUtil;

        public void OnCreate(ref SystemState state)
        {
            loadUtil = new SingletonCollectionUtil<SceneGraphLoadRequests, NativeQueue<SectionAddress>>(ref state);
            unloadUtil = new SingletonCollectionUtil<SceneGraphUnloadRequests, NativeQueue<SectionAddress>>(ref state);
        }

        public void OnDestroy(ref SystemState state)
        {
            loadUtil.Dispose();
            unloadUtil.Dispose();
        }

        public unsafe void OnUpdate(ref SystemState state)
        {
            var loadQueues = loadUtil.Containers;
            for (int i = 0; i < loadQueues.Length; i++)
            {
                var queue = loadQueues.Ptr[i];
                while (queue.TryDequeue(out var address))
                {
                    // TODO: issue actual load request
                    _ = address;
                }
            }

            var unloadQueues = unloadUtil.Containers;
            for (int i = 0; i < unloadQueues.Length; i++)
            {
                var queue = unloadQueues.Ptr[i];
                while (queue.TryDequeue(out var address))
                {
                    // TODO: issue actual unload request
                    _ = address;
                }
            }

            loadUtil.ClearRewind(state.Dependency);
            unloadUtil.ClearRewind(state.Dependency);
        }
    }
}
