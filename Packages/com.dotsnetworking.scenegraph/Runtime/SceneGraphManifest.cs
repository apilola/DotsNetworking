using System;
using System.Collections.Generic;
using Unity.Entities.Content;
using UnityEngine;
namespace DotsNetworking.SceneGraph
{
    [Serializable]
    public struct SectionManifestEntry
    {
        public SectionAddress Address;
        public string ResourceKey;
        public WeakObjectReference<TextAsset> SectionBlob;
    }

    public sealed class SceneGraphManifest : ScriptableObject
    {
        [SerializeField] private List<SectionManifestEntry> m_Sections = new List<SectionManifestEntry>();

        public IReadOnlyList<SectionManifestEntry> Sections => m_Sections;

        public void SetSections(List<SectionManifestEntry> sections)
        {
            m_Sections.Clear();
            if (sections == null || sections.Count == 0)
            {
                return;
            }

            m_Sections.AddRange(sections);
        }

        public void SetSection(SectionManifestEntry entry)
        {
            for (int i = 0; i < m_Sections.Count; i++)
            {
                var existing = m_Sections[i];
                if (existing.Address.SceneGuid.Equals(entry.Address.SceneGuid) &&
                    existing.Address.SectionId == entry.Address.SectionId)
                {
                    m_Sections[i] = entry;
                    return;
                }
            }

            m_Sections.Add(entry);
        }

        public bool RemoveSection(SectionAddress address)
        {
            for (int i = 0; i < m_Sections.Count; i++)
            {
                var existing = m_Sections[i];
                if (existing.Address.SceneGuid.Equals(address.SceneGuid) &&
                    existing.Address.SectionId == address.SectionId)
                {
                    m_Sections.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }
    }
}
