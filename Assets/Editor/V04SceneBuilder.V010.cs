using System.Collections.Generic;
using Freehand.Audio;
using Freehand.Paint;
using Freehand.Spray;
using Freehand.Tools;
using Freehand.VR;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;

// V0.10: the Color Orb (replacing the HSV panel), hold-to-clear on the undo
// button. Same rules as the rest of the builder: everything found by name
// or created, asset-backed materials (a material assembled at runtime loses
// its shader variant in the Android build), idempotent.
public static partial class V04SceneBuilder
{
    const string OrbMaterialsFolder = "Assets/Materials/Orb";
    const string OrbSettingsPath = "Assets/Settings/ColorOrbSettings.asset";
    const string OrbArcMeshPath = GeneratedWorldFolder + "/ColorOrb_ValueArc.asset";

    static void BuildV010(GameObject sprayCan, GameObject left, GameObject right, GameObject xrOrigin, GameObject room,
        InputActionAssetRef inputActions, SprayCanSfx sfx, GameObject handsRig, Freehand.Environment.RoomLayoutProfile layout)
    {
        var head = FindHead(xrOrigin);
        var uiLine = BuildParticlesUnlitMaterial($"{OrbMaterialsFolder}/OrbBeam.mat", additive: true, texture: null);
        BuildColorOrb(sprayCan, left, right, head, inputActions, handsRig, uiLine);
        WireUndoHold(room, right, head, uiLine);
        BuildCeilingFixtures(room, layout);
    }

    // ---------- environment ----------

    // Two fluorescent fixtures on the ceiling: a steel housing and a lit
    // tube - light sources the room visibly has, without adding a realtime
    // light (the directional + one practical stay the lighting budget).
    // Children of the ceiling, sized against its scale, so they follow it
    // when AdaptiveRoomController resizes the room.
    static void BuildCeilingFixtures(GameObject room, Freehand.Environment.RoomLayoutProfile layout)
    {
        var ceiling = room.transform.Find("Ceiling");
        if (ceiling == null)
            return;

        var size = ceiling.localScale;
        var steel = CreateMaterial("RackStand", new Color(0.16f, 0.17f, 0.18f), 0.45f, 0.6f);
        var tube = CreateUnlitMaterial("FluorescentTube", new Color(0.94f, 0.97f, 1f));
        var spots = new[] { new Vector3(-0.45f, 0f, 0.45f), new Vector3(0.55f, 0f, -0.55f) };
        for (var i = 0; i < spots.Length; i++)
        {
            var housingSize = new Vector3(1.2f, 0.045f, 0.13f);
            var y = -size.y * 0.5f - housingSize.y * 0.5f;
            var housing = MakeBox(ceiling, $"Fixture_{i}", Divide(new Vector3(spots[i].x, y, spots[i].z), size), Divide(housingSize, size), steel, false);
            housing.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var tubeSize = new Vector3(1.12f, 0.014f, 0.04f);
            var light = MakeBox(ceiling, $"Fixture_{i}_Tube", Divide(new Vector3(spots[i].x, y - housingSize.y * 0.5f - tubeSize.y * 0.4f, spots[i].z), size), Divide(tubeSize, size), tube, false);
            light.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        log.AppendLine("Ambiente: 2 luminarias fluorescentes no teto (emissivas, sem luz em tempo real extra).");
    }

    static Vector3 Divide(Vector3 a, Vector3 b) => new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);

    // ---------- Color Orb ----------

    static void BuildColorOrb(GameObject sprayCan, GameObject left, GameObject right, Transform head, InputActionAssetRef inputActions, GameObject handsRig, Material uiLine)
    {
        EnsureFolder("Assets", "Materials");
        EnsureFolder("Assets/Materials", "Orb");
        EnsureFolder("Assets", "Settings");

        var settings = AssetDatabase.LoadAssetAtPath<ColorOrbSettings>(OrbSettingsPath);
        if (settings == null)
        {
            // Created once; never overwritten, so tuning done on it stays.
            settings = ScriptableObject.CreateInstance<ColorOrbSettings>();
            AssetDatabase.CreateAsset(settings, OrbSettingsPath);
        }

        var coreShader = Shader.Find("Freehand/ColorOrb");
        var haloShader = Shader.Find("Freehand/OrbHalo");
        if (coreShader == null || haloShader == null)
        {
            log.AppendLine("ERRO: shaders Freehand/ColorOrb ou Freehand/OrbHalo nao encontrados - Color Orb NAO construida.");
            return;
        }

        var coreMat = ShaderMaterial($"{OrbMaterialsFolder}/OrbCore.mat", coreShader, m => m.SetFloat("_Mode", 0f));
        var solidMat = ShaderMaterial($"{OrbMaterialsFolder}/OrbSolid.mat", coreShader, m => m.SetFloat("_Mode", 1f));
        var arcMat = ShaderMaterial($"{OrbMaterialsFolder}/OrbArc.mat", coreShader, m => m.SetFloat("_Mode", 2f));
        var haloMat = ShaderMaterial($"{OrbMaterialsFolder}/OrbHalo.mat", haloShader, m => m.SetFloat("_Intensity", 0.8f));
        var sparkMat = BuildParticlesUnlitMaterial($"{OrbMaterialsFolder}/OrbSparks.mat", additive: true, texture: AssetDatabase.LoadAssetAtPath<Texture2D>(SprayParticleTexturePath));

        var root = GameObject.Find("ColorOrb");
        if (root == null)
            root = new GameObject("ColorOrb");
        root.transform.SetParent(null, false);
        root.transform.SetPositionAndRotation(Vector3.up * -10f, Quaternion.identity);
        root.transform.localScale = Vector3.one;

        // Rebuilt from scratch every run: parts only, no state lives in them.
        for (var i = root.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(root.transform.GetChild(i).gameObject);

        var sphereSource = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var sphere = sphereSource.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(sphereSource);

        var body = new GameObject("Body").transform;
        body.SetParent(root.transform, false);
        var core = OrbPart(body, "Core", sphere, coreMat, settings.coreRadius * 2f);
        var halo = OrbPart(body, "Halo", sphere, haloMat, settings.coreRadius * 2f * settings.haloScale);
        var arcMesh = SaveMeshAsset(BuildArcTube(settings.valueArcRadius, settings.valueArcHalfAngle, 0.0022f), OrbArcMeshPath);
        var arc = OrbPart(root.transform, "ValueArc", arcMesh, arcMat, 1f);
        var knob = OrbPart(root.transform, "ValueKnob", sphere, solidMat, settings.beadRadius * 1.1f);
        var cursor = OrbPart(root.transform, "Cursor", sphere, solidMat, 0.007f);

        var beads = new Renderer[ColorManager.Count + 1];
        for (var i = 0; i < beads.Length; i++)
        {
            var name = i < ColorManager.Count ? $"Bead_{ColorManager.PaletteNames[i]}" : "Bead_Cleaner";
            beads[i] = OrbPart(root.transform, name, sphere, solidMat, settings.beadRadius * 2f);
        }

        var sparks = BuildOrbSparks(root.transform, sparkMat, settings);
        var beam = new GameObject("Beam").AddComponent<LineRenderer>();
        beam.transform.SetParent(root.transform, false);
        beam.useWorldSpace = true;
        beam.positionCount = 2;
        beam.widthMultiplier = 0.0022f;
        beam.numCapVertices = 2;
        beam.sharedMaterial = uiLine;
        beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        beam.receiveShadows = false;
        beam.enabled = false;

        var audio = GetOrAdd<AudioSource>(root);
        audio.playOnAwake = false;
        audio.spatialBlend = 1f;
        audio.minDistance = 0.3f;
        audio.maxDistance = 6f;

        var visual = GetOrAdd<ColorOrbVisual>(root);
        SetField(visual, "settings", settings);
        SetField(visual, "body", body);
        SetField(visual, "core", core);
        SetField(visual, "halo", halo);
        SetField(visual, "valueArc", arc);
        SetField(visual, "valueKnob", knob);
        SetField(visual, "cursor", cursor);
        SetArrayField(visual, "beads", beads);
        SetField(visual, "sparks", sparks);
        SetField(visual, "beam", beam);
        SetField(visual, "audioSource", audio);
        SetInt(visual, "cleanerBead", ColorManager.Count);

        var orb = GetOrAdd<ColorOrb>(root);
        SetField(orb, "settings", settings);
        SetField(orb, "visual", visual);
        SetField(orb, "inputActions", inputActions.Asset);
        SetField(orb, "leftController", left.transform);
        SetField(orb, "rightController", right.transform);
        SetField(orb, "leftGrabber", left.GetComponent<VRGrabber>());
        SetField(orb, "rightGrabber", right.GetComponent<VRGrabber>());
        SetField(orb, "handRig", handsRig != null ? handsRig.GetComponent<HandRig>() : null);
        SetField(orb, "head", head);
        SetField(orb, "colorManager", sprayCan.GetComponent<ColorManager>());
        SetField(orb, "canController", sprayCan.GetComponent<SprayCanController>());
        SetField(orb, "sprayEmitter", sprayCan.GetComponent<SprayEmitter>());
        SetField(orb, "canGrab", sprayCan.GetComponent<VRGrabInteractable>());
        EditorUtility.SetDirty(orb);

        var map = inputActions.Asset.FindActionMap("XR", false);
        var leftOk = map?.FindAction("Left Primary Button", false) != null;
        var rightOk = map?.FindAction("Right Primary Button", false) != null;
        log.AppendLine($"Color Orb: esfera sobre a palma (X esquerda / A direita), {beads.Length} contas (6 cores + cleaner), arco de brilho, halo aditivo, faiscas (max 24), shaders Freehand/ColorOrb + OrbHalo. Actions: Left Primary {(leftOk ? "ok" : "AUSENTE")}, Right Primary {(rightOk ? "ok" : "AUSENTE")}.");
    }

    static Renderer OrbPart(Transform parent, string name, Mesh mesh, Material material, float size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * size;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.enabled = false;
        return renderer;
    }

    // A thin tube along an arc on the +X side of the orb's face plane
    // (z = 0); uv.x runs 0 (bottom) -> 1 (top): the brightness slider.
    static Mesh BuildArcTube(float radius, float halfAngleDegrees, float tubeRadius)
    {
        const int along = 48;
        const int around = 8;
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        for (var i = 0; i <= along; i++)
        {
            var u = i / (float)along;
            var angle = Mathf.Lerp(-halfAngleDegrees, halfAngleDegrees, u) * Mathf.Deg2Rad;
            var center = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * radius;
            var outward = center.normalized;
            for (var j = 0; j < around; j++)
            {
                var a = j * Mathf.PI * 2f / around;
                var normal = outward * Mathf.Cos(a) + Vector3.forward * Mathf.Sin(a);
                positions.Add(center + normal * tubeRadius);
                normals.Add(normal);
                uvs.Add(new Vector2(u, j / (float)around));
            }
        }

        for (var i = 0; i < along; i++)
        {
            for (var j = 0; j < around; j++)
            {
                var a = i * around + j;
                var b = i * around + (j + 1) % around;
                var c = (i + 1) * around + j;
                var d = (i + 1) * around + (j + 1) % around;
                var outward = normals[a] + normals[d];
                AddFacing(triangles, positions, a, c, d, outward);
                AddFacing(triangles, positions, a, d, b, outward);
            }
        }

        var mesh = new Mesh { name = "ColorOrb_ValueArc" };
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    static ParticleSystem BuildOrbSparks(Transform root, Material material, ColorOrbSettings settings)
    {
        var go = new GameObject("Sparks");
        go.transform.SetParent(root, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.duration = 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.05f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.003f, 0.008f);
        main.startColor = new Color(1f, 1f, 1f, 0.9f);
        main.maxParticles = 24;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = 5f;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = settings.coreRadius * 1.4f;
        shape.radiusThickness = 0.2f;

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.04f;
        noise.frequency = 1.5f;
        noise.quality = ParticleSystemNoiseQuality.Low;

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = gradient;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return ps;
    }

    static Material ShaderMaterial(string path, Shader shader, System.Action<Material> configure)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        configure(material);
        EditorUtility.SetDirty(material);
        return material;
    }

    // URP Particles/Unlit, transparent, vertex color multiplied (lines and
    // particles carry their color in vertices), keywords set the way URP's
    // own material inspector does.
    static Material BuildParticlesUnlitMaterial(string path, bool additive, Texture2D texture)
    {
        EnsureFolder("Assets", "Materials");
        EnsureFolder("Assets/Materials", "Orb");
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        if (texture != null)
            material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Surface", (float)BaseShaderGUI.SurfaceType.Transparent);
        material.SetFloat("_Blend", (float)(additive ? BaseShaderGUI.BlendMode.Additive : BaseShaderGUI.BlendMode.Alpha));
        material.SetFloat("_ColorMode", (float)ParticleGUI.ColorMode.Multiply);
        material.SetFloat("_AlphaClip", 0f);
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        material.SetFloat("_SoftParticlesEnabled", 0f);
        material.SetFloat("_CameraFadingEnabled", 0f);
        BaseShaderGUI.SetMaterialKeywords(material, null, ParticleGUI.SetMaterialKeywords);
        EditorUtility.SetDirty(material);
        return material;
    }

    // ---------- hold B to clear all ----------

    static void WireUndoHold(GameObject room, GameObject right, Transform head, Material uiLine)
    {
        var holder = FindOrCreate(room.transform, "PaintControls", Vector3.zero);
        var undo = GetOrAdd<UndoControl>(holder);

        var ringObject = FindOrCreate(holder.transform, "ClearProgressRing", Vector3.zero);
        var ring = GetOrAdd<LineRenderer>(ringObject);
        ring.useWorldSpace = true;
        ring.loop = false;
        ring.positionCount = 0;
        ring.widthMultiplier = 0.004f;
        ring.numCapVertices = 3;
        ring.sharedMaterial = uiLine;
        ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ring.receiveShadows = false;
        ring.enabled = false;

        SetField(undo, "buttonHand", right.transform);
        SetBool(undo, "buttonHandIsLeft", false);
        SetField(undo, "head", head);
        SetField(undo, "progressRing", ring);
        EditorUtility.SetDirty(undo);
        log.AppendLine("UndoControl: toque em B desfaz; segurar B 2s limpa todas as paredes (uma acao, desfazivel), anel de progresso sobre a mao direita + haptico.");
    }
}
