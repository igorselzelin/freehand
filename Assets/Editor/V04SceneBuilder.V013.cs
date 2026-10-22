using System.Collections.Generic;
using System.IO;
using Freehand.Customization;
using Freehand.Environment;
using Freehand.MR;
using Freehand.Paint;
using Freehand.Tools;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEngine;
#if FREEHAND_MR
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;
#endif

// V0.13: the mixed reality foundation. Idempotent: components on the existing rig are
// found or added (nothing is replaced, no second rig or camera is made), materials are
// assets updated in place, the panel's parts are found by name.
public static partial class V04SceneBuilder
{
    const string MRMaterialsFolder = "Assets/Materials/MR";
    const string MRTexturesFolder = "Assets/Art/Generated/MR";

    sealed class MRPanelParts
    {
        public Transform Root;
        public WorkshopButton Mode;
        public WorkshopButton Scan;
        public Renderer Plate;
        public Renderer Lamp;
    }

    static void BuildV013(GameObject sprayCan, GameObject room, GameObject left, GameObject right, GameObject xrOrigin, InputActionAssetRef inputActions, RoomLayoutProfile layout)
    {
        log.AppendLine(V013XRSetup.Configure());

        var headTransform = FindHead(xrOrigin);
        var headCamera = headTransform != null ? headTransform.GetComponent<Camera>() : null;
        if (xrOrigin == null || headCamera == null)
        {
            log.AppendLine("ERRO V0.13: XR Origin / camera principal nao encontrados - MR nao construido.");
            return;
        }

        EnsureFolder("Assets/Materials", "MR");
        var overlayMaterial = MRMaterial("MR_PaintOverlay");
        var frameMaterial = MRMaterial("MR_Frame");

#if FREEHAND_MR
        // The AR Foundation parts sit on the rig that is already there - and start switched off:
        // the game opens in the virtual room, with no passthrough and no plane query.
        var cameraManager = GetOrAdd<ARCameraManager>(headCamera.gameObject);
        cameraManager.enabled = false;
        EditorUtility.SetDirty(cameraManager);

        var planeManager = GetOrAdd<ARPlaneManager>(xrOrigin);
        planeManager.enabled = false;
        EditorUtility.SetDirty(planeManager);

        var sessionObject = GameObject.Find("AR Session") ?? new GameObject("AR Session");
        var session = GetOrAdd<ARSession>(sessionObject);
        session.enabled = false;
        EditorUtility.SetDirty(session);
#else
        // V0.15.1: built without the MR packages. The AR components they left on the rig are now scripts that no
        // longer exist: removed, together with the "AR Session" object, so the scene has no missing scripts.
        var removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(headCamera.gameObject)
                      + GameObjectUtility.RemoveMonoBehavioursWithMissingScript(xrOrigin);
        var oldSession = GameObject.Find("AR Session");
        if (oldSession != null)
        {
            removed += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(oldSession);
            Object.DestroyImmediate(oldSession);
        }

        log.AppendLine($"V0.15.1: build sem os pacotes de MR (FREEHAND_MR desligado) - {removed} componente(s) AR orfao(s) removido(s) da cena.");
#endif

        var mr = GameObject.Find("MixedReality") ?? new GameObject("MixedReality");
        var template = BuildWallTemplate(mr.transform, overlayMaterial);

        var hub = GetOrAdd<MRSurfaceHub>(mr);
        SetField(hub, "template", template);
        SetField(hub, "frameMaterial", frameMaterial);
        SetField(hub, "head", headCamera);
        SetField(hub, "surfaceRoot", mr.transform);
        EditorUtility.SetDirty(hub);

        var walls = GetOrAdd<ARPlaneSurfaceSource>(mr);
#if FREEHAND_MR
        SetField(walls, "planeManager", planeManager);
        SetField(walls, "session", session);
#endif
        SetField(walls, "hub", hub);
        EditorUtility.SetDirty(walls);

        var adaptive = Object.FindAnyObjectByType<AdaptiveRoomController>(FindObjectsInactive.Include);
        var panel = BuildMRPanel(room, layout);

        var controller = GetOrAdd<MRModeController>(mr);
        SetField(controller, "head", headCamera);
#if FREEHAND_MR
        SetField(controller, "session", session);
        SetField(controller, "cameraManager", cameraManager);
#endif
        SetField(controller, "walls", walls);
        SetField(controller, "hub", hub);
        SetField(controller, "layout", adaptive);

        // What the real room replaces: the virtual room's shell and scenery, its workshop, its ambience.
        var virtualOnly = new List<GameObject>();
        foreach (var name in new[] { "PaintWall", "WallLeft", "WallRight", "WallBack", "Ceiling", "UrbanProps", "CanWorkshop", "Ambience" })
        {
            var child = room.transform.Find(name);
            if (child != null)
                virtualOnly.Add(child.gameObject);
        }

        SetObjectArray(controller, "virtualOnly", virtualOnly.ToArray());
        var floor = room.transform.Find("Floor");
        SetObjectArray(controller, "hideRenderersInMixed", floor != null && floor.GetComponent<Renderer>() != null ? new[] { floor.GetComponent<Renderer>() } : new Renderer[0]);
        EditorUtility.SetDirty(controller);

        var panelComponent = GetOrAdd<MRPanel>(panel.Root.gameObject);
        SetField(panelComponent, "controller", controller);
        SetField(panelComponent, "modeButton", panel.Mode);
        SetField(panelComponent, "scanButton", panel.Scan);
        SetField(panelComponent, "plate", panel.Plate);
        SetField(panelComponent, "lamp", panel.Lamp);
        EditorUtility.SetDirty(panelComponent);

        foreach (var button in new[] { panel.Mode, panel.Scan })
        {
            SetField(button, "leftController", left != null ? left.transform : null);
            SetField(button, "rightController", right != null ? right.transform : null);
            EditorUtility.SetDirty(button);
        }

        var input = GetOrAdd<MRModeInput>(mr);
        SetField(input, "controller", controller);
        SetField(input, "inputActions", inputActions.Asset);
        EditorUtility.SetDirty(input);

        var diagnostics = GetOrAdd<MRDiagnostics>(mr);
        SetField(diagnostics, "controller", controller);
        SetField(diagnostics, "hub", hub);
        EditorUtility.SetDirty(diagnostics);

        if (adaptive != null)
        {
            SetField(adaptive, "mrPanelRoot", panel.Root);
            EditorUtility.SetDirty(adaptive);
        }

        EditorUtility.SetDirty(mr);
        log.AppendLine($"V0.13 MR: ARSession + ARCameraManager (passthrough) + ARPlaneManager (paredes) no rig existente, todos desligados ate o jogador entrar em MR; {virtualOnly.Count} objetos do quarto virtual somem em MR; painel MODO/SALA ao lado da mesa; atalho: os dois analogicos (clique) por {1.2f:F1}s.");
    }

    // ---------- materials ----------

    static Material MRMaterial(string name)
    {
        var path = $"{MRMaterialsFolder}/{name}.mat";
        var shader = Shader.Find("Freehand/MRPaintOverlay");
        if (shader == null)
        {
            log.AppendLine("ERRO V0.13: shader Freehand/MRPaintOverlay nao encontrado.");
            return null;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.shader = shader;
        material.SetColor("_BaseColor", Color.white);
        EditorUtility.SetDirty(material);
        return material;
    }

    // ---------- the wall template ----------

    // An inactive object the hub copies for every real wall: renderer (the overlay material),
    // collider, the overlay PaintSurface and the MRSurface. Its mesh, size and pose are set on the
    // copy before it is switched on.
    static GameObject BuildWallTemplate(Transform mr, Material overlayMaterial)
    {
        var template = FindOrCreate(mr, "WallTemplate", Vector3.zero);
        var filter = GetOrAdd<MeshFilter>(template);
        var renderer = GetOrAdd<MeshRenderer>(template);
        renderer.sharedMaterial = overlayMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        var collider = GetOrAdd<MeshCollider>(template);
        collider.convex = false;

        // A stand-in 1 x 1 m wall so the template is a complete, valid paint surface; every real wall
        // gets a mesh of its own size before it is switched on.
        var meshPath = $"{MRTexturesFolder}/WallQuad.asset";
        var quad = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (quad == null)
        {
            EnsureFolder("Assets/Art", "Generated");
            Directory.CreateDirectory(Path.Combine(Directory.GetParent(Application.dataPath).FullName, MRTexturesFolder));
            AssetDatabase.CreateAsset(MRSurface.BuildQuadMesh(Vector2.one, Vector2.zero), meshPath);
            quad = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        }

        filter.sharedMesh = quad;
        collider.sharedMesh = quad;

        var paint = GetOrAdd<PaintSurface>(template);
        SetBool(paint, "overlay", true);
        SetInt(paint, "textureSize", 512);
        SetBool(paint, "includeInClearAll", true);
        SetFloat(paint, "reliefThroughPaint", 0f);
        SetInt(paint, "preExistingGraffitiMarks", 0);

        var surface = GetOrAdd<MRSurface>(template);
        SetField(surface, "paintRenderer", renderer);
        SetField(surface, "paintCollider", collider);

        template.SetActive(false);
        EditorUtility.SetDirty(template);
        EditorUtility.SetDirty(filter);
        return template;
    }

    // ---------- the panel ----------

    static MRPanelParts BuildMRPanel(GameObject room, RoomLayoutProfile layout)
    {
        var parts = new MRPanelParts();
        var root = FindOrCreate(room.transform, "MRPanel", Vector3.zero);
        root.transform.SetPositionAndRotation(layout.MRPanelPosition(Vector3.zero), layout.MRPanelRotation(Vector3.zero));
        parts.Root = root.transform;

        var steel = CreateMaterial("MR_Steel", new Color(0.32f, 0.33f, 0.35f), 0.5f, 0.4f);
        var dark = CreateMaterial("MR_Board", new Color(0.09f, 0.095f, 0.1f), 0.3f);
        var housing = CreateMaterial("MR_ButtonHousing", new Color(0.18f, 0.19f, 0.2f), 0.4f);

        MakeBox(root.transform, "Post", new Vector3(0f, 0.47f, 0.02f), new Vector3(0.045f, 0.94f, 0.045f), steel, true);
        MakeBox(root.transform, "Foot", new Vector3(0f, 0.012f, 0.02f), new Vector3(0.26f, 0.024f, 0.26f), steel, true);

        // Front is local -Z, tilted up toward the face looking down at it.
        var board = FindOrCreate(root.transform, "Board", new Vector3(0f, 1.0f, 0f));
        board.transform.localPosition = new Vector3(0f, 1.0f, 0f);
        board.transform.localRotation = Quaternion.Euler(30f, 0f, 0f);
        MakeBox(board.transform, "Body", new Vector3(0f, 0f, 0.012f), new Vector3(0.30f, 0.24f, 0.024f), dark, true);

        var plateMaterial = CreateUnlitMaterial("MR_Plate", Color.white);
        var plate = MakeQuad(board.transform, "Plate", new Vector3(-0.01f, 0.06f, -0.001f), new Vector3(0.24f, 0.12f, 1f), plateMaterial);
        parts.Plate = plate.GetComponent<Renderer>();

        // The lamp's colour is set at run time by the panel.
        var lampMaterial = EmissiveMaterial("MR_Lamp", new Color(0.35f, 1f, 0.45f), 1.4f);
        var lamp = MakeCylinder(board.transform, "Lamp", new Vector3(0.132f, 0.1f, -0.003f), new Vector3(0.024f, 0.003f, 0.024f), lampMaterial, false);
        lamp.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        parts.Lamp = lamp.GetComponent<Renderer>();

        parts.Mode = BuildPanelButton(board.transform, "ModeButton", new Vector3(-0.075f, -0.07f, -0.002f), new Color(0.3f, 0.75f, 1f), housing);
        parts.Scan = BuildPanelButton(board.transform, "ScanButton", new Vector3(0.075f, -0.07f, -0.002f), new Color(1f, 0.7f, 0.25f), housing);
        MRLabel(board.transform, "Label_Modo", "MODO", new Vector3(-0.075f, -0.03f, -0.001f), new Vector2(0.08f, 0.026f));
        MRLabel(board.transform, "Label_Sala", "SALA", new Vector3(0.075f, -0.03f, -0.001f), new Vector2(0.08f, 0.026f));

        EditorUtility.SetDirty(root);
        return parts;
    }

    // A button whose cap travels along the board's normal: the cap's own up is the board's front.
    static WorkshopButton BuildPanelButton(Transform board, string name, Vector3 position, Color color, Material housing)
    {
        var button = BuildButton(board, name, position, color, housing);
        button.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        return button;
    }

    static void MRLabel(Transform parent, string name, string text, Vector3 position, Vector2 size)
    {
        var height = 64;
        var width = Mathf.Clamp(Mathf.RoundToInt(height * (size.x / size.y) / 4f) * 4, 64, 512);
        var background = new Color32(9, 10, 11, 255);
        var pixels = new Color32[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = background;

        var letter = height * 0.62f;
        var textWidth = StencilText.Measure(text) * letter;
        StencilText.Draw(pixels, width, height, text, new Vector2((width - textWidth) * 0.5f, (height - letter) * 0.5f), letter, Mathf.Max(2f, letter * 0.14f), new Color32(225, 222, 212, 255));
        var texture = SaveMRTexture($"{MRTexturesFolder}/{name}.png", pixels, width, height);
        var material = CreateUnlitMaterial("MR_" + name, Color.white);
        material.SetTexture("_BaseMap", texture);
        EditorUtility.SetDirty(material);
        MakeQuad(parent, name, position, new Vector3(size.x, size.y, 1f), material);
    }

    static Texture2D SaveMRTexture(string path, Color32[] pixels, int width, int height)
    {
        EnsureFolder("Assets/Art", "Generated");
        var temp = new Texture2D(width, height, TextureFormat.RGBA32, false);
        temp.SetPixels32(pixels);
        temp.Apply();
        var png = temp.EncodeToPNG();
        Object.DestroyImmediate(temp);

        var fullPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        if (!File.Exists(fullPath) || !SameBytes(File.ReadAllBytes(fullPath), png))
        {
            File.WriteAllBytes(fullPath, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ---------- validation ----------

    static void ValidateV013(System.Action<bool, string, string> check)
    {
        var mr = GameObject.Find("MixedReality");
        var controller = mr != null ? mr.GetComponent<MRModeController>() : null;
        var hub = mr != null ? mr.GetComponent<MRSurfaceHub>() : null;
        var source = mr != null ? mr.GetComponent<ARPlaneSurfaceSource>() : null;
        check(controller != null && hub != null && source != null, "MR: controlador, hub e fonte de paredes", controller != null && hub != null && source != null ? "presentes" : "ausentes");

#if FREEHAND_MR
        var session = Object.FindFirstObjectByType<ARSession>(FindObjectsInactive.Include);
        var cameraManager = Object.FindFirstObjectByType<ARCameraManager>(FindObjectsInactive.Include);
        var planeManager = Object.FindFirstObjectByType<ARPlaneManager>(FindObjectsInactive.Include);
        var off = session != null && !session.enabled && cameraManager != null && !cameraManager.enabled && planeManager != null && !planeManager.enabled;
        check(off, "MR: AR Foundation desligado na partida", off ? "ARSession, ARCameraManager e ARPlaneManager presentes e desligados (o jogo abre no VR)" : "faltando ou ligado");
#else
        var missing = 0;
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
        check(missing == 0 && GameObject.Find("AR Session") == null, "MR: build sem pacotes de MR (V0.15.1)", missing == 0 ? "nenhum script faltando na cena; MR indisponivel neste build" : $"{missing} script(s) faltando");
#endif

        check(Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include).Length == 1, "MR: um unico XR Origin", "sem rig duplicado");
        var mainCameras = 0;
        foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
        {
            if (camera.CompareTag("MainCamera"))
                mainCameras++;
        }

        check(mainCameras == 1, "MR: uma unica camera principal", $"{mainCameras} camera(s) MainCamera");
        var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include).Length;
        check(listeners == 1, "MR: um unico AudioListener", $"{listeners}");

        var template = hub != null ? Freehand_MR_Template(hub) : null;
        var templateOk = template != null && !template.activeSelf && template.GetComponent<PaintSurface>() != null && template.GetComponent<PaintSurface>().IsOverlay
                         && template.GetComponent<MeshRenderer>() != null && template.GetComponent<MeshRenderer>().sharedMaterial != null
                         && template.GetComponent<MeshRenderer>().sharedMaterial.shader.name == "Freehand/MRPaintOverlay";
        check(templateOk, "MR: molde de parede", templateOk ? "inativo, PaintSurface overlay, material Freehand/MRPaintOverlay" : "ausente ou mal configurado");

#if FREEHAND_MR
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        var featuresOk = settings != null && settings.GetFeature<ARSessionFeature>() != null && settings.GetFeature<ARSessionFeature>().enabled
                         && settings.GetFeature<ARCameraFeature>() != null && settings.GetFeature<ARCameraFeature>().enabled
                         && settings.GetFeature<ARPlaneFeature>() != null && settings.GetFeature<ARPlaneFeature>().enabled;
        check(featuresOk, "MR: features OpenXR (Android)", featuresOk ? "Session, Camera (Passthrough) e Planes ligadas" : "alguma desligada");
#endif
        check(PlayerSettings.Android.minSdkVersion >= AndroidSdkVersions.AndroidApiLevel32, "MR: API minima Android", $"{PlayerSettings.Android.minSdkVersion} (passthrough exige 32)");

        var panel = Object.FindFirstObjectByType<MRPanel>(FindObjectsInactive.Include);
        check(panel != null, "MR: painel MODO/SALA", panel != null ? "presente" : "ausente - nao ha como entrar em MR");
        var input = mr != null ? mr.GetComponent<MRModeInput>() : null;
        check(input != null, "MR: atalho dos analogicos", input != null ? "presente" : "ausente");
    }

    static GameObject Freehand_MR_Template(MRSurfaceHub hub)
    {
        var so = new SerializedObject(hub);
        var property = so.FindProperty("template");
        return property != null ? property.objectReferenceValue as GameObject : null;
    }
}
