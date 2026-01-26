using UnityEngine;

namespace DotsNetworking.SceneGraph.Editor
{
    /// <summary>
    /// Shared editor-only data containers used by both the tool overlay and baker.
    /// </summary>
    internal sealed class EditorChunkData
    {
        public float[] Heights = new float[SceneGraphConstants.NodesPerChunk]; // NaN = invalid
        public MovementFlags[] Flags = new MovementFlags[SceneGraphConstants.NodesPerChunk];
        public bool IsConnectivityCalculated = false;

        public EditorChunkData()
        {
            for (int i = 0; i < Heights.Length; i++)
            {
                Heights[i] = float.NaN;
                Flags[i] = MovementFlags.None;
            }
        }
    }

    internal struct EditorChunkHeights
    {
        public float[] Heights; // Indexed by Morton Node Index (0-255)
    }
}


