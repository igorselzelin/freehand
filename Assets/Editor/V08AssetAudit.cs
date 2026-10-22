using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Read-only audit of the new fps-hands.fbx / SM_Spraybottle.fbx assets.
// Does not create GameObjects, prefabs, or materials, does not touch import
// settings, does not touch the scene. Only reads what Unity already
// imported and writes a report - exactly the "verify, don't integrate"
// scope this task asked for.
public static class V08AssetAudit
{
    const string HandPath = "Assets/Art/External/Hands/fps-hands.fbx";
    const string CanPath = "Assets/Art/External/SprayCan/SM_Spraybottle.fbx";

    [MenuItem("Freehand/V0.8/1 - Audit New Assets (Hands + Can)")]
    public static void Audit()
    {
        var log = new StringBuilder();

        log.AppendLine("================ V0.8 ASSET AUDIT ================");
        log.AppendLine();

        AuditModel(HandPath, "MAO/BRACO (fps-hands.fbx)", log);
        log.AppendLine();
        AuditModel(CanPath, "LATA (SM_Spraybottle.fbx)", log);

        var outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "V08AssetAudit.log");
        File.WriteAllText(outPath, log.ToString());
        Debug.Log($"[V08AssetAudit] OK. Log: {outPath}");
    }

    static void AuditModel(string path, string label, StringBuilder log)
    {
        log.AppendLine($"---------------- {label} ----------------");
        log.AppendLine($"Path: {path}");

        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
        {
            log.AppendLine("ERRO: ModelImporter nao encontrado neste path - o arquivo ainda nao foi importado pelo Unity (abra o Editor e deixe importar).");
            return;
        }

        log.AppendLine();
        log.AppendLine("[ModelImporter settings]");
        log.AppendLine($"  globalScale = {importer.globalScale}");
        log.AppendLine($"  useFileScale = {importer.useFileScale}");
        log.AppendLine($"  importAnimation = {importer.importAnimation}");
        log.AppendLine($"  animationType = {importer.animationType}");
        log.AppendLine($"  avatarSetup = {importer.avatarSetup}");
        log.AppendLine($"  materialImportMode = {importer.materialImportMode}");
        log.AppendLine($"  importBlendShapes = {importer.importBlendShapes}");
        log.AppendLine($"  importVisibility = {importer.importVisibility}");
        log.AppendLine($"  importCameras = {importer.importCameras}");
        log.AppendLine($"  importLights = {importer.importLights}");
        log.AppendLine($"  isReadable = {importer.isReadable}");
        log.AppendLine($"  meshCompression = {importer.meshCompression}");
        log.AppendLine($"  optimizeMeshPolygons = {importer.optimizeMeshPolygons}");
        log.AppendLine($"  optimizeMeshVertices = {importer.optimizeMeshVertices}");
        log.AppendLine($"  importNormals = {importer.importNormals}");
        log.AppendLine($"  importTangents = {importer.importTangents}");

        var allAssets = AssetDatabase.LoadAllAssetsAtPath(path);
        if (allAssets == null || allAssets.Length == 0)
        {
            log.AppendLine("ERRO: nenhum sub-asset encontrado neste path.");
            return;
        }

        var meshes = new List<Mesh>();
        var materials = new List<Material>();
        var clips = new List<AnimationClip>();
        var gameObjects = new List<GameObject>();
        Avatar avatar = null;

        foreach (var asset in allAssets)
        {
            switch (asset)
            {
                case Mesh mesh: meshes.Add(mesh); break;
                case Material mat: materials.Add(mat); break;
                case AnimationClip clip: clips.Add(clip); break;
                case GameObject go: gameObjects.Add(go); break;
                case Avatar av: avatar = av; break;
            }
        }

        log.AppendLine();
        log.AppendLine($"[Sub-assets] {allAssets.Length} total: {meshes.Count} Mesh, {materials.Count} Material, {clips.Count} AnimationClip, {gameObjects.Count} GameObject, avatar={(avatar != null ? "presente" : "ausente")}");

        log.AppendLine();
        log.AppendLine("[Meshes]");
        var totalTris = 0;
        var totalVerts = 0;
        foreach (var mesh in meshes)
        {
            var tris = mesh.triangles.Length / 3;
            totalTris += tris;
            totalVerts += mesh.vertexCount;
            log.AppendLine($"  '{mesh.name}': {mesh.vertexCount} vertices, {tris} triangulos, bounds={mesh.bounds.size}, blendShapes={mesh.blendShapeCount}, bonesPorVertice(boneWeights)={(mesh.boneWeights != null && mesh.boneWeights.Length > 0 ? mesh.boneWeights.Length : 0)}, bindposes={mesh.bindposes?.Length ?? 0}");
        }
        log.AppendLine($"  TOTAL: {totalVerts} vertices, {totalTris} triangulos em {meshes.Count} mesh(es).");

        log.AppendLine();
        log.AppendLine("[Materials]");
        foreach (var mat in materials)
        {
            log.AppendLine($"  '{mat.name}' shader='{mat.shader.name}'");
            var texProps = mat.GetTexturePropertyNames();
            foreach (var propName in texProps)
            {
                var tex = mat.GetTexture(propName);
                if (tex != null)
                    log.AppendLine($"    {propName} -> '{tex.name}' ({tex.width}x{tex.height})");
            }
        }

        log.AppendLine();
        log.AppendLine("[Animation Clips]");
        if (clips.Count == 0)
            log.AppendLine("  nenhuma AnimationClip encontrada.");
        foreach (var clip in clips)
            log.AppendLine($"  '{clip.name}' length={clip.length:F2}s frameRate={clip.frameRate} isLooping={clip.isLooping} empty={clip.empty}");

        log.AppendLine();
        log.AppendLine("[Root hierarchy + bone tree]");
        foreach (var go in gameObjects)
        {
            // Only walk from actual roots (no parent among the sub-assets) to
            // avoid printing the same sub-tree once per node.
            if (go.transform.parent != null)
                continue;

            DumpHierarchy(go.transform, log, 0);
        }

        log.AppendLine();
        log.AppendLine("[Renderers found in hierarchy]");
        foreach (var go in gameObjects)
        {
            if (go.transform.parent != null)
                continue;

            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                log.AppendLine($"  SkinnedMeshRenderer on '{GetPath(smr.transform)}': sharedMesh='{smr.sharedMesh?.name}', bones={smr.bones?.Length ?? 0}, rootBone='{smr.rootBone?.name}', materials=[{string.Join(", ", System.Array.ConvertAll(smr.sharedMaterials, m => m != null ? m.name : "null"))}]");
            }

            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                log.AppendLine($"  MeshRenderer on '{GetPath(mr.transform)}': sharedMesh='{mf?.sharedMesh?.name}', materials=[{string.Join(", ", System.Array.ConvertAll(mr.sharedMaterials, m => m != null ? m.name : "null"))}]");
            }
        }
    }

    static void DumpHierarchy(Transform t, StringBuilder log, int depth)
    {
        var indent = new string(' ', depth * 2);
        var comps = new List<string>();
        if (t.GetComponent<SkinnedMeshRenderer>() != null) comps.Add("SkinnedMeshRenderer");
        if (t.GetComponent<MeshRenderer>() != null) comps.Add("MeshRenderer");
        if (t.GetComponent<MeshFilter>() != null) comps.Add("MeshFilter");
        var compsStr = comps.Count > 0 ? $"  [{string.Join(",", comps)}]" : "";

        log.AppendLine($"{indent}{t.name}  pos={t.localPosition} rot={t.localRotation.eulerAngles} scale={t.localScale}{compsStr}");

        for (var i = 0; i < t.childCount; i++)
            DumpHierarchy(t.GetChild(i), log, depth + 1);
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }

        return path;
    }
}
