using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// One-shot integration: run manually via the menu item below.
// Adds the real glTF models as new child GameObjects. Does not touch
// XR Origin, Main Camera, TrackedPoseDriver, XRInputBootstrap, existing
// Input Actions, SprayOrigin, GrabPoint, or any existing script/component.
public static class GltfVisualIntegration
{
    const string HandAssetPath = "Assets/Art/External/Hands/hand_topology_study.glb";
    const string CanAssetPath = "Assets/Art/External/SprayCan/spray_can_graffiti_low_-_poly_-_by_smakologg.glb";

    // Estimated scale factors (raw model units -> meters), derived from comparing
    // raw bounding-box proportions to plausible real-world hand/can dimensions.
    // These are estimates, not measured facts - verify visually and tweak the
    // Visual/HandVisual localScale in the Inspector if they look wrong.
    const float CanUniformWorldScale = 0.032f;
    const float HandUniformWorldScale = 0.008f;

    // Measured from the source mesh (radius-taper analysis): raw local Y of the
    // nozzle tip after the model's own baked root correction, before any extra scale.
    const float RawNozzleLocalY = 6.26f;

    [MenuItem("Freehand/Integrate GLTF Visuals (Run Once)")]
    public static void Integrate()
    {
        var log = new StringBuilder();
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            Debug.LogError("[GltfVisualIntegration] No active scene.");
            return;
        }

        var canAsset = AssetDatabase.LoadAssetAtPath<GameObject>(CanAssetPath);
        var handAsset = AssetDatabase.LoadAssetAtPath<GameObject>(HandAssetPath);
        if (canAsset == null) { Debug.LogError("[GltfVisualIntegration] Could not load can asset at " + CanAssetPath); return; }
        if (handAsset == null) { Debug.LogError("[GltfVisualIntegration] Could not load hand asset at " + HandAssetPath); return; }

        var sprayCan = GameObject.Find("SprayCan");
        var leftController = GameObject.Find("Left Controller");
        var rightController = GameObject.Find("Right Controller");
        if (sprayCan == null) { Debug.LogError("[GltfVisualIntegration] 'SprayCan' not found in scene."); return; }
        if (leftController == null) { Debug.LogError("[GltfVisualIntegration] 'Left Controller' not found in scene."); return; }
        if (rightController == null) { Debug.LogError("[GltfVisualIntegration] 'Right Controller' not found in scene."); return; }

        IntegrateCan(sprayCan, canAsset, log);
        IntegrateHand(leftController, handAsset, "HandVisual", log);
        IntegrateHand(rightController, handAsset, "HandVisual", log);

        EditorSceneManager.MarkSceneDirty(scene);

        string outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GltfIntegration.log");
        File.WriteAllText(outPath, log.ToString());

        Debug.Log("[GltfVisualIntegration] Done. Review in Scene view, then save the scene (Ctrl+S) when satisfied. Log: " + outPath);
    }

    static void IntegrateCan(GameObject sprayCan, GameObject canAsset, StringBuilder log)
    {
        if (sprayCan.transform.Find("Visual") != null)
        {
            log.AppendLine("SprayCan/Visual already exists - skipped.");
            Debug.LogWarning("[GltfVisualIntegration] 'Visual' already exists under SprayCan - skipping.");
            return;
        }

        var visual = new GameObject("Visual");
        visual.transform.SetParent(sprayCan.transform, false);

        Vector3 parentScale = sprayCan.transform.localScale;
        visual.transform.localScale = new Vector3(
            CanUniformWorldScale / parentScale.x,
            CanUniformWorldScale / parentScale.y,
            CanUniformWorldScale / parentScale.z);

        var sprayOrigin = sprayCan.transform.Find("Cap/SprayOrigin");
        if (sprayOrigin == null)
        {
            Debug.LogError("[GltfVisualIntegration] Could not find Cap/SprayOrigin under SprayCan - aborting can visual placement.");
            log.AppendLine("ERROR: Cap/SprayOrigin not found - can visual not placed.");
            Object.DestroyImmediate(visual);
            return;
        }

        float nozzleWorldOffset = RawNozzleLocalY * CanUniformWorldScale;
        float sprayOriginWorldY = sprayOrigin.position.y;
        float visualWorldY = sprayOriginWorldY - nozzleWorldOffset;
        float visualLocalY = (visualWorldY - sprayCan.transform.position.y) / parentScale.y;
        visual.transform.localPosition = new Vector3(0f, visualLocalY, 0f);
        visual.transform.localRotation = Quaternion.identity;

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(canAsset, visual.transform);

        log.AppendLine("SprayCan/Visual created.");
        log.AppendLine("  localPosition=" + visual.transform.localPosition);
        log.AppendLine("  localScale=" + visual.transform.localScale);
        log.AppendLine("  worldPosition=" + visual.transform.position);
        log.AppendLine("  instance root localRotation(euler)=" + instance.transform.localRotation.eulerAngles);
        log.AppendLine("  SprayOrigin.position (unchanged, reference)=" + sprayOrigin.position);
        log.AppendLine("  modeled nozzle world Y (should ~= SprayOrigin.position.y)=" + (visual.transform.position.y + nozzleWorldOffset * (visual.transform.lossyScale.y / (CanUniformWorldScale))));
    }

    static void IntegrateHand(GameObject controller, GameObject handAsset, string childName, StringBuilder log)
    {
        if (controller.transform.Find(childName) != null)
        {
            log.AppendLine(controller.name + "/" + childName + " already exists - skipped.");
            Debug.LogWarning("[GltfVisualIntegration] '" + childName + "' already exists under " + controller.name + " - skipping.");
            return;
        }

        var visual = new GameObject(childName);
        visual.transform.SetParent(controller.transform, false);

        Vector3 parentScale = controller.transform.localScale; // expected uniform (0.04,0.04,0.04)
        float localScale = HandUniformWorldScale / parentScale.x;
        visual.transform.localScale = Vector3.one * localScale;
        visual.transform.localPosition = Vector3.zero;
        // Best-effort orientation: source model's own baked root correction already
        // aligns its wrist->fingertip axis to local +Y. This adds a further rotation
        // so that axis points along the controller's forward (+Z) instead.
        // NOT visually verified - check in the Editor/Play mode and adjust if wrong.
        visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(handAsset, visual.transform);

        log.AppendLine(controller.name + "/" + childName + " created.");
        log.AppendLine("  localScale=" + visual.transform.localScale);
        log.AppendLine("  localRotation(euler)=" + visual.transform.localRotation.eulerAngles);
        log.AppendLine("  instance root localRotation(euler)=" + instance.transform.localRotation.eulerAngles);
    }
}
