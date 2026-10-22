using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Builds a single clean hand mesh from the imported hand_topology_study.glb.
//
// Why this exists: that file contains the SAME hand twice, side by side -
// a base cage and a once-subdivided copy (verified: the two groups are
// separated by a band of zero vertices, no triangle crosses it, and the
// triangle counts are in an exact 4:1 ratio in all four meshes). Both copies
// live inside all four of the GLB's meshes, so the duplicate cannot be hidden
// by disabling a GameObject - the geometry itself has to be filtered.
//
// This does not modify or replace the .glb. It reads the meshes Unity already
// imported and writes a new Mesh asset next to them.
public static class HandMeshCleanup
{
    const string HandAssetPath = "Assets/Art/External/Hands/hand_topology_study.glb";
    const string OutputFolder = "Assets/Art/Generated";
    const string OutputMesh = OutputFolder + "/Hand_Single.asset";
    const string MirroredMesh = OutputFolder + "/Hand_Single_Mirrored.asset";

    static void WriteMesh(string path, string name, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris)
    {
        var target = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        var isNew = target == null;
        if (isNew)
            target = new Mesh();

        target.Clear();
        target.name = name;
        if (verts.Count > 65000)
            target.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        target.SetVertices(verts);
        target.SetNormals(normals);
        target.SetUVs(0, uvs);
        target.SetTriangles(tris, 0);
        target.RecalculateBounds();
        target.RecalculateTangents();

        if (isNew)
            AssetDatabase.CreateAsset(target, path);
        else
            EditorUtility.SetDirty(target);
    }

    [MenuItem("Freehand/V0.4/1 - Build Single Hand Mesh")]
    public static void Build()
    {
        var log = new StringBuilder();

        var subAssets = AssetDatabase.LoadAllAssetsAtPath(HandAssetPath);
        var sourceMeshes = new List<Mesh>();
        foreach (var asset in subAssets)
        {
            if (asset is Mesh mesh)
                sourceMeshes.Add(mesh);
        }

        if (sourceMeshes.Count == 0)
        {
            Debug.LogError($"[HandMeshCleanup] No meshes found at {HandAssetPath}");
            return;
        }

        log.AppendLine($"Meshes de origem: {sourceMeshes.Count}");

        // Find the split automatically rather than hardcoding an axis/value:
        // glTFast applies its own coordinate conversion on import, so the axis
        // that separated the two copies in the raw file is not necessarily the
        // axis that separates them in Unity space.
        var allPoints = new List<Vector3>();
        foreach (var mesh in sourceMeshes)
            allPoints.AddRange(mesh.vertices);

        var splitAxis = -1;
        var splitValue = 0f;
        var bestGap = 0f;

        for (var axis = 0; axis < 3; axis++)
        {
            var values = new List<float>(allPoints.Count);
            foreach (var p in allPoints)
                values.Add(p[axis]);
            values.Sort();

            for (var i = 1; i < values.Count; i++)
            {
                var gap = values[i] - values[i - 1];
                if (gap > bestGap)
                {
                    bestGap = gap;
                    splitAxis = axis;
                    splitValue = (values[i] + values[i - 1]) * 0.5f;
                }
            }
        }

        var axisName = splitAxis == 0 ? "X" : splitAxis == 1 ? "Y" : "Z";
        log.AppendLine($"Maior vazio encontrado: eixo={axisName} valor={splitValue:F3} tamanho={bestGap:F3}");

        if (bestGap < 0.5f)
        {
            Debug.LogError($"[HandMeshCleanup] No clear gap found (largest={bestGap:F3}); aborting rather than cutting the hand in half.");
            return;
        }

        // Keep the side with fewer triangles: that is the base cage, which is
        // the same hand at a quarter of the cost - the right trade for two
        // hands that are always on screen on a standalone headset.
        var belowTris = 0;
        var aboveTris = 0;
        foreach (var mesh in sourceMeshes)
        {
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            for (var t = 0; t < tris.Length; t += 3)
            {
                if (verts[tris[t]][splitAxis] < splitValue)
                    belowTris++;
                else
                    aboveTris++;
            }
        }

        var keepBelow = belowTris <= aboveTris;
        var keptSide = keepBelow ? "abaixo do corte" : "acima do corte";
        log.AppendLine($"Triangulos: abaixo={belowTris} acima={aboveTris} -> mantendo o lado {keptSide} (menos triangulos = malha base)");

        var combinedVerts = new List<Vector3>();
        var combinedNormals = new List<Vector3>();
        var combinedUvs = new List<Vector2>();
        var combinedTris = new List<int>();

        foreach (var mesh in sourceMeshes)
        {
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            var tris = mesh.triangles;
            var remap = new Dictionary<int, int>();

            for (var t = 0; t < tris.Length; t += 3)
            {
                var isBelow = verts[tris[t]][splitAxis] < splitValue;
                if (isBelow != keepBelow)
                    continue;

                for (var k = 0; k < 3; k++)
                {
                    var original = tris[t + k];
                    if (!remap.TryGetValue(original, out var mapped))
                    {
                        mapped = combinedVerts.Count;
                        remap[original] = mapped;
                        combinedVerts.Add(verts[original]);
                        combinedNormals.Add(normals.Length > original ? normals[original] : Vector3.up);
                        combinedUvs.Add(uvs.Length > original ? uvs[original] : Vector2.zero);
                    }

                    combinedTris.Add(mapped);
                }
            }
        }

        log.AppendLine($"Combinado: {combinedVerts.Count} vertices / {combinedTris.Count / 3} triangulos em 1 mesh");

        // Normalize: centered on its own bounds and scaled so the longest axis
        // is exactly 1. The scene then sets the hand's real size in meters as
        // a plain scale value, instead of an unexplainable magic number.
        var min = combinedVerts[0];
        var max = combinedVerts[0];
        foreach (var v in combinedVerts)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        var center = (min + max) * 0.5f;
        var size = max - min;
        var longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        var normalizeScale = longest > 0f ? 1f / longest : 1f;

        for (var i = 0; i < combinedVerts.Count; i++)
            combinedVerts[i] = (combinedVerts[i] - center) * normalizeScale;

        log.AppendLine($"Bounds originais: size={size} -> normalizado para eixo maior = 1 (fator {normalizeScale:F5})");

        if (!AssetDatabase.IsValidFolder(OutputFolder))
            AssetDatabase.CreateFolder("Assets/Art", "Generated");

        WriteMesh(OutputMesh, "Hand_Single", combinedVerts, combinedNormals, combinedUvs, combinedTris);

        // Mirrored copy for the other hand. Done as real geometry (flip X,
        // flip normals, reverse winding) rather than a negative scale on the
        // Transform, because negative scale inverts triangle winding at render
        // time and makes the mesh light and cull inside-out.
        var mirroredVerts = new List<Vector3>(combinedVerts.Count);
        var mirroredNormals = new List<Vector3>(combinedNormals.Count);
        foreach (var v in combinedVerts)
            mirroredVerts.Add(new Vector3(-v.x, v.y, v.z));
        foreach (var n in combinedNormals)
            mirroredNormals.Add(new Vector3(-n.x, n.y, n.z));

        var mirroredTris = new List<int>(combinedTris.Count);
        for (var t = 0; t < combinedTris.Count; t += 3)
        {
            mirroredTris.Add(combinedTris[t]);
            mirroredTris.Add(combinedTris[t + 2]);
            mirroredTris.Add(combinedTris[t + 1]);
        }

        WriteMesh(MirroredMesh, "Hand_Single_Mirrored", mirroredVerts, mirroredNormals, combinedUvs, mirroredTris);
        log.AppendLine($"Mesh espelhado salvo em {MirroredMesh}");

        AssetDatabase.SaveAssets();
        log.AppendLine($"Mesh salvo em {OutputMesh}");

        var outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "HandMeshCleanup.log");
        File.WriteAllText(outPath, log.ToString());
        Debug.Log("[HandMeshCleanup] OK. Log: " + outPath);
    }
}
