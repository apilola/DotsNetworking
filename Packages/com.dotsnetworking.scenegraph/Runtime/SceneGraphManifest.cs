using System;
using System.Collections.Generic;
using BovineLabs.Core.Settings;
using Unity.Entities.Content;
using UnityEngine;
using EntitiesHash128 = Unity.Entities.Hash128;

namespace DotsNetworking.SceneGraph
{
    [Serializable]
    public struct SectionDefinition
    {
        public SectionAddress Address;
        public string ResourceKey;
        public WeakObjectReference<TextAsset> SectionBlob;
    }

    [Serializable]
    public struct SubsceneDefinition
    {
        public EntitiesHash128 SceneGuid;
        public string ScenePath;
        public List<SectionDefinition> Sections;

        public SubsceneDefinition(EntitiesHash128 sceneGuid, string scenePath)
        {
            SceneGuid = sceneGuid;
            ScenePath = scenePath;
            Sections = new List<SectionDefinition>();
        }
    }

    [SettingsGroup("SceneGraph")]
    public class SceneGraphManifest : SettingsSingleton<SceneGraphManifest>
    {
        [SerializeField] private List<SubsceneDefinition> m_Subscenes = new List<SubsceneDefinition>();

        public IReadOnlyList<SubsceneDefinition> Subscenes => m_Subscenes;

        /// <summary>
        /// Gets all section entries for a specific subscene.
        /// </summary>
        public List<SectionDefinition> GetSectionsForSubscene(EntitiesHash128 sceneGuid)
        {
            for (int i = 0; i < m_Subscenes.Count; i++)
            {
                if (m_Subscenes[i].SceneGuid.Equals(sceneGuid))
                {
                    return m_Subscenes[i].Sections ?? new List<SectionDefinition>();
                }
            }
            return new List<SectionDefinition>();
        }

        /// <summary>
        /// Gets all sections across all subscenes.
        /// </summary>
        public IEnumerable<SectionDefinition> GetAllSections()
        {
            foreach (var subscene in m_Subscenes)
            {
                if (subscene.Sections != null)
                {
                    foreach (var section in subscene.Sections)
                    {
                        yield return section;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the subscene definition for a specific scene GUID.
        /// </summary>
        public bool TryGetSubscene(EntitiesHash128 sceneGuid, out SubsceneDefinition definition)
        {
            for (int i = 0; i < m_Subscenes.Count; i++)
            {
                if (m_Subscenes[i].SceneGuid.Equals(sceneGuid))
                {
                    definition = m_Subscenes[i];
                    return true;
                }
            }
            definition = default;
            return false;
        }

        /// <summary>
        /// Updates or adds a subscene definition.
        /// </summary>
        public void SetSubscene(SubsceneDefinition definition)
        {
            for (int i = 0; i < m_Subscenes.Count; i++)
            {
                if (m_Subscenes[i].SceneGuid.Equals(definition.SceneGuid))
                {
                    m_Subscenes[i] = definition;
                    return;
                }
            }
            m_Subscenes.Add(definition);
        }

        /// <summary>
        /// Removes a subscene and all its associated sections.
        /// </summary>
        public bool RemoveSubscene(EntitiesHash128 sceneGuid)
        {
            for (int i = 0; i < m_Subscenes.Count; i++)
            {
                if (m_Subscenes[i].SceneGuid.Equals(sceneGuid))
                {
                    m_Subscenes.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Sets all sections for a specific subscene, replacing any existing ones.
        /// </summary>
        public void SetSectionsForSubscene(EntitiesHash128 sceneGuid, List<SectionDefinition> sections)
        {
            for (int i = 0; i < m_Subscenes.Count; i++)
            {
                if (m_Subscenes[i].SceneGuid.Equals(sceneGuid))
                {
                    var def = m_Subscenes[i];
                    def.Sections = sections ?? new List<SectionDefinition>();
                    m_Subscenes[i] = def;
                    return;
                }
            }
        }

        /// <summary>
        /// Sets or updates a single section within a subscene.
        /// </summary>
        public void SetSection(SectionDefinition entry)
        {
            for (int i = 0; i < m_Subscenes.Count; i++)
            {
                if (m_Subscenes[i].SceneGuid.Equals(entry.Address.SceneGuid))
                {
                    var def = m_Subscenes[i];
                    if (def.Sections == null)
                    {
                        def.Sections = new List<SectionDefinition>();
                    }

                    for (int j = 0; j < def.Sections.Count; j++)
                    {
                        if (def.Sections[j].Address.SectionId == entry.Address.SectionId)
                        {
                            def.Sections[j] = entry;
                            m_Subscenes[i] = def;
                            return;
                        }
                    }

                    def.Sections.Add(entry);
                    m_Subscenes[i] = def;
                    return;
                }
            }
        }

        /// <summary>
        /// Removes a section from a subscene.
        /// </summary>
        public bool RemoveSection(SectionAddress address)
        {
            for (int i = 0; i < m_Subscenes.Count; i++)
            {
                if (m_Subscenes[i].SceneGuid.Equals(address.SceneGuid))
                {
                    var def = m_Subscenes[i];
                    if (def.Sections == null)
                    {
                        return false;
                    }

                    for (int j = 0; j < def.Sections.Count; j++)
                    {
                        if (def.Sections[j].Address.SectionId == address.SectionId)
                        {
                            def.Sections.RemoveAt(j);
                            m_Subscenes[i] = def;
                            return true;
                        }
                    }
                    return false;
                }
            }
            return false;
        }
    }
}
