using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.UniversalDelegates;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace DotsNetworking.SceneGraph.Editor
{
    [ScriptedImporter(1, "navblob", AllowCaching = true)]
    public class NavigationBlobImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            Debug.Log("## Importing .navblob asset: " + ctx.assetPath);
            this.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            var bytes = File.ReadAllBytes(ctx.assetPath);
            
            // Note: This relies on internal/advanced API usage as requested.
            // If this constructor is not available in your specific Unity version, 
            // you may need to use reflection or a custom wrapper SO.
            
            var textAsset = new TextAsset(bytes); 
            textAsset.name = this.name;

            ctx.AddObjectToAsset("binary", textAsset);
            ctx.SetMainObject(textAsset);

            NavigationAssetProvider.ForceReloadOfBlobAsset(ctx.assetPath.Split('.')[0]);
            Debug.Log("UserData: " + userData);
            if(!DateTime.TryParse(userData, out DateTime result))
            {
                result = DateTime.UtcNow;
            }
            File.SetLastAccessTimeUtc(ctx.assetPath, new DateTime((long) this.assetTimeStamp));
            Debug.Log("## Imported .navblob asset: " + ctx.assetPath);

            //UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(
            //   new UnityEngine.Object[] { textAsset }, 
            //   ctx.assetPath, 
            //   true);

            //UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(
            //                   ctx.assetPath);
                                              
        }
    }

    /*
    public class BlobAssetPostProcessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var assetPath in importedAssets)
            {
                if (assetPath.EndsWith(".navblob"))
                {
                    Debug.Log("BlobAssetPostProcessor detected imported .navblob asset: " + assetPath);
                    NavigationAssetProvider.ForceReloadOfBlobAsset(assetPath.Split('.')[0]);
                }
            }

        }
    }
    */
    [CustomEditor(typeof(NavigationBlobImporter))]
    public class NavigationBlobImporterEditor : ScriptedImporterEditor
    {
        PreviewRenderUtility preview;

        NavigationBlobImporter importer;
        TextAsset asset;

        BlobAssetReference<Section> blob;

        protected override void Awake()
        {

        }

        static MethodInfo GetPtr = typeof(TextAsset).GetMethod("GetDataPtr", BindingFlags.NonPublic | BindingFlags.Instance);
        static MethodInfo GetDataSize = typeof(TextAsset).GetMethod("GetDataSize", BindingFlags.NonPublic | BindingFlags.Instance);
        static MethodInfo TryReadInplace = typeof(BlobAssetReference<Section>).GetMethod("TryReadInplace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        public override void OnEnable()
        {
            base.OnEnable();
            preview = new PreviewRenderUtility();
            importer = (NavigationBlobImporter) target;
            asset = AssetDatabase.LoadAssetAtPath<TextAsset>(importer.assetPath);
            
            var ptr = (IntPtr)GetPtr.Invoke(asset, null);
            MethodInfo TryReadInplace = typeof(BlobAssetReference<Section>).GetMethod("TryReadInplace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            unsafe
            {
                // 1. Properly box the pointer for the byte* parameter
                // You MUST use Pointer.Box to pass unsafe pointers via reflection
                var data = asset.GetData<byte>();
                // 2. Setup the parameters array with the boxed pointer
                // Use the parameters array to retrieve 'out' values later
                object[] parameters = new object[]
                {
                    (IntPtr) data.GetUnsafeReadOnlyPtr(),                             // data (byte*)
                    data.Length,                                 // size (long)
                    0,                                    // version (int) - verify this matches asset version
                    default(BlobAssetReference<Section>),  // result (out BlobAssetReference<T>)
                    0                                     // numBytesRead (out int)
                };

                // 3. Invoke the method
                bool success = (bool)TryReadInplace.Invoke(null, parameters);

                if (!success)
                {
                    // Debugging hint: TryReadInplace fails if:
                    // - storedVersion (first 4 bytes of data) != 1
                    // - size < sizeof(int) + sizeof(BlobAssetHeader)
                    // - size < total data length specified in header
                    int actualVersion = *(int*)ptr;
                    Debug.LogError($"Failed to load Scene Graph Section Blob. " +
                                   $"Passed Version: 1, Actual Version in Header: {actualVersion}");
                    return;
                }
                // 4. Retrieve the out parameter from the array
                blob = (BlobAssetReference<Section>)parameters[3];
            }
        }

        public override void OnDisable()
        {
            base.OnDisable();
            preview.Cleanup();
        }

        public override void OnInspectorGUI()
        {
            // No custom inspector needed for this importer
            //EditorGUILayout.LabelField($"Size (bytes): {asset.dataSize}");

            if(blob.IsCreated)
            {
                //EditorGUILayout.LabelField($"Sections Count: {blob.Value.Chunks.Length}");
            }
            else
            {
                EditorGUILayout.LabelField("Failed to load blob data.");
            }


            EditorGUILayout.HelpBox("This importer handles .navblob files and does not have configurable settings.", MessageType.Info);
            ApplyRevertGUI();
        }

        /*
        public override bool HasPreviewGUI()
        {
            return false;
        }
        Rect m_handlesRect;
        Rect m_screenRect;
        public override void OnInteractivePreviewGUI(Rect r, GUIStyle background)
        {
            return;
            preview.BeginPreview(r, background);
            preview.camera.Render();
            preview.camera.transform.position = new Vector3(0, 0, -5);

            using (new Handles.DrawingScope(Matrix4x4.identity))
            {
                Handles.SetCamera(preview.camera);
                //Your custom handles function.
                OnDrawHandles();
            }

            preview.EndAndDrawPreview(r);
        }

        private void OnDrawHandles()
        {
            Handles.color = Color.white;
            Handles.DrawWireCube(Vector3.zero, Vector3.one * 5f);
        }*/

    }
}



