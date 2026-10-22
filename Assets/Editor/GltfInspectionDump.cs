using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GltfInspectionDump
{
    static GltfInspectionDump()
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
        foreach (var path in paths)
        {
            sb.AppendLine("==== " + path + " ====");
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets == null || assets.Length == 0)
            {
                sb.AppendLine("  (no sub-assets found - path may be wrong or not imported)");
                continue;
            }
            foreach (var obj in assets)
            {
                if (obj == null) continue;
                string guid;
                long localId;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out guid, out localId);
                string parentInfo = "";
                if (obj is Transform t && t.parent != null)
                    parentInfo = " parent=" + t.parent.name;
                if (obj is GameObject go && go.transform.parent != null)
                    parentInfo = " parent=" + go.transform.parent.name;
                sb.AppendLine(string.Format("  type={0,-14} name={1,-45} fileID={2,-22} guid={3}{4}",
                    obj.GetType().Name, obj.name, localId, guid, parentInfo));

                if (obj is GameObject goDetail)
                {
                    var tr = goDetail.transform;
                    sb.AppendLine(string.Format("      localPos={0} localRot(euler)={1} localScale={2}",
                        tr.localPosition, tr.localRotation.eulerAngles, tr.localScale));
                    foreach (Component c in goDetail.GetComponents<Component>())
                    {
                        if (c == null) { sb.AppendLine("      [MISSING COMPONENT]"); continue; }
                        sb.AppendLine("      component: " + c.GetType().Name);
                    }
                }
            }
        }

        string outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GltfInspection.log");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log("[GltfInspectionDump] wrote " + outPath);
    }
}
