using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GltfMaterialDiagnostics
{
    static GltfMaterialDiagnostics()
    {
        EditorApplication.delayCall += Run;
    }

    static void Run()
    {
        string[] paths =
        {
            "Assets/Art/External/Hands/hand_topology_study.glb",
            "Assets/Art/External/SprayCan/spray_can_graffiti_low_-_poly_-_by_smakologg.glb"
        };

        var sb = new StringBuilder();
        sb.AppendLine("Active Render Pipeline Asset: " + (UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline.name : "NULL (Built-in RP)"));
        sb.AppendLine();

        foreach (var path in paths)
        {
            sb.AppendLine("==== " + path + " ====");
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            foreach (var obj in assets)
            {
                if (obj is Material mat)
                {
                    string shaderName = mat.shader != null ? mat.shader.name : "NULL SHADER";
                    bool isErrorShader = mat.shader != null && mat.shader.name == "Hidden/InternalErrorShader";
                    bool supported = mat.shader != null && mat.shader.isSupported;
                    sb.AppendLine(string.Format("  Material '{0}': shader='{1}' isErrorShader={2} shaderSupported={3} renderQueue={4}",
                        mat.name, shaderName, isErrorShader, supported, mat.renderQueue));
                    sb.AppendLine("    color property _BaseColor present=" + mat.HasProperty("_BaseColor") + " _Color present=" + mat.HasProperty("_Color"));
                }
            }
        }

        // Also check the existing hand-authored materials for comparison
        string[] existingMats = { "Assets/Materials/SprayCanBody.mat", "Assets/Materials/SprayCanCap.mat" };
        sb.AppendLine();
        sb.AppendLine("==== existing scene materials (for comparison) ====");
        foreach (var mp in existingMats)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mat == null) { sb.AppendLine("  " + mp + " -> NOT FOUND at that path"); continue; }
            sb.AppendLine(string.Format("  {0}: shader='{1}'", mp, mat.shader != null ? mat.shader.name : "NULL"));
        }

        string outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GltfMaterialDiagnostics.log");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("[GltfMaterialDiagnostics] wrote " + outPath);
    }
}
