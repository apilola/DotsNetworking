using Unity.Collections;
using Unity.Entities;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph.Components
{
    [System.Serializable]
    public struct SectionAddress : IComponentData, System.IEquatable<SectionAddress>
    {
        [UnityEngine.SerializeField, ReadOnly] private EntitiesHash128 m_SceneGuid;
        [UnityEngine.SerializeField, ReadOnly] private uint m_SectionId;

        public EntitiesHash128 SceneGuid => m_SceneGuid;
        public uint SectionId => m_SectionId;

        public SectionAddress(EntitiesHash128 sceneGuid, uint sectionId)
        {
            m_SceneGuid = sceneGuid;
            m_SectionId = sectionId;
        }

        public bool Equals(SectionAddress other)
        {
            return m_SceneGuid.Equals(other.m_SceneGuid) && m_SectionId == other.m_SectionId;
        }

        public override bool Equals(object obj) => obj is SectionAddress other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = m_SceneGuid.GetHashCode();
                hash = hash * 397 ^ (int)m_SectionId;
                return hash;
            }
        }

        public static bool operator ==(SectionAddress left, SectionAddress right) => left.Equals(right);
        public static bool operator !=(SectionAddress left, SectionAddress right) => !left.Equals(right);

        public override string ToString() => $"SectionAddress(Scene={m_SceneGuid};R={m_SectionId})";
    }
}



