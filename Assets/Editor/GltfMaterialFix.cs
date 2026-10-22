using System.Collections.Generic;
using System.IO;
using System.Text;
using Freehand.Spray;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// One-shot fix, run manually via the menu item below.
// Problem: glTFast generated its materials using the Built-in RP Shader Graph
// variant ("Shader Graphs/glTF-pbrMetallicRoughness"), which renders magenta
// in this URP project. This does not reimport or touch the .glb assets -
// it creates new Universal Render Pipeline/Lit materials (colors taken
// directly from the source glTF baseColorFactor values, not guessed) and
// reassigns them only on the MeshRenderers under the SprayCan/Visual and
// HandVisual instances added earlier. Also disables the old procedural
// can renderers (body/cap) so they stop showing behind the real model,
// without removing VRGrabInteractable/SprayEmitter/SprayOrigin/GrabPoint
// or SprayCanVisual's code.
public static class GltfMaterialFix
{
    // name -> (baseColorFactor RGBA, metallic, roughness) - read directly
    // from the source .glb JSON (pbrMetallicRoughness), not invented.
    static readonly Dictionary<string, (Color color, float metallic, float roughness)> SourceMaterials =
        new Dictionary<string, (Color, float, float)>
        {
            { "Blue",     (new Color(0.16111611f, 0.29824263f, 1f, 1f), 0f, 0.6f) },
            { "Green",    (new Color(0.05706289f, 0.74943454f, 0.10503580f, 1f), 0f, 0.6f) },
            { "Grey",     (new Color(1f, 1f, 1f, 1f), 0f, 0.6f) },
            { "Yellow",   (new Color(0.90959594f, 1f, 0.09503719f, 1f), 0f, 0.6f) },
            { "NewMat30", (new Color(1f, 1f, 1f, 1f), 0f, 0f) },
            { "NewMat31", (new Color(0.614047f, 0.614047f, 0.614047f, 1f), 0.24228645f, 0.41637917f) },
        };

    const string OutputFolder = "Assets/Materials/Imported";

    [MenuItem("Freehand/Fix GLTF Materials + Disable Old Can Visual (Run Once)")]
    public static void Fix()
    {
        var log = new StringBuilder();

        if (!AssetDatabase.IsValidFolder(OutputFolder))
            AssetDatabase.CreateFolder("Assets/Materials", "Imported");

        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            Debug.LogError("[GltfMaterialFix] Could not find shader 'Universal Render Pipeline/Lit'.");
            return;
        }

        var createdMaterials = new Dictionary<string, Material>();
        foreach (var kvp in SourceMaterials)
        {
            string matPath = OutputFolder + "/" + kvp.Key + "_URP.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetColor("_BaseColor", kvp.Value.color);
            mat.SetFloat("_Metallic", kvp.Value.metallic);
            mat.SetFloat("_Smoothness", 1f - kvp.Value.roughness);
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off); // source glTF materials are doubleSided
            EditorUtility.SetDirty(mat);
            createdMaterials[kvp.Key] = mat;
            log.AppendLine("Material ready: " + matPath + " color=" + kvp.Value.color);
        }

        var sprayCan = GameObject.Find("SprayCan");
        var leftController = GameObject.Find("Left Controller");
        var rightController = GameObject.Find("Right Controller");

        int reassigned = 0;
        if (sprayCan != null)
        {
            var visual = sprayCan.transform.Find("Visual");
            if (visual != null) reassigned += ReassignMaterials(visual, createdMaterials, log);
        }
        if (leftController != null)
        {
            var hv = leftController.transform.Find("HandVisual");
            if (hv != null) reassigned += ReassignMaterials(hv, createdMaterials, log);
        }
        if (rightController != null)
        {
            var hv = rightController.transform.Find("HandVisual");
            if (hv != null) reassigned += ReassignMaterials(hv, createdMaterials, log);
        }

        // Disable the old procedural can visual (body + cap renderers) so it
        // stops rendering behind the real model. Component/logic left intact.
        if (sprayCan != null)
        {
            var bodyRenderer = sprayCan.GetComponent<MeshRenderer>();
            if (bodyRenderer != null) { bodyRenderer.enabled = false; log.AppendLine("Disabled SprayCan body MeshRenderer."); }

            var cap = sprayCan.transform.Find("Cap");
            if (cap != null)
            {
                var capRenderer = cap.GetComponent<MeshRenderer>();
                if (capRenderer != null) { capRenderer.enabled = false; log.AppendLine("Disabled Cap MeshRenderer."); }
            }

            var visualScript = sprayCan.GetComponent<SprayCanVisual>();
            if (visualScript != null)
            {
                var so = new SerializedObject(visualScript);
                var sprayOriginProp = so.FindProperty("sprayOrigin");
                if (sprayOriginProp != null)
                {
                    sprayOriginProp.objectReferenceValue = null;
                    so.ApplyModifiedProperties();
                    log.AppendLine("Cleared SprayCanVisual.sprayOrigin so it no longer spawns the procedural NozzleTip sphere at Play.");
                }
            }
        }

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);

        string outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GltfMaterialFix.log");
        File.WriteAllText(outPath, log.ToString());
        Debug.Log("[GltfMaterialFix] Done. Reassigned materials on " + reassigned + " renderer(s). Review in Scene view, then save (Ctrl+S). Log: " + outPath);
    }

    static int ReassignMaterials(Transform root, Dictionary<string, Material> createdMaterials, StringBuilder log)
    {
        int count = 0;
        var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var renderer in renderers)
        {
            var mats = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                string sourceName = mats[i] != null ? mats[i].name : null;
                if (sourceName != null && createdMaterials.TryGetValue(sourceName, out var replacement))
                {
                    mats[i] = replacement;
                    changed = true;
                }
            }
            if (changed)
            {
                renderer.sharedMaterials = mats;
                EditorUtility.SetDirty(renderer);
                count++;
                log.AppendLine("  Reassigned materials on " + GetPath(renderer.transform));
            }
            else
            {
                log.AppendLine("  NOT matched (left as-is) on " + GetPath(renderer.transform) + " - current materials: " + string.Join(",", System.Array.ConvertAll(mats, m => m != null ? m.name : "null")));
            }
        }
        return count;
    }

    static string GetPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }
}
