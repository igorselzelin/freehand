using System.Collections.Generic;
using System.IO;
using System.Text;
using Freehand.Audio;
using Freehand.Environment;
using Freehand.Paint;
using Freehand.Spray;
using Freehand.Tools;
using Freehand.VR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Applies the V0.4 scene changes in one pass. Safe to re-run: every step
// either finds what it already created or creates it.
//
// Deliberately does NOT touch XR Origin, Camera Offset, Main Camera,
// TrackedPoseDriver, XRInputBootstrap or the XR action map - the controller
// GameObjects are only ever read from, except for switching off the old
// placeholder cube renderer.
public static partial class V04SceneBuilder
{
    const string RoomMaterialsFolder = "Assets/Materials/Room";
    const string HandMeshPath = "Assets/Art/Generated/Hand_Single.asset";
    const string HandMeshMirroredPath = "Assets/Art/Generated/Hand_Single_Mirrored.asset";

    // V0.8 part 2 - real, rigged hand and can assets (see V08AssetAudit.log).
    const string HandRigFbxPath = "Assets/Art/External/Hands/fps-hands.fbx";
    const string CanFbxPath = "Assets/Art/External/SprayCan/SM_Spraybottle.fbx";
    const string CanTexturesFolder = "Assets/Art/External/SprayCan/Textures";

    // Real hand length, wrist to fingertip. The generated mesh is normalized
    // to 1 unit on its longest axis, so this is literally the size in meters.
    const float HandLengthMeters = 0.19f;

    static StringBuilder log;

    [MenuItem("Freehand/V0.4/2 - Build V0.4 Scene")]
    public static void Build()
    {
        log = new StringBuilder();
        measuredCanBodyCenter = null;

        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            Debug.LogError("[V04SceneBuilder] No active scene.");
            return;
        }

        var sprayCan = GameObject.Find("SprayCan");
        var leftController = GameObject.Find("Left Controller");
        var rightController = GameObject.Find("Right Controller");
        var paintWall = GameObject.Find("PaintWall");
        var xrOrigin = GameObject.Find("XR Origin");

        if (sprayCan == null || leftController == null || rightController == null || paintWall == null)
        {
            Debug.LogError("[V04SceneBuilder] Missing one of: SprayCan, Left Controller, Right Controller, PaintWall.");
            return;
        }

        var inputActions = FindInputActions();
        if (inputActions == null)
        {
            Debug.LogError("[V04SceneBuilder] Could not find InputSystem_Actions asset.");
            return;
        }

        EnsureFolder("Assets/Materials", "Room");

        // Compact is the authored/edit-time layout and the runtime fallback
        // when no boundary is available (see AdaptiveRoomController) - this
        // project's primary target is a small room, so the room is built
        // small by default rather than needing every profile-driven object
        // to jump on first Play.
        var layout = RoomLayoutProfile.Compact();

        HideControllerPlaceholders(leftController, rightController);
        var handsOk = RebuildHands(leftController, rightController);
        var room = BuildRoom(layout);
        var wallLeft = room.transform.Find("WallLeft")?.gameObject;
        var wallRight = room.transform.Find("WallRight")?.gameObject;
        RemoveLegacyPaintSurfaces(paintWall, wallLeft, wallRight);
        ConfigurePaintWall(paintWall, room, layout);
        ConfigurePaintableSideWalls(wallLeft, wallRight);
        ConfigureLighting();
        var sfx = ConfigureCan(sprayCan, inputActions, leftController, rightController, layout);

        // V0.8.x: the real can model, its cap socket and the seated Standard
        // cap - all derived from the model's measured geometry. The parked
        // caps are built with the rack, below, from the same generated cap.
        var capData = RebuildCanVisualFromRealMesh(sprayCan);

        var racks = BuildRacks(room, sprayCan, leftController, rightController, sfx, layout, capData);
        BuildUndoControl(room, inputActions, sfx);
        ConfigureAdaptiveRoom(room, sprayCan, paintWall, xrOrigin, racks);

        // V0.6 physical interaction pass.
        ConfigureHandPoses(leftController, rightController, sprayCan);
        ConfigureNozzleAnimator(sprayCan, sfx);
        ConfigureShakeVisualFeedback(sprayCan);

        // V0.7 graffiti spot / environment pass.
        BuildUrbanProps(room, layout);
        ConfigurePracticalLighting(room);
        BuildAmbience(room);

        // V0.8 interaction realism pass.
        ConfigureCanWallClearance(sprayCan, paintWall, wallLeft, wallRight);
        ConfigureGrabbers(leftController, rightController);

        // V0.8 part 2: real rigged hands (fps-hands.fbx), replacing the old
        // rigless fused-mesh HandVisual as the visible hand. The old one is
        // deactivated, not deleted (Freehand/V0.4/1 mesh asset and the
        // legacy HandPoseController wiring both stay intact as a fallback -
        // see BuildHand/ConfigureHandPoses above, both still run unchanged).
        var handsRig = RebuildHandsRigged(leftController, rightController, xrOrigin);
        ConfigureSkinWeights();
        if (handsRig != null)
        {
            DeactivateLegacyHandVisual(leftController);
            DeactivateLegacyHandVisual(rightController);
        }

        // V0.10: Color Orb, hold-to-clear on the undo button.
        BuildV010(sprayCan, leftController, rightController, xrOrigin, room, inputActions, sfx, handsRig, layout);

        // Sound polish: motion/impact audio on the can and caps, what each surface is made of.
        BuildAudio(sprayCan, room, sfx);

        // V0.11: item inspection.
        BuildV011(sprayCan, leftController, rightController, inputActions);

        // V0.12: the can's own look, the can workshop, the player's skin.
        BuildV012(sprayCan, room, leftController, rightController, xrOrigin, inputActions, layout);

        // V0.13: mixed reality foundation (passthrough, real walls, the mode panel).
        BuildV013(sprayCan, room, leftController, rightController, xrOrigin, inputActions, layout);

        EditorSceneManager.MarkSceneDirty(scene);

        log.AppendLine();
        log.AppendLine("================ VALIDACAO ================");
        var problems = ValidateScene();

        // Saved here rather than left dirty: the builder runs inside the
        // Editor process, so there is no risk of racing an external write, and
        // an unsaved scene silently loses everything on the next reload.
        EditorSceneManager.SaveScene(scene);
        log.AppendLine($"Cena salva em {scene.path}.");

        var outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "V04SceneBuilder.log");
        File.WriteAllText(outPath, log.ToString());

        if (problems == 0)
            Debug.Log($"[V04SceneBuilder] OK (hands rebuilt: {handsOk}). Cena salva, validacao sem problemas. Log: {outPath}");
        else
            Debug.LogWarning($"[V04SceneBuilder] Concluido com {problems} problema(s) na validacao - ver {outPath}");
    }

    // ---------- 4.1 controller placeholder ----------

    static void HideControllerPlaceholders(GameObject left, GameObject right)
    {
        foreach (var controller in new[] { left, right })
        {
            var renderer = controller.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.enabled)
            {
                renderer.enabled = false;
                EditorUtility.SetDirty(renderer);
                log.AppendLine($"Placeholder cube desativado em {controller.name} (MeshRenderer.enabled = false).");
            }
            else
            {
                log.AppendLine($"{controller.name}: placeholder ja estava desativado.");
            }
        }
    }

    // ---------- 5 hands ----------

    static bool RebuildHands(GameObject left, GameObject right)
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(HandMeshPath);
        var mirrored = AssetDatabase.LoadAssetAtPath<Mesh>(HandMeshMirroredPath);

        if (mesh == null || mirrored == null)
        {
            log.AppendLine("AVISO: mesh da mao nao encontrado - rode 'Freehand/V0.4/1 - Build Single Hand Mesh' antes. Mãos nao alteradas.");
            return false;
        }

        var material = CreateMaterial("Hand", new Color(0.78f, 0.63f, 0.55f), 0.35f);

        // Confirmed by physical Quest 3 test (thumb side): the unmirrored
        // "base" mesh reads anatomically as a RIGHT hand, and its mirror reads
        // as a LEFT hand - the opposite of the V0.4 assignment. There is no
        // negative scale or extra rotation anywhere in this method or its
        // parent chain (verified: visual and hand transforms both use
        // Vector3.one/identity), so swapping which mesh goes to which
        // controller is the entire fix.
        BuildHand(left, mirrored, material, "Left");
        BuildHand(right, mesh, material, "Right");
        return true;
    }

    static void BuildHand(GameObject controller, Mesh mesh, Material material, string label)
    {
        var visual = controller.transform.Find("HandVisual");
        if (visual == null)
        {
            visual = new GameObject("HandVisual").transform;
            visual.SetParent(controller.transform, false);
        }

        // Everything under HandVisual is replaced - this is what removes the
        // old glTF prefab instance that was drawing both copies of the hand.
        for (var i = visual.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(visual.GetChild(i).gameObject);

        // The controller transform carries a 0.04 scale from the old cube
        // placeholder, so cancel it here to express the hand in real meters.
        var parentScale = controller.transform.localScale.x;
        var localScale = parentScale > 0.0001f ? HandLengthMeters / parentScale : HandLengthMeters;

        visual.localPosition = Vector3.zero;
        visual.localRotation = Quaternion.identity;
        visual.localScale = Vector3.one * localScale;

        var hand = new GameObject("Hand");
        hand.transform.SetParent(visual, false);
        hand.transform.localPosition = Vector3.zero;
        hand.transform.localRotation = Quaternion.identity;
        hand.transform.localScale = Vector3.one;

        hand.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = hand.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        log.AppendLine($"{label} hand: 1 mesh ({mesh.name}, {mesh.triangles.Length / 3} tris), escala local {localScale:F3} (= {HandLengthMeters} m).");
    }

    // ---------- 20 room ----------

    static GameObject BuildRoom(RoomLayoutProfile layout)
    {
        var room = GameObject.Find("Room");
        if (room == null)
        {
            room = new GameObject("Room");
            log.AppendLine("Room criada.");
        }

        // Colliders kept on the room shell and pedestal: the can has a
        // Rigidbody with gravity, so without them a dropped can falls forever.
        // The front of the room is deliberately missing - PaintWall is the
        // front wall, configured separately. Sizes/distances all come from
        // the layout profile (see RoomLayoutProfile) rather than being typed
        // here, so AdaptiveRoomController can later re-apply a different
        // profile to these exact same objects without a second code path.
        var depth = layout.roomHalfDepth + layout.wallDistance;
        var width = layout.roomHalfWidth * 2f;

        // V0.9: cast concrete floor and ceiling (1.2m slabs), a block back
        // wall (tile = 0.8m). The side walls keep the plain RoomWall
        // material: they are paint surfaces, and PaintSurface's own
        // texture inherits whatever tiling their material has.
        var concrete = BuildConcreteTexture();
        var floorMat = CreateTexturedMaterial("RoomFloor", concrete, new Vector2(width / 1.2f, depth / 1.2f), new Color(0.55f, 0.55f, 0.57f), 0.22f, 0f);
        var ceilingMat = CreateTexturedMaterial("RoomCeiling", concrete, new Vector2(width / 1.2f, depth / 1.2f), new Color(0.42f, 0.42f, 0.44f), 0.1f, 0f);
        var wallMat = CreateMaterial("RoomWall", new Color(0.34f, 0.33f, 0.31f), 0.1f);
        var backWallMat = CreateTexturedMaterial("RoomWallBack", BuildBlockTexture(), new Vector2(width / 0.8f, layout.wallHeight / 0.8f), new Color(0.78f, 0.77f, 0.74f), 0.08f, 0f);

        MakeBox(room.transform, "Floor", new Vector3(0f, -0.05f, 0f), new Vector3(width, 0.1f, depth), floorMat, true);
        MakeBox(room.transform, "Ceiling", new Vector3(0f, layout.wallHeight + 0.05f, 0f), new Vector3(width, 0.1f, depth), ceilingMat, false);
        MakeBox(room.transform, "WallLeft", new Vector3(-layout.roomHalfWidth, layout.wallHeight * 0.5f, 0f), new Vector3(0.2f, layout.wallHeight, depth), wallMat, true);
        MakeBox(room.transform, "WallRight", new Vector3(layout.roomHalfWidth, layout.wallHeight * 0.5f, 0f), new Vector3(0.2f, layout.wallHeight, depth), wallMat, true);
        MakeBox(room.transform, "WallBack", new Vector3(0f, layout.wallHeight * 0.5f, -layout.roomHalfDepth), new Vector3(width, layout.wallHeight, 0.2f), backWallMat, true);

        // Tool table on the player's left: the can rests on its front half,
        // the cap rack on its back half. It replaces the V0.4 pedestal that
        // stood in front of the player, inside the spraying cone - renamed in
        // place so anything already referencing that object keeps working.
        var legacyPedestal = room.transform.Find("CanPedestal");
        if (legacyPedestal != null && room.transform.Find("ToolTable") == null)
            legacyPedestal.name = "ToolTable";

        var tableCenter = layout.ToolTableCenter(Vector3.zero);
        var table = MakeBox(room.transform, "ToolTable", tableCenter, layout.toolTableSize,
            CreateMaterial("ToolTable", new Color(0.2f, 0.21f, 0.22f), 0.3f, 0.2f), true);
        DressToolTable(table.transform, layout.toolTableSize);

        log.AppendLine($"Room ({layout.profileName}): shell {width:F1}x{depth:F1}m, altura {layout.wallHeight:F1}m.");
        return room;
    }

    // Only the surfaces this builder itself authors (PaintWall + the two V0.7
    // side walls) may carry a PaintSurface. Any other PaintSurface in the
    // scene is a leftover from an earlier prototype: it competes for the
    // spray raycast and reads as a stray slab floating in the room.
    static void RemoveLegacyPaintSurfaces(params GameObject[] canonical)
    {
        var surfaces = Object.FindObjectsByType<PaintSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var removed = 0;

        foreach (var surface in surfaces)
        {
            if (surface == null || System.Array.IndexOf(canonical, surface.gameObject) >= 0)
                continue;

            log.AppendLine($"Superficie de pintura legada removida: '{GetPath(surface.transform)}'.");
            Object.DestroyImmediate(surface.gameObject);
            removed++;
        }

        // Prototype leftovers that are not PaintSurfaces but still clutter the
        // room. Disabled rather than destroyed - they cost nothing and keep the
        // early test setup recoverable.
        foreach (var legacyName in new[] { "TEST_Ground", "TEST_Cube" })
        {
            var legacy = GameObject.Find(legacyName);
            if (legacy != null && legacy.activeSelf)
            {
                legacy.SetActive(false);
                EditorUtility.SetDirty(legacy);
                log.AppendLine($"{legacyName} desativado (legado do prototipo).");
            }
        }

        var canonicalNames = string.Join(", ", System.Array.ConvertAll(canonical, g => g != null ? g.name : "null"));
        log.AppendLine($"Superficies de pintura legadas removidas: {removed}. Restaram as {canonical.Length} canonicas ({canonicalNames}).");
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

    static void ConfigurePaintWall(GameObject paintWall, GameObject room, RoomLayoutProfile layout)
    {
        // The paint wall IS the front wall of the room, not a panel standing in
        // front of one. Previously it hung in mid-air at z=2.4 with no wall
        // behind it, which is exactly why it read as a leftover object.
        paintWall.transform.SetParent(room.transform, true);
        paintWall.name = "PaintWall";
        paintWall.transform.position = new Vector3(0f, layout.wallHeight * 0.5f, layout.wallDistance);
        paintWall.transform.rotation = Quaternion.identity;
        paintWall.transform.localScale = new Vector3(layout.wallWidth, layout.wallHeight, 0.2f);

        // RaycastHit.textureCoord is only filled in for MeshCollider hits, so
        // this has to stay a MeshCollider - a BoxCollider silently makes every
        // stamp land on the same texel.
        var box = paintWall.GetComponent<BoxCollider>();
        if (box != null)
            Object.DestroyImmediate(box);

        var meshCollider = paintWall.GetComponent<MeshCollider>();
        if (meshCollider == null)
        {
            meshCollider = paintWall.AddComponent<MeshCollider>();
            var filter = paintWall.GetComponent<MeshFilter>();
            if (filter != null)
                meshCollider.sharedMesh = filter.sharedMesh;
        }

        // The scene still carries the V0.2 value of 256 for a wall that is now
        // 4.4 m wide (~58 px/m, visibly blocky). 512 doubles the detail and the
        // new batched upload path absorbs the cost.
        var surface = paintWall.GetComponent<PaintSurface>();
        if (surface != null)
        {
            SetInt(surface, "textureSize", 512);

            // V0.7: this is the main graffiti wall - Concrete look, a couple
            // of pre-existing marks so the room does not start looking unused.
            SetString(surface, "surfaceId", "MainWall");
            SetEnumField(surface, "materialKind", (int)SurfaceMaterialKind.Concrete);
            SetFloat(surface, "baseSmoothness", 0.12f);
            SetFloat(surface, "baseMetallic", 0f);
            SetInt(surface, "preExistingGraffitiMarks", 3);
            SetInt(surface, "graffitiRandomSeed", "MainWall".GetHashCode());
        }

        EditorUtility.SetDirty(paintWall);
        log.AppendLine($"PaintWall: pos={paintWall.transform.position} escala={paintWall.transform.localScale}, MeshCollider preservado, textureSize=512, materialKind=Concrete.");
    }

    // ---------- V0.7 additional paintable surfaces ----------

    // Reuses the two existing room-shell side walls (built by BuildRoom, no
    // new geometry) as two more paintable surfaces, so "pelo menos 3
    // superficies pintaveis" costs zero extra GameObjects.
    static void ConfigurePaintableSideWalls(GameObject wallLeft, GameObject wallRight)
    {
        ConfigureWallAsPaintable(wallRight, "SideWall_Right", SurfaceMaterialKind.Brick,
            new Color(0.5f, 0.28f, 0.22f), 0.08f, 0f, 2);
        ConfigureWallAsPaintable(wallLeft, "SideWall_Left", SurfaceMaterialKind.Metal,
            new Color(0.42f, 0.45f, 0.49f), 0.55f, 0.35f, 1);
    }

    static void ConfigureWallAsPaintable(GameObject wall, string surfaceId, SurfaceMaterialKind kind, Color baseColor, float smoothness, float metallic, int graffitiMarks)
    {
        if (wall == null)
        {
            log.AppendLine($"AVISO: '{surfaceId}' nao configurada - parede nao encontrada.");
            return;
        }

        // Same fix PaintWall already needed: RaycastHit.textureCoord is only
        // populated for MeshCollider hits.
        var box = wall.GetComponent<BoxCollider>();
        if (box != null)
            Object.DestroyImmediate(box);

        var meshCollider = wall.GetComponent<MeshCollider>();
        if (meshCollider == null)
        {
            meshCollider = wall.AddComponent<MeshCollider>();
            var filter = wall.GetComponent<MeshFilter>();
            if (filter != null)
                meshCollider.sharedMesh = filter.sharedMesh;
        }

        var surface = wall.GetComponent<PaintSurface>() ?? wall.AddComponent<PaintSurface>();
        SetString(surface, "surfaceId", surfaceId);
        SetInt(surface, "textureSize", 512);
        SetColorField(surface, "baseColor", baseColor);
        SetEnumField(surface, "materialKind", (int)kind);
        SetFloat(surface, "baseSmoothness", smoothness);
        SetFloat(surface, "baseMetallic", metallic);
        SetInt(surface, "preExistingGraffitiMarks", graffitiMarks);
        SetInt(surface, "graffitiRandomSeed", surfaceId.GetHashCode());
        EditorUtility.SetDirty(surface);

        log.AppendLine($"{surfaceId}: PaintSurface adicionado a '{GetPath(wall.transform)}' (MeshCollider, materialKind={kind}).");
    }

    // ---------- V0.7 urban props ----------

    // Plain primitives only (CreatePrimitive Cube/Cylinder), per the brief's
    // explicit preference over importing external low-poly packs - keeps the
    // scene reproducible with zero new licensing to track. Placed in the
    // room's back corners (roomHalfWidth/roomHalfDepth scaled, not fixed
    // meters) so they land sensibly for the Compact layout this builder
    // authors; see the V0.7 report for why they do not currently follow
    // AdaptiveRoomController across Standard/Large.
    static void BuildUrbanProps(GameObject room, RoomLayoutProfile layout)
    {
        var propsRoot = FindOrCreate(room.transform, "UrbanProps", Vector3.zero);

        var metalMat = CreateMaterial("PropMetal", new Color(0.32f, 0.33f, 0.35f), 0.45f);
        var woodMat = CreateMaterial("PropWood", new Color(0.45f, 0.32f, 0.2f), 0.12f);
        var plasticMat = CreateMaterial("PropPlastic", new Color(0.18f, 0.38f, 0.22f), 0.3f);

        var cornerX = -layout.roomHalfWidth * 0.72f;
        var cornerZ = -layout.roomHalfDepth * 0.72f;
        var otherCornerX = layout.roomHalfWidth * 0.72f;

        BuildPallet(propsRoot.transform, "Pallet", new Vector3(cornerX, 0f, cornerZ), woodMat);
        BuildCrateStack(propsRoot.transform, "Crates", new Vector3(cornerX + 0.55f, 0f, cornerZ - 0.15f), plasticMat);
        BuildPipes(propsRoot.transform, "Pipes", new Vector3(otherCornerX, layout.wallHeight * 0.78f, -layout.roomHalfDepth + 0.12f), metalMat);
        var binBase = new Vector3(otherCornerX - 0.35f, 0f, cornerZ + 0.35f);
        BuildTrashBin(propsRoot.transform, "TrashBin", binBase, metalMat);

        // Against the back wall's inner face (the wall is 20cm thick,
        // centred on -roomHalfDepth), standing on the floor.
        var doorMat = CreateMaterial("DoorPaint", new Color(0.13f, 0.27f, 0.27f), 0.35f, 0.3f);
        BuildMetalDoor(propsRoot.transform, "MetalDoor", new Vector3(0f, MetalDoorSize.y * 0.5f, -layout.roomHalfDepth + 0.1f + MetalDoorSize.z * 0.5f), doorMat, metalMat);
        BuildStreetPaper(propsRoot.transform, layout, binBase + new Vector3(0f, 0.38f, 0f), 0.15f);

        // Never moves after being placed, and is not touched by
        // AdaptiveRoomController - safe to batch, unlike the room shell,
        // which AdaptiveRoomController repositions every Recalibrate().
        MarkStaticRecursive(propsRoot.transform);

        log.AppendLine("UrbanProps: pallet, caixas, canos, lixeira e porta metalica (primitivas, sem assets externos), marcados como estaticos para batching.");
    }

    static void BuildPallet(Transform parent, string name, Vector3 basePos, Material mat)
    {
        var root = FindOrCreate(parent, name, basePos);
        root.transform.localPosition = basePos;
        MakeBox(root.transform, "Base", new Vector3(0f, 0.06f, 0f), new Vector3(0.9f, 0.12f, 0.9f), mat, true);
        for (var i = 0; i < 3; i++)
        {
            var z = -0.32f + i * 0.32f;
            MakeBox(root.transform, $"Slat_{i}", new Vector3(0f, 0.13f, z), new Vector3(0.9f, 0.03f, 0.12f), mat, false);
        }
    }

    static void BuildCrateStack(Transform parent, string name, Vector3 basePos, Material mat)
    {
        var root = FindOrCreate(parent, name, basePos);
        root.transform.localPosition = basePos;
        MakeBox(root.transform, "Crate_0", new Vector3(0f, 0.2f, 0f), new Vector3(0.4f, 0.4f, 0.4f), mat, true);
        MakeBox(root.transform, "Crate_1", new Vector3(0.15f, 0.55f, 0.08f), new Vector3(0.3f, 0.3f, 0.3f), mat, true);
    }

    static void BuildPipes(Transform parent, string name, Vector3 basePos, Material mat)
    {
        var root = FindOrCreate(parent, name, basePos);
        root.transform.localPosition = basePos;
        for (var i = 0; i < 3; i++)
        {
            var y = i * 0.16f;
            var pipe = MakeCylinder(root.transform, $"Pipe_{i}", new Vector3(0f, y, 0f), new Vector3(0.06f, 0.9f, 0.06f), mat, true);
            pipe.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        }
    }

    static void BuildTrashBin(Transform parent, string name, Vector3 basePos, Material mat)
    {
        MakeCylinder(parent, name, basePos + new Vector3(0f, 0.3f, 0f), new Vector3(0.3f, 0.3f, 0.3f), mat, true);
    }

    static readonly Vector3 MetalDoorSize = new Vector3(0.95f, 2.05f, 0.05f);

    // Until V0.9 the door was centred 2cm into the 20cm back wall - buried,
    // never seen - and its handle, a child of the scaled door, came out
    // 42cm tall and 2.5mm deep. The handle's numbers are now divided by the
    // door's size so it comes out at real size, on the room side (+Z).
    static void BuildMetalDoor(Transform parent, string name, Vector3 pos, Material doorMat, Material handleMat)
    {
        var door = MakeBox(parent, name, pos, MetalDoorSize, doorMat, false);
        var handle = new Vector3(0.03f, 0.14f, 0.03f);
        var offset = new Vector3(0.36f, 0f, MetalDoorSize.z * 0.5f + handle.z * 0.5f);
        MakeBox(door.transform, "Handle",
            new Vector3(offset.x / MetalDoorSize.x, offset.y / MetalDoorSize.y, offset.z / MetalDoorSize.z),
            new Vector3(handle.x / MetalDoorSize.x, handle.y / MetalDoorSize.y, handle.z / MetalDoorSize.z), handleMat, false);
    }

    static void MarkStaticRecursive(Transform root)
    {
        const StaticEditorFlags flags = StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic |
            StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
    }

    // ---------- V0.7 practical lighting ----------

    // One extra small point light, shadows off. The directional light +
    // Trilight ambient from V0.4's ConfigureLighting stay untouched - this is
    // additive, not a lighting rework, and keeps total realtime lights at 2.
    static void ConfigurePracticalLighting(GameObject room)
    {
        var lightingRoot = FindOrCreate(room.transform, "Lighting", Vector3.zero);
        var practical = FindOrCreate(lightingRoot.transform, "PracticalLight", new Vector3(0.9f, 2.1f, 0.6f));

        // "PracticalLight" can legitimately exist without a Light component
        // yet (a fresh GameObject just created by FindOrCreate, or a scene
        // left over from before this pass ever ran) - get-or-add explicitly,
        // and never assume the add succeeded.
        var light = practical.GetComponent<Light>();
        if (light == null)
            light = practical.AddComponent<Light>();

        if (light == null)
        {
            log.AppendLine("ERRO: nao foi possivel obter/criar o componente Light em 'PracticalLight' - iluminacao pratica NAO configurada.");
            return;
        }

        // Root cause of the "You probably need to add a Light..." fatal
        // error: URP lights require a UniversalAdditionalLightData companion
        // component (confirmed in the installed package source -
        // UniversalAdditionalLightData itself carries
        // [RequireComponent(typeof(Light))], but not the reverse). Adding a
        // Light through the Inspector's own "Add Component" menu attaches
        // this companion via the Light's Reset() callback - but Reset() does
        // NOT run for a component added via AddComponent<T>() from an
        // Editor script (the same Unity behaviour already documented on
        // CapManager.EnsureInitialized). Without it, URP's own lighting code
        // has nothing to read for this light and later Editor lighting
        // processing (on scene save) throws trying to process it - a
        // missing Light was never the actual fault. Uses URP's own official
        // GetUniversalAdditionalLightData() (get-or-create), not a manual
        // AddComponent guess.
        UnityEngine.Rendering.Universal.LightExtensions.GetUniversalAdditionalLightData(light);

        light.type = LightType.Point;
        light.color = new Color(1f, 0.82f, 0.6f);
        light.intensity = 1.4f;
        light.range = 3.5f;
        light.shadows = LightShadows.None;
        EditorUtility.SetDirty(light);

        log.AppendLine("Lighting: 1 point light pratica adicionada (sem sombras, alcance 3.5m; UniversalAdditionalLightData confirmada); directional + ambient da V0.4 preservados.");
    }

    // ---------- V0.7 ambience ----------

    static void BuildAmbience(GameObject room)
    {
        var ambienceGo = FindOrCreate(room.transform, "Ambience", Vector3.zero);
        var ambienceSource = ambienceGo.GetComponent<AudioSource>() ?? ambienceGo.AddComponent<AudioSource>();

        // The room's voices are created and played by EnvironmentAmbience; this one never plays by itself.
        ambienceSource.playOnAwake = false;
        ambienceSource.loop = false;
        EditorUtility.SetDirty(ambienceSource);

        var ambience = ambienceGo.GetComponent<EnvironmentAmbience>() ?? ambienceGo.AddComponent<EnvironmentAmbience>();
        EditorUtility.SetDirty(ambience);

        log.AppendLine("Ambience: eventos raros espacializados (rajadas, metal distante, trem, tubo), sem cama continua, gerados por codigo.");
    }

    // ---------- V0.8 interaction realism ----------

    // Protects the can BODY from clipping through the 3 paintable walls
    // while held, without limiting nozzle reach or touching the grab math
    // itself. Only wires the 3 walls the player is actually likely to press
    // the can up against - WallBack has no gameplay reason to be approached
    // closely and stays out of this list to keep the per-frame check cheap.
    static void ConfigureCanWallClearance(GameObject sprayCan, GameObject paintWall, GameObject wallLeft, GameObject wallRight)
    {
        var grab = sprayCan.GetComponent<VRGrabInteractable>();
        var clearance = sprayCan.GetComponent<CanWallClearance>() ?? sprayCan.AddComponent<CanWallClearance>();
        SetField(clearance, "sprayCan", sprayCan.transform);
        SetField(clearance, "grab", grab);

        // Middle of the real body, in the can's (now unscaled) local space.
        // The component's default (0, 0.35, 0) was authored against the old
        // 0.24 root scale and would sit above the can in meters.
        if (measuredCanBodyCenter.HasValue)
            SetVector3(clearance, "bodyLocalPoint", measuredCanBodyCenter.Value);

        var walls = new System.Collections.Generic.List<Collider>();
        foreach (var wallGo in new[] { paintWall, wallLeft, wallRight })
        {
            if (wallGo == null)
                continue;

            var collider = wallGo.GetComponent<Collider>();
            if (collider != null)
                walls.Add(collider);
        }

        SetArrayField(clearance, "walls", walls.ToArray());
        EditorUtility.SetDirty(clearance);

        log.AppendLine($"CanWallClearance conectado ({walls.Count} parede(s): MainWall/SideWall_Right/SideWall_Left) - protege o corpo da lata, mao nao e travada.");
    }

    // Widened from the class default (0.12m) - reported feel was that the
    // can did not reliably come to the hand. Explicitly set rather than
    // relying on the C# default, since a value already serialized into this
    // scene from an earlier version would otherwise silently win.
    static void ConfigureGrabbers(GameObject left, GameObject right)
    {
        foreach (var controller in new[] { left, right })
        {
            var grabber = controller.GetComponent<VRGrabber>();
            if (grabber == null)
                continue;

            SetFloat(grabber, "grabRadius", 0.15f);
        }

        log.AppendLine("VRGrabber.grabRadius definido para 0.15m em ambos os controllers (mais tolerante ao aproximar a mao da lata).");
    }

    // ---------- lighting ----------

    static void ConfigureLighting()
    {
        var light = Object.FindFirstObjectByType<Light>();
        if (light != null && light.type == LightType.Directional)
        {
            light.transform.rotation = Quaternion.Euler(45f, -25f, 0f);
            light.intensity = 1.15f;
            light.color = new Color(1f, 0.97f, 0.91f);

            // Real-time shadows are the single most expensive thing a
            // directional light does on a standalone headset, and the room is
            // a lit box - ambient carries the shape well enough without them.
            light.shadows = LightShadows.None;
            EditorUtility.SetDirty(light);
            log.AppendLine("Directional Light ajustada (45/-25, intensidade 1.15, sombras desligadas para Quest).");
        }

        // Flat-ish ambient so the can, hands and paint stay readable even on
        // faces pointing away from the light.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.46f, 0.48f, 0.52f);
        RenderSettings.ambientEquatorColor = new Color(0.36f, 0.36f, 0.38f);
        RenderSettings.ambientGroundColor = new Color(0.18f, 0.18f, 0.19f);
        log.AppendLine("Ambient light configurada (Trilight).");

        DisableScreenSpaceAmbientOcclusion();
    }

    // The V0.3 build log flagged this itself: "Using the Screen Space Ambient
    // Occlusion render feature results in significant performance overhead
    // when the application is running natively on device."
    static void DisableScreenSpaceAmbientOcclusion()
    {
        var guids = AssetDatabase.FindAssets("t:ScriptableRendererData");
        var disabled = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is not UnityEngine.Rendering.Universal.ScriptableRendererFeature feature)
                    continue;

                if (!feature.GetType().Name.Contains("ScreenSpaceAmbientOcclusion") || !feature.isActive)
                    continue;

                feature.SetActive(false);
                EditorUtility.SetDirty(feature);
                disabled++;
                log.AppendLine($"SSAO desativado em {path} (custo alto no Quest, sinalizado pelo proprio build da V0.3).");
            }
        }

        if (disabled == 0)
            log.AppendLine("SSAO: nenhuma feature ativa encontrada (ja estava desligado ou nao existe).");

        AssetDatabase.SaveAssets();
    }

    // ---------- 6/13/21 can ----------

    static SprayCanSfx ConfigureCan(GameObject sprayCan, InputActionAssetRef inputActions, GameObject left, GameObject right, RoomLayoutProfile layout)
    {
        // Resting on the tool table. Scale is normalised to 1: the root used
        // to carry a non-uniform (0.16, 0.24, 0.16) left over from the first
        // placeholder cube, which every child then had to compensate for -
        // and which would shear a cap parented under it the moment it
        // rotated. Everything below is authored in real meters.
        sprayCan.transform.SetPositionAndRotation(layout.CanRestPosition(Vector3.zero), Quaternion.identity);
        sprayCan.transform.localScale = Vector3.one;

        // Physics stays, because a can you can put down and pick back up feels
        // like an object, and the grab path already makes it kinematic while
        // held so there is no fighting between physics and the hand.
        // Interpolation removes the visible stutter of a physics body rendered
        // on a 72-90 Hz display, and continuous detection stops a dropped can
        // from tunnelling through the thin floor slab.
        var rigidbody = sprayCan.GetComponent<Rigidbody>();
        if (rigidbody != null)
        {
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // Low: a thrown can keeps spinning through the air; rolling
            // friction on the floor (PhysicsMaterial) brings it to rest.
            rigidbody.angularDamping = 0.2f;
            EditorUtility.SetDirty(rigidbody);
            log.AppendLine("Rigidbody da lata: interpolacao + deteccao continua (sem jitter, sem atravessar o chao).");
        }

        // Holds which physical cap is seated; CapSocket (built in
        // RebuildCanVisualFromRealMesh) is what fills it in at runtime.
        var capManager = GetOrAdd<CapManager>(sprayCan);
        EditorUtility.SetDirty(capManager);

        var sfxObject = sprayCan.transform.Find("Sfx");
        if (sfxObject == null)
        {
            sfxObject = new GameObject("Sfx").transform;
            sfxObject.SetParent(sprayCan.transform, false);
        }

        var sfx = sfxObject.GetComponent<SprayCanSfx>();
        if (sfx == null)
            sfx = sfxObject.gameObject.AddComponent<SprayCanSfx>();

        // GrabPoint's pose is derived from the real can geometry in
        // RebuildCanVisualFromRealMesh, together with SprayOrigin - the two
        // must share one forward, so they are set in one place.
        var grab = sprayCan.GetComponent<VRGrabInteractable>();

        // V0.4.1: the scene previously carried a guessed 45-degree grip/aim
        // compensation that was never validated on hardware and is the
        // suspected cause of the can appearing far below the hand and lying
        // horizontal. Zero is the value the V0.3 physical Quest 3 test
        // actually confirmed. Reset explicitly so re-running the builder
        // cannot silently reintroduce the old guess from a stale scene.
        if (grab != null)
        {
            SetVector3(grab, "attachRotationEuler", Vector3.zero);
            log.AppendLine("VRGrabInteractable.attachRotationEuler resetado para (0,0,0) - linha de base validada na V0.3, sem compensacao grip/aim nao testada.");
        }

        // Shake detection. Reads the can's own motion, so it only needs the
        // grab state and a place to play the rattle from.
        var shake = sprayCan.GetComponent<SprayCanShake>() ?? sprayCan.AddComponent<SprayCanShake>();
        SetField(shake, "grabInteractable", grab);

        // Shows the loaded color while holding the can, without looking at
        // the palette. Created here (SprayCanController needs its renderer);
        // RebuildCanVisualFromRealMesh then shapes it into a thin band
        // around the real can body. No collider - it would block the spray
        // raycast.
        var indicator = MakeBox(sprayCan.transform, "ColorIndicator",
            new Vector3(0f, 0.15f, 0f), new Vector3(0.03f, 0.03f, 0.03f),
            CreateMaterial("NozzleIndicator", Color.red, 0.5f), false);

        var emitter = sprayCan.GetComponent<SprayEmitter>();
        if (emitter != null)
        {
            SetField(emitter, "capManager", capManager);
            SetField(emitter, "shake", shake);
            // Scene still holds the V0.2 tick of 0.03s; with segment
            // interpolation a faster tick is what makes the line feel attached
            // to the hand rather than trailing it.
            SetFloat(emitter, "paintIntervalSeconds", 0.02f);
            SetField(emitter, "particleMaterialAsset", BuildSprayParticleMaterial());
        }

        var controller = sprayCan.GetComponent<SprayCanController>();
        if (controller != null)
        {
            SetField(controller, "sfx", sfx);
            SetField(controller, "grabInteractable", grab);

            // V0.8 fix: this used to also tint every MeshRenderer under
            // 'Visual' (visual.GetComponentsInChildren<MeshRenderer>(true)),
            // which recolored the whole imported can body - the reported bug
            // ("selecting blue repaints the entire can, not just the cap").
            // A real spray can's body keeps its printed label; only the cap
            // shows the loaded color. ColorIndicator already exists exactly
            // for this (a small collar positioned at the nozzle, see below) -
            // it just needed to be the ONLY renderer wired here.
            var tinted = new System.Collections.Generic.List<MeshRenderer>();
            var indicatorRenderer = indicator.GetComponent<MeshRenderer>();
            if (indicatorRenderer != null)
                tinted.Add(indicatorRenderer);

            SetArrayField(controller, "colorTintRenderers", tinted.ToArray());
            log.AppendLine($"SprayCanController: {tinted.Count} renderer(s) recebem a cor (somente o colar/indicador no bico - corpo da lata preserva o material original).");
        }

        // Kept wired but disabled: the grip pose is now derived (GrabPoint +
        // HandAnatomy), and cycling attach presets with the Y button would
        // silently tilt the can away from it mid-test. Enable by hand only
        // for a deliberate calibration session.
        var calibrator = GetOrAdd<GripCalibrator>(sprayCan);
        SetField(calibrator, "target", grab);
        SetField(calibrator, "inputActions", inputActions.Asset);
        calibrator.enabled = false;

        EditorUtility.SetDirty(sprayCan);
        log.AppendLine($"SprayCan: pos={sprayCan.transform.position}, CapManager + Sfx configurados; GripCalibrator presente mas DESATIVADO (pose da pegada e derivada, nao calibrada por botao).");
        return sfx;
    }

    // ---------- 12/13 racks ----------

    // Same ring placement AdaptiveRoomController.PlaceOnRing uses at runtime -
    // duplicated rather than shared across the Editor/runtime boundary, since
    // it is three lines and both call sites already read their numbers from
    // the same RoomLayoutProfile.
    static Vector3 RingPosition(float distance, float angleDegrees)
    {
        var angleRad = angleDegrees * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(angleRad) * distance, 0f, Mathf.Cos(angleRad) * distance);
    }

    static (Transform paletteRoot, Transform capRoot) BuildRacks(GameObject room, GameObject sprayCan, GameObject left, GameObject right, SprayCanSfx sfx, RoomLayoutProfile layout, CapBuildData capData)
    {
        var standMat = CreateMaterial("RackStand", new Color(0.16f, 0.17f, 0.18f), 0.45f, 0.6f);

        // Layout follows the room's own logic: paint wall ahead, the tool
        // table (can + caps) on the left, colors on the right, player in the
        // middle with nothing in front of them.
        var palettePos = RingPosition(layout.paletteDistance, layout.paletteAngleDegrees);
        var paletteRoot = FindOrCreate(room.transform, "ColorPalette", palettePos);
        // FindOrCreate only positions a brand-new object - set it explicitly
        // too, so re-running the builder with different layout numbers always
        // moves an already-existing rack instead of leaving it stale.
        paletteRoot.transform.localPosition = palettePos;
        paletteRoot.transform.localRotation = RingRotation(layout.paletteAngleDegrees);

        // V0.9: a physical color station - spray cans hanging on a
        // pegboard - replaces the stand of debug cubes. Same ColorPalette,
        // same slots-by-proximity interaction: each slot is now a can. One
        // slot past the palette is the cleaner. It rides the same
        // interaction and the same spray pipeline as a color.
        var eraserIndex = ColorManager.Count;
        var (swatches, paletteMarker) = BuildColorStation(paletteRoot.transform);

        var colorManager = sprayCan.GetComponent<ColorManager>();
        var canController = sprayCan.GetComponent<SprayCanController>();
        if (colorManager == null || canController == null)
        {
            Debug.LogError("[V04SceneBuilder] SprayCan is missing ColorManager and/or SprayCanController - the palette would select colors with nowhere to send them.", sprayCan);
            log.AppendLine("ERRO: ColorManager/SprayCanController ausentes na SprayCan.");
            return (paletteRoot.transform, null);
        }

        var palette = GetOrAdd<ColorPalette>(paletteRoot);
        SetArrayField(palette, "slots", swatches);
        SetField(palette, "leftHand", left.transform);
        SetField(palette, "rightHand", right.transform);
        SetField(palette, "colorManager", colorManager);
        SetField(palette, "canController", canController);
        SetField(palette, "sprayEmitter", sprayCan.GetComponent<SprayEmitter>());
        SetField(palette, "sfx", sfx);
        SetField(palette, "selectionMarker", paletteMarker);
        SetVector3(palette, "markerOffset", StationMarkerOffset);
        SetInt(palette, "eraserSlotIndex", eraserIndex);
        log.AppendLine($"ColorPalette: {ColorManager.Count} cores + 1 slot de limpeza, com marcador de selecao.");

        var capRoot = BuildCapRack(room, sfx, layout, capData, standMat);
        return (paletteRoot.transform, capRoot);
    }

    // ---------- V0.8.x physical cap rack ----------

    // One peg per cap type, all caps as real PhysicalCap objects. The
    // Standard cap starts seated on the can (RebuildCanVisualFromRealMesh),
    // so its peg starts empty - that is where the player parks it.
    //
    // Replaces the V0.4 rack of three floating cubes ("cube size = spray
    // size") that selected a cap by touch: that was a second way to change
    // the cap that could contradict the cap physically on the can.
    static Transform BuildCapRack(GameObject room, SprayCanSfx sfx, RoomLayoutProfile layout, CapBuildData capData, Material standMat)
    {
        var capRoot = FindOrCreate(room.transform, "CapRack", Vector3.zero).transform;
        capRoot.SetPositionAndRotation(layout.CapRackPosition(Vector3.zero), layout.CapRackRotation(Vector3.zero));
        capRoot.localScale = Vector3.one;

        // Everything under the rack is rebuilt: the old cubes/stand/marker,
        // and any pegs/caps from a previous run.
        for (var i = capRoot.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(capRoot.GetChild(i).gameObject);

        if (capData == null)
        {
            log.AppendLine("AVISO: CapRack sem caps - a malha do cap nao foi gerada (ver erro da lata acima).");
            return capRoot;
        }

        var defaults = SprayCap.CreateDefaults();
        const float spacing = 0.07f;
        const float boardHeight = 0.018f;
        var width = spacing * defaults.Length + 0.02f;

        MakeBox(capRoot, "Board", new Vector3(0f, boardHeight * 0.5f, 0f), new Vector3(width, boardHeight, 0.07f), standMat, true);
        DressCapTray(capRoot, width, 0.07f, boardHeight);
        var pegMaterial = CreateMaterial("CapRackPeg", new Color(0.6f, 0.6f, 0.62f), 0.5f);

        var slots = new Transform[defaults.Length];
        var standardHome = -1;
        var parked = 0;

        for (var i = 0; i < defaults.Length; i++)
        {
            var x = (i - (defaults.Length - 1) * 0.5f) * spacing;

            // A short pin, like the valve stem a cap sits on: visible when the
            // peg is empty, hidden inside the cap when it is not.
            var peg = MakeCylinder(capRoot, $"Peg_{defaults[i].displayName}", new Vector3(x, boardHeight + 0.004f, 0f), new Vector3(0.004f, 0.004f, 0.004f), pegMaterial);
            peg.transform.localRotation = Quaternion.identity;

            var slot = new GameObject($"Slot_{defaults[i].displayName}").transform;
            slot.SetParent(capRoot, false);
            slot.localPosition = new Vector3(x, boardHeight, 0f);
            slots[i] = slot;

            if (defaults[i].displayName == "Standard")
            {
                standardHome = i;
                continue;
            }

            var variant = CapVariantFor(defaults[i].displayName);
            var cap = CreatePhysicalCap($"Cap_{defaults[i].displayName}", defaults[i], capData,
                CreateMaterial($"Cap_{defaults[i].displayName}", variant.tint, 0.35f), variant.scale, placeholder: true);
            cap.transform.SetParent(slot, false);
            parked++;
        }

        var rack = GetOrAdd<CapRack>(capRoot.gameObject);
        SetArrayField(rack, "slots", slots);
        SetField(rack, "sfx", sfx);
        EditorUtility.SetDirty(rack);

        // Home slots, so a cap that falls out of the room comes back to its
        // own peg.
        for (var i = 0; i < slots.Length; i++)
        {
            var cap = i == standardHome ? capData.StandardCap : slots[i].GetComponentInChildren<PhysicalCap>(true);
            if (cap == null)
                continue;

            SetField(cap, "homeRack", rack);
            SetInt(cap, "homeSlot", i);
        }

        log.AppendLine($"CapRack fisico: {slots.Length} pinos na mesa lateral, {parked} cap(s) estacionado(s) + Standard instalado na lata (pino '{(standardHome >= 0 ? defaults[standardHome].displayName : "-")}' livre para ele).");
        return capRoot;
    }

    // Visual identity per cap type. Only Standard uses the real actuator
    // model as-is; the others are the SAME mesh, tinted (graffiti caps are
    // colour-coded in real life too) and, for Skinny, slightly smaller -
    // placeholders flagged on the object until real Skinny/Fat models exist.
    static (Color tint, float scale) CapVariantFor(string capName) => capName switch
    {
        "Skinny" => (new Color(0.93f, 0.42f, 0.6f), 0.85f),
        "Fat" => (new Color(0.12f, 0.12f, 0.13f), 1f),
        _ => (new Color(0.85f, 0.85f, 0.88f), 1f),
    };

    static PhysicalCap CreatePhysicalCap(string name, SprayCap spray, CapBuildData capData, Material material, float scale, bool placeholder)
    {
        var go = new GameObject(name);
        go.transform.localScale = Vector3.one * scale;

        go.AddComponent<MeshFilter>().sharedMesh = capData.CapMesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Kinematic trigger, not interpolated, until it is dropped loose
        // (PhysicalCap owns the switch). Speculative CCD is the mode valid for
        // both kinematic and dynamic bodies, and is enough to keep a 2cm cap
        // from tunnelling through the 10cm floor slab when dropped.
        var body = go.AddComponent<Rigidbody>();
        body.mass = 0.01f;
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.None;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        // Auto-fits the cap mesh's bounds.
        var collider = go.AddComponent<BoxCollider>();
        collider.isTrigger = true;

        var grab = go.AddComponent<VRGrabInteractable>();
        SetField(grab, "body", body);
        SetBool(grab, "keepWorldPoseOnGrab", true);

        var orifice = new GameObject("Orifice").transform;
        orifice.SetParent(go.transform, false);
        orifice.localPosition = capData.OrificeLocal;
        orifice.localRotation = Quaternion.identity;

        // Where the index finger presses: on the scoop cut into the top, on
        // the side facing away from the orifice (toward the hand).
        var fingerPad = new GameObject("FingerPad").transform;
        fingerPad.SetParent(go.transform, false);
        fingerPad.localPosition = capData.FingerPadLocal;
        fingerPad.localRotation = Quaternion.identity;

        var cap = go.AddComponent<PhysicalCap>();
        var so = new SerializedObject(cap);
        so.FindProperty("spray").boxedValue = spray;
        so.FindProperty("orifice").objectReferenceValue = orifice;
        so.FindProperty("fingerPad").objectReferenceValue = fingerPad;
        so.FindProperty("capCollider").objectReferenceValue = collider;
        so.FindProperty("usesPlaceholderModel").boolValue = placeholder;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(go);
        return cap;
    }

    static GameObject MakeCylinder(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
    {
        var cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cylinder.name = name;
        cylinder.transform.SetParent(parent, false);
        cylinder.transform.localPosition = localPosition;
        cylinder.transform.localScale = localScale;
        Object.DestroyImmediate(cylinder.GetComponent<Collider>());
        cylinder.GetComponent<MeshRenderer>().sharedMaterial = material;
        return cylinder;
    }

    // TryGetComponent, not "GetComponent<T>() ?? AddComponent<T>()": in the
    // Editor a missing component comes back as Unity's fake-null
    // placeholder, which C#'s ?? treats as present.
    static T GetOrAdd<T>(GameObject go) where T : Component =>
        go.TryGetComponent<T>(out var existing) ? existing : go.AddComponent<T>();

    static void BuildUndoControl(GameObject room, InputActionAssetRef inputActions, SprayCanSfx sfx)
    {
        var holder = FindOrCreate(room.transform, "PaintControls", Vector3.zero);
        var undo = holder.GetComponent<UndoControl>() ?? holder.AddComponent<UndoControl>();
        SetField(undo, "inputActions", inputActions.Asset);
        SetField(undo, "sfx", sfx);
        log.AppendLine("UndoControl criado (botao B / Right Secondary Button desfaz o ultimo traco).");
    }

    // ---------- V0.5 adaptive room ----------

    // Wires (never duplicates) AdaptiveRoomController onto the same Room
    // object the rest of this file builds. All the Transform references it
    // needs already exist as children of `room` or as top-level objects found
    // earlier in Build() - this method only connects them.
    static void ConfigureAdaptiveRoom(GameObject room, GameObject sprayCan, GameObject paintWall, GameObject xrOrigin, (Transform paletteRoot, Transform capRoot) racks)
    {
        var controller = room.GetComponent<AdaptiveRoomController>() ?? room.AddComponent<AdaptiveRoomController>();

        SetField(controller, "floor", room.transform.Find("Floor"));
        SetField(controller, "ceiling", room.transform.Find("Ceiling"));
        SetField(controller, "wallLeft", room.transform.Find("WallLeft"));
        SetField(controller, "wallRight", room.transform.Find("WallRight"));
        SetField(controller, "wallBack", room.transform.Find("WallBack"));
        SetField(controller, "paintWall", paintWall.transform);
        SetField(controller, "toolTable", room.transform.Find("ToolTable"));
        SetField(controller, "sprayCan", sprayCan.transform);
        SetField(controller, "sprayCanGrab", sprayCan.GetComponent<VRGrabInteractable>());
        SetField(controller, "colorPaletteRoot", racks.paletteRoot);
        SetField(controller, "capRackRoot", racks.capRoot);

        if (xrOrigin != null)
            SetField(controller, "playerOrigin", xrOrigin.transform);

        EditorUtility.SetDirty(controller);
        log.AppendLine("AdaptiveRoomController conectado (fallback=Compact; recalibra no Start via boundary do XRInputSubsystem, ou usa o fallback se indisponivel).");
    }

    // ---------- V0.6 physical interaction ----------

    // Whole-hand pose blending (Open/GrabCan/Spray) - see HandPoseController's
    // own doc comment for why this cannot be a per-finger pose: the imported
    // hand mesh (HandMeshCleanup) is a single fused mesh with no rig.
    static void ConfigureHandPoses(GameObject left, GameObject right, GameObject sprayCan)
    {
        var grab = sprayCan.GetComponent<VRGrabInteractable>();
        var emitter = sprayCan.GetComponent<SprayEmitter>();

        ConfigureHandPose(left, true, grab, emitter);
        ConfigureHandPose(right, false, grab, emitter);
    }

    static void ConfigureHandPose(GameObject controller, bool isLeft, VRGrabInteractable grab, SprayEmitter emitter)
    {
        var hand = controller.transform.Find("HandVisual/Hand");
        if (hand == null)
        {
            log.AppendLine($"AVISO: HandPoseController nao configurado em {controller.name} - 'HandVisual/Hand' nao encontrado (rode a reconstrucao de maos primeiro).");
            return;
        }

        var pose = hand.GetComponent<HandPoseController>() ?? hand.gameObject.AddComponent<HandPoseController>();
        SetField(pose, "poseTarget", hand);
        SetBool(pose, "isLeftHand", isLeft);
        SetField(pose, "canGrab", grab);
        SetField(pose, "sprayEmitter", emitter);
        EditorUtility.SetDirty(pose);
        log.AppendLine($"HandPoseController conectado em {controller.name}/HandVisual/Hand (isLeftHand={isLeft}).");
    }

    // Presses CapSocket/NozzlePivot with the trigger. The pivot carries the
    // seated cap and SprayOrigin, so both follow the press by construction.
    static void ConfigureNozzleAnimator(GameObject sprayCan, SprayCanSfx sfx)
    {
        var pivot = sprayCan.transform.Find("CapSocket/NozzlePivot");
        var emitter = sprayCan.GetComponent<SprayEmitter>();
        if (pivot == null || emitter == null)
        {
            log.AppendLine("ERRO: NozzleAnimator nao configurado - CapSocket/NozzlePivot ou SprayEmitter ausente na SprayCan.");
            return;
        }

        var animator = GetOrAdd<NozzleAnimator>(sprayCan);
        SetField(animator, "nozzlePivot", pivot);
        SetField(animator, "sprayEmitter", emitter);
        SetField(animator, "capManager", sprayCan.GetComponent<CapManager>());
        SetField(animator, "sfx", sfx);
        SetField(animator, "canGrab", sprayCan.GetComponent<VRGrabInteractable>());
        EditorUtility.SetDirty(animator);

        log.AppendLine("NozzleAnimator conectado a CapSocket/NozzlePivot (curso em metros, proporcional ao trigger; parado sem cap). Cap instalado e SprayOrigin sao filhos do pivot e descem juntos.");
    }

    static void ConfigureShakeVisualFeedback(GameObject sprayCan)
    {
        var visual = sprayCan.transform.Find("Visual");
        var shake = sprayCan.GetComponent<SprayCanShake>();
        if (visual == null || shake == null)
        {
            log.AppendLine("AVISO: ShakeVisualFeedback nao configurado - Visual ou SprayCanShake ausente.");
            return;
        }

        var feedback = visual.GetComponent<ShakeVisualFeedback>() ?? visual.gameObject.AddComponent<ShakeVisualFeedback>();
        SetField(feedback, "visualRoot", visual);
        SetField(feedback, "shake", shake);
        EditorUtility.SetDirty(feedback);
        log.AppendLine("ShakeVisualFeedback conectado ao 'Visual' (jitter cosmetico; SprayCan root e GrabPoint intocados).");
    }

    // ---------- validation ----------

    // Runs the checklist against the scene the builder just produced, so a
    // single menu click reports whether V0.4 is actually wired rather than
    // just "no exception was thrown".
    static int ValidateScene()
    {
        var problems = 0;

        void Check(bool condition, string label, string detail)
        {
            if (condition)
            {
                log.AppendLine($"  OK    {label}: {detail}");
            }
            else
            {
                log.AppendLine($"  FALHA {label}: {detail}");
                problems++;
            }
        }

        // V0.7: at least 3 paintable surfaces, each with a MeshCollider -
        // RaycastHit.textureCoord is empty for primitive colliders.
        var surfaces = Object.FindObjectsByType<PaintSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Check(surfaces.Length >= 3, "Superficies de pintura", $"{surfaces.Length} encontrada(s), esperado >=3");

        var surfaceIds = new System.Collections.Generic.HashSet<string>();
        foreach (var surf in surfaces)
        {
            var collider = surf.GetComponent<MeshCollider>();
            Check(collider != null && collider.sharedMesh != null, $"{surf.SurfaceId} MeshCollider",
                collider == null ? "ausente" : (collider.sharedMesh == null ? "sem mesh" : "presente com mesh"));

            var isUniqueId = surfaceIds.Add(surf.SurfaceId);
            Check(isUniqueId, $"{surf.SurfaceId} id unico", isUniqueId ? "unico" : "duplicado");
        }

        // One hand per controller, exactly one renderer each.
        foreach (var controllerName in new[] { "Left Controller", "Right Controller" })
        {
            var controller = GameObject.Find(controllerName);
            if (controller == null)
            {
                Check(false, controllerName, "nao encontrado");
                continue;
            }

            var placeholder = controller.GetComponent<MeshRenderer>();
            Check(placeholder == null || !placeholder.enabled, $"{controllerName} placeholder",
                placeholder == null ? "sem MeshRenderer" : (placeholder.enabled ? "AINDA VISIVEL" : "desativado"));

            var visual = controller.transform.Find("HandVisual");
            if (visual == null)
            {
                Check(false, $"{controllerName} HandVisual", "ausente");
                continue;
            }

            var renderers = visual.GetComponentsInChildren<MeshRenderer>(true);
            var filters = visual.GetComponentsInChildren<MeshFilter>(true);
            var tris = filters.Length == 1 && filters[0].sharedMesh != null ? filters[0].sharedMesh.triangles.Length / 3 : 0;
            Check(renderers.Length == 1 && filters.Length == 1 && tris > 0,
                $"{controllerName} mao",
                $"{renderers.Length} renderer(s), {filters.Length} mesh(es), {tris} tris (esperado 1/1/>0)");
        }

        // Spray chain.
        var sprayCan = GameObject.Find("SprayCan");
        if (sprayCan == null)
        {
            Check(false, "SprayCan", "nao encontrada");
            return problems;
        }

        var emitter = sprayCan.GetComponent<SprayEmitter>();
        Check(emitter != null, "SprayEmitter", emitter != null ? "presente" : "ausente");

        var sprayOrigin = sprayCan.transform.Find("CapSocket/NozzlePivot/SprayOrigin");
        Check(sprayOrigin != null, "SprayOrigin", sprayOrigin != null ? "presente" : "ausente");

        if (sprayOrigin != null && emitter != null)
        {
            // SprayEmitter.SetUpParticles() only runs in Awake(), which does
            // not execute in Edit Mode - so there is no ParticleSystem
            // component to find here even when everything is wired correctly.
            // What IS verifiable without Play Mode, and what actually
            // guarantees particles/raycast agree, is that sprayParticlesRoot
            // (read from the serialized field, the same value Awake reads)
            // is sprayOrigin or a descendant of it.
            var so = new SerializedObject(emitter);
            var rootProp = so.FindProperty("sprayParticlesRoot");
            var particlesRoot = rootProp != null ? rootProp.objectReferenceValue as Transform : null;

            var wired = particlesRoot != null && (particlesRoot == sprayOrigin || particlesRoot.IsChildOf(sprayOrigin));
            Check(wired, "sprayParticlesRoot sob SprayOrigin",
                particlesRoot == null ? "campo nao atribuido" : $"'{GetPath(particlesRoot)}' -> {(wired ? "e descendente de SprayOrigin (direcao unica garantida)" : "NAO e descendente de SprayOrigin")}");
        }

        Check(sprayCan.GetComponent<CapManager>() != null, "CapManager", "na lata");
        Check(sprayCan.GetComponent<SprayCanShake>() != null, "SprayCanShake", "na lata");
        Check(sprayCan.GetComponentInChildren<SprayCanSfx>(true) != null, "SprayCanSfx", "na lata");
        Check(sprayCan.GetComponent<VRGrabInteractable>() != null, "VRGrabInteractable", "na lata");
        Check(sprayCan.transform.Find("GrabPoint") != null, "GrabPoint", "na lata");
        Check(sprayCan.transform.Find("ColorIndicator") != null, "ColorIndicator", "no bico");

        // Racks.
        var palette = Object.FindFirstObjectByType<ColorPalette>();
        Check(palette != null, "ColorPalette", palette != null ? "presente" : "ausente");
        var rack = Object.FindFirstObjectByType<CapRack>();
        Check(rack != null, "CapRack", rack != null ? "presente" : "ausente");
        var undo = Object.FindFirstObjectByType<UndoControl>();
        Check(undo != null, "UndoControl", undo != null ? "presente" : "ausente");

        // Missing scripts anywhere in the scene show up as null components.
        var missing = 0;
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    missing++;
                    log.AppendLine($"        Missing Script em '{GetPath(go.transform)}'");
                }
            }
        }

        Check(missing == 0, "Missing Scripts", $"{missing} encontrado(s)");

        // Duplicates of things the builder creates.
        foreach (var uniqueName in new[] { "Room", "ColorPalette", "CapRack", "PaintWall", "SprayCan" })
        {
            var count = 0;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go.name == uniqueName)
                    count++;
            }

            Check(count == 1, $"Unicidade de '{uniqueName}'", $"{count} na cena");
        }

        // V0.5 adaptive room.
        var room = GameObject.Find("Room");
        var adaptiveController = room != null ? room.GetComponent<AdaptiveRoomController>() : null;
        Check(adaptiveController != null, "AdaptiveRoomController", adaptiveController != null ? "presente na Room" : "ausente");

        if (adaptiveController != null)
        {
            var so = new SerializedObject(adaptiveController);
            foreach (var fieldName in new[] { "floor", "ceiling", "wallLeft", "wallRight", "wallBack", "paintWall", "toolTable", "sprayCan", "colorPaletteRoot", "capRackRoot" })
            {
                var prop = so.FindProperty(fieldName);
                var ok = prop != null && prop.objectReferenceValue != null;
                Check(ok, $"AdaptiveRoomController.{fieldName}", ok ? "referenciado" : "NULO");
            }
        }

        // V0.6 physical interaction pass.
        foreach (var controllerName in new[] { "Left Controller", "Right Controller" })
        {
            var controller = GameObject.Find(controllerName);
            var hand = controller != null ? controller.transform.Find("HandVisual/Hand") : null;
            var pose = hand != null ? hand.GetComponent<HandPoseController>() : null;
            Check(pose != null, $"{controllerName} HandPoseController", pose != null ? "presente" : "ausente");
        }

        Check(sprayCan.GetComponent<NozzleAnimator>() != null, "NozzleAnimator", "na lata");
        Check(sprayCan.transform.Find("Visual")?.GetComponent<ShakeVisualFeedback>() != null, "ShakeVisualFeedback", "no Visual");

        var wallClearance = sprayCan.GetComponent<CanWallClearance>();
        Check(wallClearance != null, "CanWallClearance", "na lata");
        if (wallClearance != null)
        {
            var so = new SerializedObject(wallClearance);
            var wallsProp = so.FindProperty("walls");
            var wallCount = wallsProp != null ? wallsProp.arraySize : 0;
            Check(wallCount >= 3, "CanWallClearance.walls", $"{wallCount} parede(s) conectada(s), esperado >=3");
        }

        // V0.10: the Color Orb replaced the HSV panel (VRColorPicker).
        var orb = Object.FindAnyObjectByType<ColorOrb>(FindObjectsInactive.Include);
        Check(orb != null, "ColorOrb", orb != null ? "presente" : "ausente");
        if (orb != null)
        {
            var so = new SerializedObject(orb);
            foreach (var fieldName in new[] { "settings", "visual", "inputActions", "leftController", "rightController", "leftGrabber", "rightGrabber", "handRig", "head", "colorManager", "canController", "sprayEmitter", "canGrab" })
            {
                var prop = so.FindProperty(fieldName);
                var ok = prop != null && prop.objectReferenceValue != null;
                Check(ok, $"ColorOrb.{fieldName}", ok ? "referenciado" : "NULO");
            }

            var visualSo = new SerializedObject(orb.GetComponent<ColorOrbVisual>());
            var beadsProp = visualSo.FindProperty("beads");
            Check(beadsProp != null && beadsProp.arraySize == ColorManager.Count + 1, "ColorOrb: contas", $"{(beadsProp != null ? beadsProp.arraySize : 0)} (6 cores + cleaner)");
            var coreRenderer = visualSo.FindProperty("core")?.objectReferenceValue as Renderer;
            Check(coreRenderer != null && coreRenderer.sharedMaterial != null && coreRenderer.sharedMaterial.shader.name == "Freehand/ColorOrb" && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(coreRenderer.sharedMaterial)),
                "ColorOrb: material persistente do nucleo", coreRenderer != null && coreRenderer.sharedMaterial != null ? AssetDatabase.GetAssetPath(coreRenderer.sharedMaterial) : "ausente");
            var colliders = orb.GetComponentsInChildren<Collider>(true).Length;
            Check(colliders == 0, "ColorOrb: sem colliders", $"{colliders} (mira analitica: nao bloqueia spray, grab nem fisica)");
        }

        Check(GameObject.Find("ColorPicker") == null, "Painel HSV antigo removido", "substituido pela Color Orb");

        var inputActionsCheck = FindInputActions();
        var rightPrimaryOk = inputActionsCheck?.Asset.FindActionMap("XR", false)?.FindAction("Right Primary Button", false) != null;
        Check(rightPrimaryOk, "Action 'Right Primary Button'", rightPrimaryOk ? "presente no asset XR" : "AUSENTE - a Color Orb nao abre na mao direita");
        var leftPrimaryOk = inputActionsCheck?.Asset.FindActionMap("XR", false)?.FindAction("Left Primary Button", false) != null;
        Check(leftPrimaryOk, "Action 'Left Primary Button'", leftPrimaryOk ? "presente no asset XR" : "AUSENTE - a Color Orb nao abre na mao esquerda");

        // No second color/paint system: ColorManager must still resolve to
        // exactly one current color regardless of source.
        var colorManagerCheck = sprayCan.GetComponent<ColorManager>();
        Check(colorManagerCheck != null, "ColorManager", colorManagerCheck != null ? "presente (custom color via SelectCustom, mesma classe)" : "ausente");

        // V0.7 environment pass.
        var audioManager = Object.FindFirstObjectByType<Freehand.Audio.AudioManager>(FindObjectsInactive.Include);
        Check(audioManager != null, "AudioManager", audioManager != null ? "presente" : "ausente");

        var ambience = Object.FindFirstObjectByType<Freehand.Audio.EnvironmentAmbience>(FindObjectsInactive.Include);
        Check(ambience != null, "EnvironmentAmbience", ambience != null ? "presente" : "ausente");

        // Sound: nothing may start playing by itself or loop in the scene - the room's
        // sound is event-based, and every loop is started and stopped by code that owns it.
        var strayAudio = 0;
        foreach (var source in Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include))
        {
            if (source.playOnAwake || source.loop)
                strayAudio++;
        }

        Check(strayAudio == 0, "AudioSources da cena", strayAudio == 0 ? "nenhuma com Play On Awake ou loop (nada toca sozinho)" : $"{strayAudio} com Play On Awake/loop - possivel som constante");
        Check(sprayCan.GetComponent<Freehand.Audio.CanMotionAudio>() != null && sprayCan.GetComponent<Freehand.Audio.ImpactAudio>() != null, "Audio da lata", "chacoalhada (CanMotionAudio) e impacto (ImpactAudio)");
        Check(Object.FindFirstObjectByType<Freehand.Audio.SprayCanSfx>(FindObjectsInactive.Include) != null, "Pool de vozes (SprayCanSfx)", "presente");

        // V0.11: every holdable item that has a presentation, and the one input reading it.
        var inspectionInput = Object.FindAnyObjectByType<Freehand.Interaction.InspectionInput>(FindObjectsInactive.Include);
        var capsWithoutInspection = 0;
        foreach (var physicalCap in Object.FindObjectsByType<PhysicalCap>(FindObjectsInactive.Include))
        {
            if (physicalCap.GetComponent<Freehand.Interaction.ItemInspection>() == null)
                capsWithoutInspection++;
        }

        Check(sprayCan.GetComponent<Freehand.Interaction.ItemInspection>() != null && capsWithoutInspection == 0, "Inspecao (V0.11)", capsWithoutInspection == 0 ? "lata e caps com ItemInspection" : $"{capsWithoutInspection} cap(s) sem ItemInspection");
        Check(inspectionInput != null, "InspectionInput", inspectionInput != null ? "presente (A/X da mao que segura)" : "ausente - nada pode ser inspecionado");

        // V0.14: every can has a freestyle wired to both hands.
        var freestyleMissing = 0;
        foreach (var canGrab in Object.FindObjectsByType<VRGrabInteractable>(FindObjectsInactive.Include))
        {
            if (canGrab.GetComponent<Freehand.Spray.SprayEmitter>() == null)
                continue;
            var freestyle = canGrab.GetComponent<Freehand.Interaction.CanFreestyle>();
            var so = freestyle != null ? new SerializedObject(freestyle) : null;
            if (so == null || so.FindProperty("leftGrabber").objectReferenceValue == null || so.FindProperty("rightGrabber").objectReferenceValue == null)
                freestyleMissing++;
        }

        Check(freestyleMissing == 0, "Freestyle (V0.14)", freestyleMissing == 0 ? "toda lata com CanFreestyle ligado as duas maos" : $"{freestyleMissing} lata(s) sem freestyle ou sem maos"); 

        var propsRoot = room != null ? room.transform.Find("UrbanProps") : null;
        Check(propsRoot != null && propsRoot.childCount >= 4, "UrbanProps",
            propsRoot == null ? "ausente" : $"{propsRoot.childCount} prop(s)");

        var lightingRoot = room != null ? room.transform.Find("Lighting") : null;
        var practicalLight = lightingRoot != null ? lightingRoot.GetComponentInChildren<Light>() : null;
        Check(practicalLight != null, "Practical Light", practicalLight != null ? "presente" : "ausente");

        var totalRealtimeLights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        Check(totalRealtimeLights <= 3, "Contagem de realtime lights", $"{totalRealtimeLights} luz(es) na cena (orcamento Quest: manter baixo)");

        // Informational, not pass/fail: Personal-tier Unity may force the
        // Unity splash back on regardless of what V07SplashScreenConfig set -
        // the log records the real value instead of assuming it stuck.
        log.AppendLine($"  INFO  Made With Unity splash: show={PlayerSettings.SplashScreen.show} showUnityLogo={PlayerSettings.SplashScreen.showUnityLogo} (rode 'Freehand/V0.7/1 - Disable Made With Unity Splash' se ainda nao rodou)");

        // V0.8 part 2: real rigged hands - HandRig/HandBonePoseController
        // wiring, the bone chains, left/right and the hand anatomy measured
        // from the bones. Shared with the standalone 'Validate Hand Rig' menu.
        foreach (var result in HandRigValidator.Validate(GameObject.Find("Hands")))
        {
            if (result.IsInfo)
                log.AppendLine($"  INFO  {result.Label}: {result.Detail}");
            else
                Check(result.Ok, result.Label, result.Detail);
        }

        foreach (var controllerName in new[] { "Left Controller", "Right Controller" })
        {
            var controller = GameObject.Find(controllerName);
            var legacyVisual = controller != null ? controller.transform.Find("HandVisual") : null;
            Check(legacyVisual == null || !legacyVisual.gameObject.activeSelf, $"{controllerName} HandVisual antiga",
                legacyVisual == null ? "ausente" : (legacyVisual.gameObject.activeSelf ? "AINDA VISIVEL (deveria estar desativada pelo rig novo)" : "desativada (correto, mao real vem de Hands)"));
        }

        var canVisual = sprayCan.transform.Find("Visual/SM_Spraybottle");
        Check(canVisual != null, "Lata: visual real (SM_Spraybottle)", canVisual != null ? "presente" : "ausente (prefab antigo pode ainda estar em uso - ver AVISO acima)");

        // V0.8.x physical caps + spray origin geometry - shared with the
        // standalone 'Validate Caps + Spray Origin' menu, one set of checks.
        foreach (var result in CapSystemValidator.Validate(sprayCan))
        {
            if (result.IsInfo)
                log.AppendLine($"  INFO  {result.Label}: {result.Detail}");
            else
                Check(result.Ok, result.Label, result.Detail);
        }

        var legacyCanVisual = sprayCan.GetComponent<SprayCanVisual>();
        Check(legacyCanVisual == null, "Lata: SprayCanVisual (placeholder) removido", legacyCanVisual == null ? "removido" : "AINDA PRESENTE - vai sobrescrever o mesh real com um cilindro no Awake");

        // V0.9: can physics, finger wiring, drip readiness, color station.
        ValidateV09(Check);
        ValidateV013(Check);

        return problems;
    }

    // ---------- helpers ----------

    class InputActionAssetRef
    {
        public UnityEngine.InputSystem.InputActionAsset Asset;
    }

    static InputActionAssetRef FindInputActions()
    {
        var guids = AssetDatabase.FindAssets("t:InputActionAsset");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.Contains("InputSystem_Actions"))
                continue;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(path);
            if (asset != null)
                return new InputActionAssetRef { Asset = asset };
        }

        return null;
    }

    static GameObject FindOrCreate(Transform parent, string name, Vector3 localPosition)
    {
        var existing = parent.Find(name);
        if (existing != null)
            return existing.gameObject;

        var created = new GameObject(name);
        created.transform.SetParent(parent, false);
        created.transform.localPosition = localPosition;
        return created;
    }

    static GameObject MakeBox(Transform parent, string name, Vector3 localPosition, Vector3 size, Material material, bool keepCollider)
    {
        var existing = parent.Find(name);
        GameObject box;

        if (existing != null)
        {
            box = existing.gameObject;
        }
        else
        {
            box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
        }

        box.transform.localPosition = localPosition;
        box.transform.localRotation = Quaternion.identity;
        box.transform.localScale = size;

        var collider = box.GetComponent<Collider>();
        if (collider != null && !keepCollider)
            Object.DestroyImmediate(collider);

        var renderer = box.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.sharedMaterial = material;

        EditorUtility.SetDirty(box);
        return box;
    }

    // Quad primitives ship with a MeshCollider by default - stripped here
    // unconditionally, since every quad this builder makes either gets its
    // own BoxCollider added at runtime once its texture exists (the old picker
    // 's hue bar / SV square) or needs no collider at all (cursors, preview).
    static GameObject MakeQuad(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
    {
        var existing = parent.Find(name);
        GameObject quad;

        if (existing != null)
        {
            quad = existing.gameObject;
        }
        else
        {
            quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            quad.transform.SetParent(parent, false);
        }

        quad.transform.localPosition = localPosition;
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale = localScale;

        var collider = quad.GetComponent<Collider>();
        if (collider != null)
            Object.DestroyImmediate(collider);

        var renderer = quad.GetComponent<MeshRenderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;

        EditorUtility.SetDirty(quad);
        return quad;
    }

    // Cylinder primitives ship with a CapsuleCollider by default (Unity has
    // no dedicated cylinder collider type) - kept as-is when keepCollider is
    // true, which is a perfectly adequate "simple collider" for a decorative
    // prop (pipe/bin), matching the brief's "colliders simples" instruction.
    static GameObject MakeCylinder(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material, bool keepCollider)
    {
        var existing = parent.Find(name);
        GameObject cyl;

        if (existing != null)
        {
            cyl = existing.gameObject;
        }
        else
        {
            cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cyl.name = name;
            cyl.transform.SetParent(parent, false);
        }

        cyl.transform.localPosition = localPosition;
        cyl.transform.localRotation = Quaternion.identity;
        cyl.transform.localScale = localScale;

        var collider = cyl.GetComponent<Collider>();
        if (collider != null && !keepCollider)
            Object.DestroyImmediate(collider);

        var renderer = cyl.GetComponent<MeshRenderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;

        EditorUtility.SetDirty(cyl);
        return cyl;
    }

    static void EnsureFolder(string parent, string child)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + child))
            AssetDatabase.CreateFolder(parent, child);
    }

    static Material CreateMaterial(string name, Color color, float smoothness, float metallic = 0f)
    {
        var path = $"{RoomMaterialsFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[V04SceneBuilder] URP/Lit shader not found.");
                return null;
            }

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Metallic", metallic);
        EditorUtility.SetDirty(material);
        return material;
    }

    // Unlit and double-sided: the color picker's gradients must read as true
    // color regardless of the room's directional light (a Lit material would
    // shade/darken the hue and value readouts, defeating the point of a
    // value slider), and double-sided means the panel is visible whichever
    // way its ring placement ends up facing without having to guess the
    // GLB-import-style axis convention.
    static Material CreateUnlitMaterial(string name, Color color)
    {
        var path = $"{RoomMaterialsFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogError("[V04SceneBuilder] URP/Unlit shader not found.");
                return null;
            }

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.SetColor("_BaseColor", color);
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void SetField(Object target, string fieldName, Object value)
    {
        if (target == null)
            return;

        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetInt(Object target, string fieldName, int value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.intValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetBool(Object target, string fieldName, bool value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.boolValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetString(Object target, string fieldName, string value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.stringValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetColorField(Object target, string fieldName, Color value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.colorValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Uses intValue (the underlying stored integer) rather than
    // enumValueIndex (the index into the enum's declared name list) -
    // identical for a plain sequential enum like SurfaceMaterialKind, but
    // intValue stays correct even if the enum's declaration order ever
    // changes, so there is no reason to use the more fragile one.
    static void SetEnumField(Object target, string fieldName, int value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.intValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetFloat(Object target, string fieldName, float value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.floatValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetVector3(Object target, string fieldName, Vector3 value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.vector3Value = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetArrayField<T>(Object target, string fieldName, T[] values) where T : Object
    {
        if (target == null)
            return;

        var so = new SerializedObject(target);
        var property = so.FindProperty(fieldName);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{fieldName}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.arraySize = values.Length;
        for (var i = 0; i < values.Length; i++)
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];

        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ---------- V0.8 part 2: real rigged hands (fps-hands.fbx) ----------

    // Wrist to middle fingertip, meters, that the rig is scaled to. At scale
    // 1 the FBX hand measures 0.132m - a child's hand around a 22cm can.
    // The arm scales with it (upper arm ~0.22m, forearm ~0.24m); HandRig
    // uses those measured lengths as they are.
    const float HandRiggedLengthMeters = 0.18f;

    // Bounds of the skinned arms around the shoulders, meters. Covers the
    // arm's full reach (~0.64m at this size) in any direction, so the mesh
    // is never culled while a hand is in view.
    const float HandRigBoundsHalfExtentMeters = 0.9f;

    static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "pinky" };

    class RiggedArmBones
    {
        public Transform UpperArm;
        public Transform Forearm;
        public Transform ForearmEnd;
        public Transform Hand;
        public readonly FingerBones[] Fingers = new FingerBones[5];
        public readonly Transform[] Tips = new Transform[5];
    }

    // Instantiates the rigged hand model ONCE (both arms share one
    // SkinnedMeshRenderer) and wires HandRig (arm IK + hand placement) and
    // one HandBonePoseController per hand. Never touches the old
    // Hand_Single.asset pipeline (RebuildHands/BuildHand, above) - that stays
    // as-is and gets deactivated, not replaced, by DeactivateLegacyHandVisual.
    //
    // Nothing is wired unless every bone of both arms is found: a partially
    // wired arm would only fail later, in Play Mode.
    static GameObject RebuildHandsRigged(GameObject left, GameObject right, GameObject xrOrigin)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(HandRigFbxPath);
        if (source == null)
        {
            log.AppendLine($"AVISO: {HandRigFbxPath} nao encontrado - maos com rig NAO construidas (HandVisual antiga permanece visivel).");
            return null;
        }

        var parent = xrOrigin != null ? xrOrigin.transform : null;
        var existingName = parent != null ? parent.Find("Hands") : GameObject.Find("Hands")?.transform;
        if (existingName != null)
            Object.DestroyImmediate(existingName.gameObject);

        var instanceObj = PrefabUtility.InstantiatePrefab(source) as GameObject;
        if (instanceObj == null)
        {
            log.AppendLine($"ERRO: falha ao instanciar {HandRigFbxPath}.");
            return null;
        }

        instanceObj.transform.SetParent(parent, false);
        instanceObj.name = "Hands";
        instanceObj.transform.localPosition = Vector3.zero;
        instanceObj.transform.localRotation = Quaternion.identity;
        instanceObj.transform.localScale = Vector3.one;

        var basicRig = instanceObj.transform.Find("basicRig");
        var missing = new List<string>();
        var leftBones = basicRig != null ? FindArmBones(basicRig, true, missing) : null;
        var rightBones = basicRig != null ? FindArmBones(basicRig, false, missing) : null;
        if (basicRig == null || missing.Count > 0)
        {
            log.AppendLine($"ERRO: hierarquia do rig incompleta em {HandRigFbxPath} - HandRig/HandBonePoseController NAO conectados. Ausentes: {(basicRig == null ? "basicRig" : string.Join(", ", missing))}");
            return instanceObj;
        }

        // Size from the model's own measurement (bind pose, scale 1).
        var measuredHandLength = Vector3.Distance(leftBones.Hand.position, leftBones.Tips[2].position);
        var scale = HandRiggedLengthMeters / Mathf.Max(0.0001f, measuredHandLength);
        instanceObj.transform.localScale = Vector3.one * scale;
        PrefabUtility.RecordPrefabInstancePropertyModifications(instanceObj.transform);

        var head = FindHead(xrOrigin);
        if (head == null)
            log.AppendLine("ERRO: camera principal (cabeca) nao encontrada sob o XR Origin - HandRig.head fica NULO e o rig nao funciona.");

        var leftGrabber = left.GetComponent<VRGrabber>();
        var rightGrabber = right.GetComponent<VRGrabber>();
        var leftPose = ConfigureBonePose(leftBones, true, leftGrabber);
        var rightPose = ConfigureBonePose(rightBones, false, rightGrabber);

        var rig = GetOrAdd<HandRig>(instanceObj);
        SetField(rig, "head", head);
        WireArm(rig, "left", true, left.transform, leftGrabber, leftPose, leftBones);
        WireArm(rig, "right", false, right.transform, rightGrabber, rightPose, rightBones);
        EditorUtility.SetDirty(rig);

        foreach (var grabber in new[] { leftGrabber, rightGrabber })
        {
            if (grabber == null)
                continue;

            SetField(grabber, "handRig", rig);
            EditorUtility.SetDirty(grabber);
        }

        FitHandRigBounds(instanceObj, basicRig, leftBones, rightBones);

        log.AppendLine($"Hands (rig): {HandRigFbxPath} instanciado 1x sob '{(parent != null ? parent.name : "root")}', escala x{scale:F3} (mao {measuredHandLength:F3}m -> {HandRiggedLengthMeters:F2}m). " +
            $"HandRig: head='{(head != null ? head.name : "NULO")}', braco esquerdo {Describe(leftBones)} -> {left.name}; braco direito {Describe(rightBones)} -> {right.name}. " +
            "VRGrabber.handRig conectado nos dois controllers.");
        return instanceObj;
    }

    // Walks the rig's real hierarchy (checked in the FBX itself):
    //   basicRig/clavicle.X/deltoid.X/upper_arm.X/forearm.X/forearm.X_end
    //       the skinned arm (upper_arm 260 / forearm 320 weighted vertices)
    //   basicRig/forearm.X.001/hand.X/palm_<finger>.X/f_<finger>.01-03.X/..._end
    //   basicRig/forearm.X.001/hand.X/thumb.01-03.X/thumb.03.X_end
    //       the hand (forearm.X.001 itself carries no weights; in the bind
    //       pose hand.X sits exactly on forearm.X_end - one arm, two chains)
    // Every bone is taken as a direct child of the previous one, so it can
    // never come from the other arm or from the other chain.
    static RiggedArmBones FindArmBones(Transform basicRig, bool isLeft, List<string> missing)
    {
        var s = isLeft ? "L" : "R";

        Transform Child(Transform parent, string name)
        {
            if (parent == null)
                return null;

            var found = parent.Find(name);
            if (found == null)
                missing.Add($"{GetPath(parent)}/{name}");
            return found;
        }

        var bones = new RiggedArmBones();
        var deltoid = Child(Child(basicRig, $"clavicle.{s}"), $"deltoid.{s}");
        bones.UpperArm = Child(deltoid, $"upper_arm.{s}");
        bones.Forearm = Child(bones.UpperArm, $"forearm.{s}");
        bones.ForearmEnd = Child(bones.Forearm, $"forearm.{s}_end");
        bones.Hand = Child(Child(basicRig, $"forearm.{s}.001"), $"hand.{s}");

        for (var f = 0; f < 5; f++)
        {
            var chainRoot = f == 0 ? bones.Hand : Child(bones.Hand, $"palm_{FingerNames[f]}.{s}");
            var prefix = f == 0 ? "thumb" : $"f_{FingerNames[f]}";
            var segment1 = Child(chainRoot, $"{prefix}.01.{s}");
            var segment2 = Child(segment1, $"{prefix}.02.{s}");
            var segment3 = Child(segment2, $"{prefix}.03.{s}");
            bones.Fingers[f] = new FingerBones { segment1 = segment1, segment2 = segment2, segment3 = segment3 };
            bones.Tips[f] = Child(segment3, $"{prefix}.03.{s}_end");
        }

        return bones;
    }

    static string Describe(RiggedArmBones bones) =>
        $"{bones.UpperArm.name}/{bones.Forearm.name}/{bones.ForearmEnd.name} + {bones.Hand.name}";

    // The XR camera - what HandRig hangs the shoulders from.
    static Transform FindHead(GameObject xrOrigin)
    {
        if (xrOrigin != null)
        {
            foreach (var camera in xrOrigin.GetComponentsInChildren<Camera>(true))
            {
                if (camera.CompareTag("MainCamera"))
                    return camera.transform;
            }
        }

        return Camera.main != null ? Camera.main.transform : null;
    }

    static HandBonePoseController ConfigureBonePose(RiggedArmBones bones, bool isLeft, VRGrabber grabber)
    {
        var pose = GetOrAdd<HandBonePoseController>(bones.Hand.gameObject);
        SetBool(pose, "isLeftHand", isLeft);
        SetField(pose, "grabber", grabber);

        for (var f = 0; f < 5; f++)
        {
            SetField(pose, $"{FingerNames[f]}.segment1", bones.Fingers[f].segment1);
            SetField(pose, $"{FingerNames[f]}.segment2", bones.Fingers[f].segment2);
            SetField(pose, $"{FingerNames[f]}.segment3", bones.Fingers[f].segment3);
        }

        SetArrayField(pose, "tips", bones.Tips);

        // V0.9 hand polish: the index's sideways range at the top of the human
        // range - with less, it cannot arch over the spray cap from the side
        // of the can (measured: pad 21mm from the cap's axis at 25 degrees).
        SetFloat(pose, "indexAbduction", 30f);

        // How high the fist sits on the can: the middle finger up to 16mm onto
        // the shoulder (still two finger widths below the nozzle's jet) - the
        // height from which the index arches over onto the actuator.
        SetFloat(pose, "fistShoulderAllowance", 0.016f);
        SetFloat(pose, "indexRestBelowSeat", 0.008f);

        if (grabber == null)
            log.AppendLine($"ERRO: VRGrabber ausente no controller {(isLeft ? "esquerdo" : "direito")} - dedos de {bones.Hand.name} nao reagem ao que a mao segura.");

        EditorUtility.SetDirty(pose);
        log.AppendLine($"HandBonePoseController em {bones.Hand.name}: 15 segmentos + 5 pontas (_end) pela cadeia real, isLeftHand={isLeft}, grabber={(grabber != null ? grabber.name : "NULO")}.");
        return pose;
    }

    static void WireArm(HandRig rig, string arm, bool isLeft, Transform controller, VRGrabber grabber, HandBonePoseController pose, RiggedArmBones bones)
    {
        SetBool(rig, $"{arm}.isLeft", isLeft);
        SetField(rig, $"{arm}.controller", controller);
        SetField(rig, $"{arm}.grabber", grabber);
        SetField(rig, $"{arm}.pose", pose);
        SetField(rig, $"{arm}.upperArm", bones.UpperArm);
        SetField(rig, $"{arm}.forearm", bones.Forearm);
        SetField(rig, $"{arm}.forearmEnd", bones.ForearmEnd);
        SetField(rig, $"{arm}.hand", bones.Hand);

        // OpenXR's grip pose taken literally; calibration offsets stay zero.
        SetVector3(rig, $"{arm}.gripRotationOffsetEuler", Vector3.zero);
        SetVector3(rig, $"{arm}.gripPositionOffset", Vector3.zero);
    }

    // The imported renderer's root bone is forearm.L.001, which HandRig
    // never moves, and its bounds are the bind pose's: a hand reaching out
    // or up leaves them, and both arms get culled while still in view.
    // basicRig follows the torso with the rig root, so bounds centred on the
    // shoulders stay around both arms wherever the hands go.
    static void FitHandRigBounds(GameObject hands, Transform basicRig, RiggedArmBones leftBones, RiggedArmBones rightBones)
    {
        var skin = hands.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (skin == null)
        {
            log.AppendLine("ERRO: SkinnedMeshRenderer nao encontrado em Hands.");
            return;
        }

        var shoulders = (leftBones.UpperArm.position + rightBones.UpperArm.position) * 0.5f;
        var rigScale = Mathf.Max(0.0001f, basicRig.lossyScale.x);
        skin.rootBone = basicRig;
        skin.updateWhenOffscreen = false;
        skin.localBounds = new Bounds(basicRig.InverseTransformPoint(shoulders), Vector3.one * (2f * HandRigBoundsHalfExtentMeters / rigScale));
        PrefabUtility.RecordPrefabInstancePropertyModifications(skin);
        EditorUtility.SetDirty(skin);
        log.AppendLine($"Hands: SkinnedMeshRenderer.rootBone = basicRig, bounds {2f * HandRigBoundsHalfExtentMeters:F1}m centrados nos ombros (bracos nao somem com a mao esticada).");
    }

    // Skin weights on device. 946 of the hand mesh's vertices (knuckles,
    // heel of the palm, thumb root) blend 3-4 bones, but the quality levels
    // Android uses skinned with 2 ("Mobile" and "Meta Quest": skinWeights
    // 2). Keeping only the two strongest weights moved those vertices up to
    // 9.5mm once the hand gripped (measured in Play Mode against 4-bone
    // skinning: hand 0.45 / palm_index 0.29 / thumb.01 0.26 loses its thumb
    // share) - creases at the palm heel and thumb root that only the headset
    // showed, since the Editor's PC level skins with 4. A renderer cannot ask
    // for more bones than its quality level allows (SkinnedMeshRenderer
    // .quality = Bone4 under a 2-bone level still skins with 2, also
    // measured), so every level skins with the 4 weights the FBX has. The
    // cost for this single ~4600-vertex mesh is negligible on Quest 3.
    static void ConfigureSkinWeights()
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
        if (assets == null || assets.Length == 0)
        {
            log.AppendLine("ERRO: ProjectSettings/QualitySettings.asset nao carregado - skinWeights nao ajustado.");
            return;
        }

        var so = new SerializedObject(assets[0]);
        var levels = so.FindProperty("m_QualitySettings");
        var changed = new List<string>();
        for (var i = 0; i < levels.arraySize; i++)
        {
            var level = levels.GetArrayElementAtIndex(i);
            var weights = level.FindPropertyRelative("skinWeights");
            if (weights.intValue == (int)SkinWeights.FourBones)
                continue;

            changed.Add($"{level.FindPropertyRelative("name").stringValue} ({weights.intValue} -> 4)");
            weights.intValue = (int)SkinWeights.FourBones;
        }

        if (changed.Count > 0)
        {
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        log.AppendLine(changed.Count > 0
            ? $"QualitySettings: skinWeights = 4 ossos em {string.Join(", ", changed)} (maos do FBX usam ate 4 pesos por vertice)."
            : "QualitySettings: todos os niveis ja usam 4 ossos por vertice.");
    }

    static void DeactivateLegacyHandVisual(GameObject controller)
    {
        var visual = controller.transform.Find("HandVisual");
        if (visual != null && visual.gameObject.activeSelf)
        {
            visual.gameObject.SetActive(false);
            log.AppendLine($"{controller.name}/HandVisual (mao antiga sem rig) desativada, nao apagada - visivel agora vem de 'Hands' (fps-hands.fbx compartilhado).");
        }
    }

    // ---------- V0.8.x: real can mesh + physical cap socket ----------

    // Physical target: real aerosol cans run roughly 20-25cm tall. The
    // midpoint keeps the diameter that follows from the model's own
    // (uniformly scaled, undistorted) proportions close to a real can's.
    const float CanTargetHeightMeters = 0.22f;

    // The orifice marker sits this far outside the actuator's surface,
    // along the spray axis, so the spray and its raycast never start inside
    // the cap's own geometry.
    const float OrificeSurfaceOffsetMeters = 0.0005f;

    // Where the palm holds the can: its grip centre this far below the top
    // of the seated cap. This is the result of the hand's own solve
    // (HandBonePoseController.GripSlide, measured in Play Mode on
    // fps-hands.fbx at its built size): the lowest the fist can sit on the
    // body while the index still arches over the cap onto the actuator,
    // with the grip crossing the palm diagonally. Stored here so the scene's
    // canonical pose is the one the hand really takes; the hand still
    // re-solves it at grab time (another cap or hand size shifts it).
    const float GripBelowCapTopMeters = 0.044f;

    const string IndexRestName = "IndexRest";

    const string GeneratedCanFolder = "Assets/Art/Generated/SprayCan";

    // Measured by RebuildCanVisualFromRealMesh, consumed later in the same
    // Build() by ConfigureCanWallClearance.
    static Vector3? measuredCanBodyCenter;

    class CapBuildData
    {
        public Mesh CapMesh;
        public Vector3 OrificeLocal;
        public Vector3 FingerPadLocal;
        public PhysicalCap StandardCap;
    }

    // Rebuilds everything visible on the can from SM_Spraybottle.fbx and
    // derives every functional transform from its real, measured geometry:
    //
    //   SprayCan (scale 1, pivot at the base centre, +Z = the can's front)
    //   ├── Visual/SM_Spraybottle/../sm_SprayBottle1   body
    //   │                          ../ValveCup          collar, split from sm_Nozzle1
    //   ├── CapSocket (seat on top of the collar)
    //   │   ├── ValveStem, InstallPreview
    //   │   └── NozzlePivot (pressed by NozzleAnimator)
    //   │       ├── Cap_Standard (the actuator split from sm_Nozzle1 - a PhysicalCap)
    //   │       ├── SprayOrigin -> SprayParticles
    //   │       └── IndexRest (where the index fingertip presses)
    //   └── GrabPoint, ColorIndicator, Sfx
    //
    // Spray direction is fixed at the root, not tuned: the actuator's notch
    // (its spout side) is turned to face the can's +Z; SprayOrigin sits on
    // the orifice with identity rotation under NozzlePivot, so the spray
    // leaves horizontally out of the front of an upright can. (The old
    // model's nozzle ran along the can's own axis, which is what made the
    // spray go "up" out of the top of an upright can.)
    //
    // GrabPoint is the axis of the fist, in the OpenXR grip convention
    // (see VRGrabInteractable): its +Z runs along the can's axis and its -Y
    // along the can's +Z. Rotated -90 about X, that is exactly
    // GrabPoint.forward = can up, GrabPoint.up = can back - the fist closes
    // around the body and the spray leaves where the fingers point.
    static CapBuildData RebuildCanVisualFromRealMesh(GameObject sprayCan)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(CanFbxPath) == null)
        {
            log.AppendLine($"AVISO: {CanFbxPath} nao encontrado - visual da lata NAO substituido.");
            return null;
        }

        // All import-setting changes happen once, batched, before any
        // instantiation - see EnsureCanImportSettings. Reload afterwards so
        // the object reflects the (possibly) reimported model.
        EnsureCanImportSettings();
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(CanFbxPath);

        var root = sprayCan.transform;
        var visual = root.Find("Visual");
        var grabPoint = root.Find("GrabPoint");
        var colorIndicator = root.Find("ColorIndicator");
        var socketTransform = FindOrCreate(root, "CapSocket", Vector3.zero).transform;
        var pivot = ResolveNozzlePivot(root, socketTransform);
        var sprayOrigin = pivot != null ? pivot.Find("SprayOrigin") : null;
        if (visual == null || grabPoint == null || sprayOrigin == null)
        {
            log.AppendLine("ERRO: 'Visual', 'GrabPoint' ou 'CapSocket/NozzlePivot/SprayOrigin' ausentes na SprayCan - lata nao reconstruida.");
            return null;
        }

        // Every cap in the scene is rebuilt (the seated one here, the parked
        // ones in BuildCapRack) - no stale duplicates across runs.
        foreach (var existingCap in Object.FindObjectsByType<PhysicalCap>(FindObjectsInactive.Include))
            Object.DestroyImmediate(existingCap.gameObject);

        // Only SprayOrigin and IndexRest survive under the pivot (older runs
        // parked nozzle pieces there), only the pivot under the socket; the
        // pivot's legacy placeholder cube renderer goes too.
        for (var i = pivot.childCount - 1; i >= 0; i--)
        {
            var child = pivot.GetChild(i);
            if (child != sprayOrigin && child.name != IndexRestName)
                Object.DestroyImmediate(child.gameObject);
        }

        for (var i = socketTransform.childCount - 1; i >= 0; i--)
        {
            if (socketTransform.GetChild(i) != pivot)
                Object.DestroyImmediate(socketTransform.GetChild(i).gameObject);
        }

        DestroyIfPresent<MeshRenderer>(pivot.gameObject);
        DestroyIfPresent<MeshFilter>(pivot.gameObject);

        for (var i = visual.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(visual.GetChild(i).gameObject);

        visual.localPosition = Vector3.zero;
        visual.localRotation = Quaternion.identity;
        visual.localScale = Vector3.one;

        // Object.Instantiate, not PrefabUtility.InstantiatePrefab: rebuilt
        // from scratch every run, and a live Prefab Instance forbids the
        // reparenting done below.
        var instance = Object.Instantiate(source);
        instance.name = "SM_Spraybottle";
        instance.transform.SetParent(visual, false);
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        // localScale stays the FBX root's own (a feet -> meters unit
        // conversion) - measured below, never overwritten.

        var body = FindDeep(instance.transform, "sm_SprayBottle1");
        var nozzle = FindDeep(instance.transform, "sm_Nozzle1");
        var bodyRenderer = body != null ? body.GetComponent<MeshRenderer>() : null;
        var nozzleFilter = nozzle != null ? nozzle.GetComponent<MeshFilter>() : null;
        var nozzleRenderer = nozzle != null ? nozzle.GetComponent<MeshRenderer>() : null;
        if (bodyRenderer == null || nozzleFilter == null || nozzleRenderer == null)
        {
            log.AppendLine("ERRO: sm_SprayBottle1/sm_Nozzle1 nao encontrados no modelo - lata nao reconstruida.");
            return null;
        }

        var parts = SprayCapMeshGenerator.Split(nozzleFilter.sharedMesh, out var splitError);
        if (parts == null)
        {
            log.AppendLine($"ERRO: nao foi possivel separar colar/atuador de sm_Nozzle1: {splitError}");
            return null;
        }

        var bodyMaterial = BuildCanBodyMaterial();

        // 1) Uniform scale to the physical target height, measured on the
        //    live instance (body base -> top of the nozzle).
        var rawHeight = Mathf.Max(0.0001f, nozzleRenderer.bounds.max.y - bodyRenderer.bounds.min.y);
        var scaleFactor = CanTargetHeightMeters / rawHeight;
        instance.transform.localScale *= scaleFactor;

        // 2) Yaw the model so the actuator's orifice (the bore drawn in its
        //    normal map - see SprayCapMeshGenerator) faces the can's +Z, and
        //    with it the finger scoop faces -Z, toward the hand. A signed
        //    angle about +Y, not FromToRotation, which between near-opposite
        //    vectors may rotate about a tilted axis.
        var orificeCan = root.InverseTransformDirection(nozzle.TransformDirection(parts.OrificeDirection));
        orificeCan.y = 0f;
        var yaw = Vector3.SignedAngle(orificeCan, Vector3.forward, Vector3.up);
        instance.transform.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) * instance.transform.localRotation;

        // 3) Body axis onto the can's own axis, base at the can's origin.
        var bodyBounds = bodyRenderer.bounds;
        var bodyCenter = root.InverseTransformPoint(bodyBounds.center);
        var bodyBase = root.InverseTransformPoint(bodyBounds.min).y;
        instance.transform.localPosition -= new Vector3(bodyCenter.x, bodyBase, bodyCenter.z);

        // Final, measured numbers (with the full nozzle still in place, so
        // totalHeight is body + seated cap).
        bodyBounds = bodyRenderer.bounds;
        var bodyTop = root.InverseTransformPoint(bodyBounds.max).y;
        var bodyRadius = Mathf.Max(bodyBounds.extents.x, bodyBounds.extents.z);
        var totalHeight = root.InverseTransformPoint(nozzleRenderer.bounds.max).y;
        measuredCanBodyCenter = new Vector3(0f, bodyTop * 0.5f, 0f);

        bodyRenderer.sharedMaterial = bodyMaterial;

        // 4) Socket at the seat (top centre of the collar), in can space.
        var toCan = root.worldToLocalMatrix * nozzle.localToWorldMatrix;
        var seat = toCan.MultiplyPoint3x4(parts.Seat);
        socketTransform.localPosition = seat;
        socketTransform.localRotation = Quaternion.identity;
        socketTransform.localScale = Vector3.one;
        pivot.localPosition = Vector3.zero;
        pivot.localRotation = Quaternion.identity;
        pivot.localScale = Vector3.one;

        // 5) The collar stays on the can, in place of the original nozzle
        //    mesh; the actuator becomes the Standard cap, re-expressed in the
        //    cap's own frame (meters, pivot at the seat, +Z out of the orifice).
        nozzle.name = "ValveCup";
        nozzleFilter.sharedMesh = SaveMeshAsset(parts.Collar, $"{GeneratedCanFolder}/SprayCan_ValveCup.asset");
        nozzleRenderer.sharedMaterial = bodyMaterial;

        var capMesh = SaveMeshAsset(BuildCapMesh(parts, toCan, seat), $"{GeneratedCanFolder}/Cap_Actuator.asset");
        var orificeLocal = toCan.MultiplyPoint3x4(parts.Orifice) - seat + Vector3.forward * OrificeSurfaceOffsetMeters;
        var fingerPadLocal = toCan.MultiplyPoint3x4(parts.FingerPad) - seat;

        var capData = new CapBuildData { CapMesh = capMesh, OrificeLocal = orificeLocal, FingerPadLocal = fingerPadLocal };
        var standard = System.Array.Find(SprayCap.CreateDefaults(), c => c.displayName == "Standard") ?? new SprayCap();
        capData.StandardCap = CreatePhysicalCap("Cap_Standard", standard, capData, bodyMaterial, 1f, placeholder: false);
        capData.StandardCap.transform.SetParent(pivot, false);

        // 6) SprayOrigin on the orifice; particles born exactly there.
        sprayOrigin.localPosition = orificeLocal;
        sprayOrigin.localRotation = Quaternion.identity;
        sprayOrigin.localScale = Vector3.one;
        foreach (Transform child in sprayOrigin)
        {
            child.localPosition = Vector3.zero;
            child.localRotation = Quaternion.identity;
        }

        // 7) The fist's axis on the can's axis, at the height that puts the
        //    index finger on the actuator (see GripBelowCapTopMeters).
        grabPoint.localPosition = new Vector3(0f, totalHeight - GripBelowCapTopMeters, 0f);
        grabPoint.localRotation = Quaternion.Euler(-90f, 0f, 0f);

        // 8) Where the index finger presses: the seated cap's finger pad (on
        //    its scoop). CapSocket keeps it there (or on the bare stem) as
        //    caps change.
        var indexRest = pivot.Find(IndexRestName);
        if (indexRest == null)
        {
            indexRest = new GameObject(IndexRestName).transform;
            indexRest.SetParent(pivot, false);
        }

        // Kept across runs while the cap is rebuilt, so pinned after it:
        // every run leaves the pivot's children in the same order.
        indexRest.SetAsLastSibling();

        indexRest.localPosition = capData.FingerPadLocal;
        indexRest.localRotation = Quaternion.identity;
        indexRest.localScale = Vector3.one;

        // 9) What the fingers close around: the can's real shape along its
        //    axis (body sliced in bands - its shoulder tapers - then the
        //    valve cup and the seated cap), in GrabPoint space.
        var profile = new List<VRGrabInteractable.GripSection>();
        MeasureRadiusProfile(bodyRenderer.GetComponent<MeshFilter>().sharedMesh, root.worldToLocalMatrix * body.localToWorldMatrix, GripProfileBandMeters, profile);
        MeasureRadiusProfile(nozzleFilter.sharedMesh, toCan, float.MaxValue, profile);
        MeasureRadiusProfile(capMesh, Matrix4x4.Translate(seat), float.MaxValue, profile);
        for (var i = 0; i < profile.Count; i++)
        {
            var section = profile[i];
            section.bottom -= grabPoint.localPosition.y;
            section.top -= grabPoint.localPosition.y;
            profile[i] = section;
        }

        var canGrab = sprayCan.GetComponent<VRGrabInteractable>();
        if (canGrab != null)
        {
            SetField(canGrab, "grabPoint", grabPoint);
            SetFloat(canGrab, "gripRadius", bodyRadius);
            SetField(canGrab, "fingerRest", indexRest);
            SetField(canGrab, "keepClearOf", sprayOrigin);
            SetGripProfile(canGrab, profile);
            EditorUtility.SetDirty(canGrab);
        }

        BuildValveStem(socketTransform);
        var preview = BuildInstallPreview(socketTransform, capMesh);
        ShapeColorIndicator(colorIndicator, bodyTop, bodyRadius);
        FitGrabCollider(sprayCan, profile, grabPoint.localPosition.y);

        var sfxTransform = root.Find("Sfx");
        var socket = GetOrAdd<CapSocket>(socketTransform.gameObject);
        SetField(socket, "installParent", pivot);
        SetField(socket, "sprayOrigin", sprayOrigin);
        SetField(socket, "capManager", sprayCan.GetComponent<CapManager>());
        SetField(socket, "canGrab", sprayCan.GetComponent<VRGrabInteractable>());
        SetField(socket, "sfx", sfxTransform != null ? sfxTransform.GetComponent<SprayCanSfx>() : null);
        SetField(socket, "initialCap", capData.StandardCap);
        SetField(socket, "installPreview", preview);
        SetField(socket, "fingerRest", indexRest);
        EditorUtility.SetDirty(socket);

        // The old procedural-cylinder swapper would overwrite the real mesh
        // with a primitive on Awake. Script file kept; scene instance removed.
        DestroyIfPresent<SprayCanVisual>(sprayCan);

        EditorUtility.SetDirty(sprayCan);

        var notchNote = $"orificio do atuador pelo disco do normal map (UV {SprayCapMeshGenerator.OrificeUv}), rampa do dedo do lado oposto (profundidade {parts.ScoopDepthFraction:P0}); modelo girado {yaw:F1} graus: orificio em +Z da lata, rampa voltada para a mao";
        log.AppendLine($"Lata: escala x{scaleFactor:F3} sobre a nativa (altura bruta {rawHeight:F3}m -> {totalHeight:F3}m, diametro {bodyRadius * 2f:F3}m), corpo centralizado no eixo. sm_Nozzle1 separado em ValveCup (fica na lata) + atuador (Cap_Standard, PhysicalCap). {notchNote}. SprayOrigin no orificio a {(seat + orificeLocal).y:F3}m de altura, forward = +Z da lata. GrabPoint (eixo do punho) a {grabPoint.localPosition.y:F3}m, rotacao (-90,0,0): forward = eixo da lata, -up = direcao do spray. IndexRest na rampa do cap (FingerPad), gripRadius {bodyRadius:F3}m.");
        return capData;
    }

    // Height of the bands the body is sliced into for the grip profile:
    // fine enough to follow the shoulder's taper, coarse enough to stay a
    // handful of sections.
    const float GripProfileBandMeters = 0.01f;

    // Stacked cylinders around the can axis (can space: x = z = 0), one per
    // band of height: the widest vertex of the mesh in that band. Adjacent
    // bands within 1mm of each other are merged.
    static void MeasureRadiusProfile(Mesh mesh, Matrix4x4 toCan, float band, List<VRGrabInteractable.GripSection> profile)
    {
        var vertices = mesh.vertices;
        var bottom = float.MaxValue;
        var top = float.MinValue;
        var points = new Vector3[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            points[i] = toCan.MultiplyPoint3x4(vertices[i]);
            bottom = Mathf.Min(bottom, points[i].y);
            top = Mathf.Max(top, points[i].y);
        }

        var count = Mathf.Max(1, Mathf.CeilToInt((top - bottom) / Mathf.Min(band, top - bottom + 1e-6f)));
        var radii = new float[count];
        var filled = new bool[count];
        foreach (var p in points)
        {
            var slot = Mathf.Min(count - 1, (int)((p.y - bottom) / (top - bottom + 1e-6f) * count));
            radii[slot] = Mathf.Max(radii[slot], new Vector2(p.x, p.z).magnitude);
            filled[slot] = true;
        }

        // A straight or conical wall has vertices only at its end rings:
        // the bands in between follow the line between those rings.
        for (var i = 0; i < count; i++)
        {
            if (filled[i])
                continue;

            var below = i - 1;
            while (below >= 0 && !filled[below])
                below--;
            var above = i + 1;
            while (above < count && !filled[above])
                above++;

            if (below >= 0 && above < count)
                radii[i] = Mathf.Lerp(radii[below], radii[above], (i - below) / (float)(above - below));
            else
                radii[i] = below >= 0 ? radii[below] : above < count ? radii[above] : 0f;
        }

        var height = (top - bottom) / count;
        for (var i = 0; i < count; i++)
        {
            var section = new VRGrabInteractable.GripSection { bottom = bottom + i * height, top = bottom + (i + 1) * height, radius = radii[i] };
            var last = profile.Count - 1;
            if (i > 0 && last >= 0 && Mathf.Abs(profile[last].radius - section.radius) < 0.001f)
            {
                var merged = profile[last];
                merged.top = section.top;
                merged.radius = Mathf.Max(merged.radius, section.radius);
                profile[last] = merged;
            }
            else
            {
                profile.Add(section);
            }
        }
    }

    static void SetGripProfile(VRGrabInteractable grab, List<VRGrabInteractable.GripSection> profile)
    {
        var so = new SerializedObject(grab);
        var property = so.FindProperty("gripProfile");
        property.arraySize = profile.Count;
        for (var i = 0; i < profile.Count; i++)
        {
            var element = property.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("bottom").floatValue = profile[i].bottom;
            element.FindPropertyRelative("top").floatValue = profile[i].top;
            element.FindPropertyRelative("radius").floatValue = profile[i].radius;
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        var summary = new StringBuilder();
        foreach (var s in profile)
            summary.Append($" [{s.bottom * 1000f:F0}..{s.top * 1000f:F0}mm r{s.radius * 1000f:F1}]");
        log.AppendLine($"Perfil de pegada da lata ({profile.Count} secoes, espaco do GrabPoint):{summary}");
    }

    // Legacy scenes have the press pivot as "SprayCan/Cap" (holding
    // SprayOrigin). It is moved under CapSocket and renamed, keeping the same
    // object - and with it every serialized reference into that chain.
    static Transform ResolveNozzlePivot(Transform root, Transform socket)
    {
        var pivot = socket.Find("NozzlePivot");
        if (pivot != null)
            return pivot;

        var legacy = root.Find("Cap");
        if (legacy == null)
            return null;

        legacy.name = "NozzlePivot";
        legacy.SetParent(socket, false);
        return legacy;
    }

    static Mesh BuildCapMesh(SprayCapMeshGenerator.NozzleParts parts, Matrix4x4 toCan, Vector3 seat)
    {
        var count = parts.ActuatorPositions.Count;
        var positions = new List<Vector3>(count);
        var normals = new List<Vector3>(count);
        var tangents = new List<Vector4>(count);

        for (var i = 0; i < count; i++)
        {
            positions.Add(toCan.MultiplyPoint3x4(parts.ActuatorPositions[i]) - seat);
            normals.Add(toCan.MultiplyVector(parts.ActuatorNormals[i]).normalized);

            // Uniform positive scale plus a yaw: no mirroring, so the
            // tangent's handedness (w) carries over unchanged.
            var t = parts.ActuatorTangents[i];
            var direction = toCan.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
            tangents.Add(new Vector4(direction.x, direction.y, direction.z, t.w));
        }

        var mesh = new Mesh { name = "Cap_Actuator" };
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetTangents(tangents);
        mesh.SetUVs(0, parts.ActuatorUvs);
        mesh.SetTriangles(parts.ActuatorTriangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Overwrites an existing mesh asset's data in place (stable GUID, so
    // scene references survive re-runs) or creates it. Any folder under
    // Assets/Art/Generated; every submesh; channels the mesh has.
    static Mesh SaveMeshAsset(Mesh mesh, string path)
    {
        EnsureFolder("Assets/Art", "Generated");
        EnsureFolder("Assets/Art/Generated", Path.GetFileName(Path.GetDirectoryName(path)));

        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        var count = mesh.vertexCount;
        existing.Clear();
        existing.SetVertices(mesh.vertices);
        if (mesh.normals.Length == count)
            existing.SetNormals(mesh.normals);
        if (mesh.tangents.Length == count)
            existing.SetTangents(mesh.tangents);
        if (mesh.uv.Length == count)
            existing.SetUVs(0, mesh.uv);

        existing.subMeshCount = mesh.subMeshCount;
        for (var s = 0; s < mesh.subMeshCount; s++)
            existing.SetTriangles(mesh.GetTriangles(s), s);

        existing.RecalculateBounds();
        EditorUtility.SetDirty(existing);
        Object.DestroyImmediate(mesh);
        return existing;
    }

    // The bare valve stem you see with the cap pulled off: a 4mm pin rising
    // 6mm from the seat. Hidden inside the cap while one is seated.
    static void BuildValveStem(Transform socket)
    {
        MakeCylinder(socket, "ValveStem", new Vector3(0f, 0.003f, 0f), new Vector3(0.004f, 0.003f, 0.004f),
            CreateMaterial("ValveStem", new Color(0.78f, 0.78f, 0.8f), 0.55f));
    }

    // Thin flat ring around the seat, lit only while a held cap would seat
    // on release - the "it fits here" cue. Unlit so it reads in any light.
    static GameObject BuildInstallPreview(Transform socket, Mesh capMesh)
    {
        var capRadius = Mathf.Max(capMesh.bounds.extents.x, capMesh.bounds.extents.z);
        var ring = SaveMeshAsset(BuildRingMesh(capRadius * 1.05f, capRadius * 1.3f, 32), $"{GeneratedCanFolder}/CapSocket_PreviewRing.asset");

        var preview = new GameObject("InstallPreview");
        preview.transform.SetParent(socket, false);
        preview.transform.localPosition = new Vector3(0f, 0.001f, 0f);
        preview.AddComponent<MeshFilter>().sharedMesh = ring;
        var renderer = preview.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = CreateUnlitMaterial("CapSocketPreview", new Color(0.45f, 0.95f, 1f));
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        preview.SetActive(false);
        return preview;
    }

    static Mesh BuildRingMesh(float innerRadius, float outerRadius, int segments)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();

        for (var i = 0; i < segments; i++)
        {
            var angle = i * Mathf.PI * 2f / segments;
            var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            positions.Add(direction * innerRadius);
            positions.Add(direction * outerRadius);
            normals.Add(Vector3.up);
            normals.Add(Vector3.up);
        }

        for (var i = 0; i < segments; i++)
        {
            var a = i * 2;
            var b = ((i + 1) % segments) * 2;
            AddUpFacing(triangles, positions, a, a + 1, b + 1);
            AddUpFacing(triangles, positions, a, b + 1, b);
        }

        var mesh = new Mesh { name = "CapSocket_PreviewRing" };
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Unity front faces satisfy Cross(b - a, c - a) . normal > 0.
    static void AddUpFacing(List<int> triangles, List<Vector3> positions, int a, int b, int c)
    {
        if (Vector3.Dot(Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]), Vector3.up) < 0f)
            (b, c) = (c, b);

        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    // The loaded-color indicator becomes a thin band around the upper body
    // (like a colour stripe on a real can), instead of a cube that would
    // now sit inside the cap socket. Same object, so SprayCanController's
    // reference to its renderer stays valid.
    static void ShapeColorIndicator(Transform indicator, float bodyTop, float bodyRadius)
    {
        if (indicator == null || !indicator.TryGetComponent<MeshFilter>(out var filter))
            return;

        var temp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        filter.sharedMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(temp);

        var diameter = bodyRadius * 2f * 1.03f;
        indicator.localPosition = new Vector3(0f, bodyTop * 0.78f, 0f);
        indicator.localRotation = Quaternion.identity;
        indicator.localScale = new Vector3(diameter, 0.003f, diameter);
        EditorUtility.SetDirty(indicator);
    }

    // What VRGrabber's overlap test and physics see: a convex hull of the
    // can's own measured silhouette (the grip profile - body, shoulder,
    // valve cup, cap), 12 sides. Round, so a thrown or dropped can rolls and
    // bounces like a can instead of tumbling on box corners; flat-bottomed,
    // so it still stands upright on the table. (V0.8 used a box; a capsule
    // before that could not stand.)
    const int CanColliderSides = 12;
    const string CanColliderMeshPath = GeneratedCanFolder + "/SprayCan_Collider.asset";
    const string CanPhysicsMaterialPath = "Assets/Materials/Physics/SprayCan.physicMaterial";

    static void FitGrabCollider(GameObject sprayCan, List<VRGrabInteractable.GripSection> profileInGrabSpace, float grabPointHeight)
    {
        DestroyIfPresent<CapsuleCollider>(sprayCan);
        DestroyIfPresent<BoxCollider>(sprayCan);

        var rings = new List<(float y, float r)>();
        foreach (var section in profileInGrabSpace)
        {
            rings.Add((section.bottom + grabPointHeight, section.radius));
            rings.Add((section.top + grabPointHeight, section.radius));
        }

        var positions = new List<Vector3>();
        foreach (var ring in rings)
        {
            for (var i = 0; i < CanColliderSides; i++)
            {
                var angle = i * Mathf.PI * 2f / CanColliderSides;
                positions.Add(new Vector3(Mathf.Cos(angle) * ring.r, ring.y, Mathf.Sin(angle) * ring.r));
            }
        }

        // Only the points matter: the collider cooks their convex hull.
        var triangles = new List<int>();
        for (var i = 2; i < positions.Count; i++)
        {
            triangles.Add(0);
            triangles.Add(i - 1);
            triangles.Add(i);
        }

        var hull = new Mesh { name = "SprayCan_Collider" };
        hull.SetVertices(positions);
        hull.SetTriangles(triangles, 0);
        hull.RecalculateBounds();
        var saved = SaveMeshAsset(hull, CanColliderMeshPath);

        var collider = GetOrAdd<MeshCollider>(sprayCan);
        collider.sharedMesh = null;
        collider.sharedMesh = saved;
        collider.convex = true;
        collider.isTrigger = false;
        collider.sharedMaterial = BuildCanPhysicsMaterial();
        EditorUtility.SetDirty(collider);
        log.AppendLine($"Lata: collider convexo de {CanColliderSides} lados sobre o perfil medido ({rings.Count} aneis), material fisico {CanPhysicsMaterialPath}.");
    }

    // A thin aluminium can on concrete: a little bounce, moderate friction.
    static PhysicsMaterial BuildCanPhysicsMaterial()
    {
        EnsureFolder("Assets/Materials", "Physics");
        var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(CanPhysicsMaterialPath);
        if (material == null)
        {
            material = new PhysicsMaterial("SprayCan");
            AssetDatabase.CreateAsset(material, CanPhysicsMaterialPath);
        }

        material.bounciness = 0.3f;
        material.dynamicFriction = 0.35f;
        material.staticFriction = 0.5f;
        material.bounceCombine = PhysicsMaterialCombine.Average;
        material.frictionCombine = PhysicsMaterialCombine.Average;
        EditorUtility.SetDirty(material);
        return material;
    }

    static void DestroyIfPresent<T>(GameObject go) where T : Component
    {
        if (go.TryGetComponent<T>(out var component))
            Object.DestroyImmediate(component);
    }

    // URP/Lit's single metallic workflow expects ONE combined map (R =
    // metallic, A = smoothness) - the 5 source textures give BaseColor,
    // Normal and Height as usable separate maps, but Metallic and Roughness
    // are separate textures, not a combined one. Packing them into a single
    // map is an image-editing step this Editor script has no way to preview
    // the result of (no interactive Unity session available here), so this
    // samples the two source textures directly (coarse grid, not every
    // pixel - they are 4096x4096) and drives the shader's plain float
    // sliders with the sampled averages instead of inventing a
    // keyword/map wiring that was never confirmed to render correctly.
    // BaseMap/BumpMap/ParallaxMap property names and the _NORMALMAP/
    // _PARALLAXMAP keywords were verified against the installed URP package
    // source (Shaders/Lit.shader, Shaders/LitInput.hlsl), not guessed.
    static Material BuildCanBodyMaterial()
    {
        // Asset-backed (like CreateMaterial, above), not a throwaway
        // in-memory Material - otherwise every re-run of the builder embeds
        // a fresh, unreferenced copy directly into the scene file instead of
        // reusing/updating the same .mat asset.
        var path = $"{RoomMaterialsFolder}/SprayCan_Body.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                log.AppendLine("ERRO: shader 'Universal Render Pipeline/Lit' nao encontrado - material da lata nao criado.");
                return null;
            }

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        var baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CanTexturesFolder}/LR_SprayCan_phong2_BaseColor.png");
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CanTexturesFolder}/LR_SprayCan_phong2_Normal.1001.png");
        var height = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CanTexturesFolder}/LR_SprayCan_phong2_Height.1001.png");
        var metallic = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CanTexturesFolder}/LR_SprayCan_phong2_Metallic.1001.png");
        var roughness = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CanTexturesFolder}/LR_SprayCan_phong2_Roughness.1001.png");

        if (baseColor != null)
            material.SetTexture("_BaseMap", baseColor);
        else
            log.AppendLine("AVISO: BaseColor.png nao encontrado - material da lata sem albedo.");

        // Normal's texture-type and Metallic/Roughness's Read/Write flag are
        // both set once, up front, by EnsureCanImportSettings (called
        // by RebuildCanVisualFromRealMesh before this method runs) - not
        // here, and not per-call, to avoid the reimport-cycle bug documented
        // there.
        if (normal != null)
        {
            material.SetTexture("_BumpMap", normal);
            material.EnableKeyword("_NORMALMAP");
        }

        if (height != null)
        {
            material.SetTexture("_ParallaxMap", height);
            material.SetFloat("_Parallax", 0.01f); // small on purpose - strong parallax on a ~7cm-wide can reads as warping, not detail.
            material.EnableKeyword("_PARALLAXMAP");
        }

        material.SetFloat("_Metallic", SampleAverageGrayscale(metallic, 0f));
        material.SetFloat("_Smoothness", 1f - SampleAverageGrayscale(roughness, 0.5f));

        EditorUtility.SetDirty(material);
        return material;
    }

    // ---------- spray particles ----------

    const string SprayParticleMaterialPath = "Assets/Materials/Spray/SprayParticle.mat";
    const string SprayParticleTexturePath = "Assets/Art/Generated/Spray/SprayParticleDot.png";
    const int SprayParticleTextureSize = 64;

    // Why the particles rendered as big opaque squares on Quest: the spray
    // built its material at runtime (Shader.Find + transparent keywords set
    // in code). No material in the build used that keyword combination, so
    // URP's variant stripping (m_StripUnusedVariants) left it out of the
    // Android player and the device drew the opaque variant - each particle
    // a solid quad. In the Editor every variant exists, so it looked fine.
    //
    // Fix: one persistent material asset, referenced from the scene
    // (SprayEmitter.particleMaterialAsset), so the build keeps exactly its
    // variant; the emitter only clones it to change _BaseColor.
    // 'Particles/Unlit', not 'Unlit': it multiplies by the particle's vertex
    // colour, which is what applies the emitter's alpha fade. Keywords and
    // render state come from URP's own ShaderGUI setup - the same call its
    // material inspector makes (ParticlesUnlitShader.ValidateMaterial).
    static Material BuildSprayParticleMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
        {
            log.AppendLine("ERRO: shader 'Universal Render Pipeline/Particles/Unlit' nao encontrado - material das particulas nao criado.");
            return null;
        }

        var texture = BuildSprayParticleTexture();

        EnsureFolder("Assets", "Materials");
        EnsureFolder("Assets/Materials", "Spray");

        var material = AssetDatabase.LoadAssetAtPath<Material>(SprayParticleMaterialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, SprayParticleMaterialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Surface", (float)BaseShaderGUI.SurfaceType.Transparent);
        material.SetFloat("_Blend", (float)BaseShaderGUI.BlendMode.Alpha);
        material.SetFloat("_ColorMode", (float)UnityEditor.Rendering.Universal.ShaderGUI.ParticleGUI.ColorMode.Multiply);
        material.SetFloat("_AlphaClip", 0f);
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        // Both need the camera depth texture - an extra full-screen pass per
        // eye that this project does not pay for on Quest.
        material.SetFloat("_SoftParticlesEnabled", 0f);
        material.SetFloat("_CameraFadingEnabled", 0f);

        BaseShaderGUI.SetMaterialKeywords(material, null, UnityEditor.Rendering.Universal.ShaderGUI.ParticleGUI.SetMaterialKeywords);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssetIfDirty(material);

        var transparent = material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT");
        log.AppendLine($"Particulas: material persistente {SprayParticleMaterialPath} (URP Particles/Unlit, transparente={transparent}, renderQueue={material.renderQueue}, textura {SprayParticleTexturePath}).");
        return material;
    }

    // A round dot whose alpha reaches zero before the quad's edge, so no
    // particle can ever show its square outline. Written only when the
    // bytes change, so re-running the builder does not reimport it.
    static Texture2D BuildSprayParticleTexture()
    {
        EnsureFolder("Assets/Art", "Generated");
        EnsureFolder("Assets/Art/Generated", "Spray");

        const int size = SprayParticleTextureSize;
        var pixels = new Color32[size * size];
        var center = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - center) / (size * 0.5f);
                var dy = (y - center) / (size * 0.5f);
                var t = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                var alpha = t * t * (3f - 2f * t);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        }

        var temp = new Texture2D(size, size, TextureFormat.RGBA32, false);
        temp.SetPixels32(pixels);
        temp.Apply();
        var png = temp.EncodeToPNG();
        Object.DestroyImmediate(temp);

        var fullPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, SprayParticleTexturePath);
        if (!File.Exists(fullPath) || !SameBytes(File.ReadAllBytes(fullPath), png))
        {
            File.WriteAllBytes(fullPath, png);
            AssetDatabase.ImportAsset(SprayParticleTexturePath, ImportAssetOptions.ForceSynchronousImport);
        }

        if (AssetImporter.GetAtPath(SprayParticleTexturePath) is TextureImporter importer)
        {
            var needsReimport = importer.textureType != TextureImporterType.Default
                || importer.alphaSource != TextureImporterAlphaSource.FromInput
                || !importer.alphaIsTransparency
                || importer.wrapMode != TextureWrapMode.Clamp
                || !importer.mipmapEnabled
                || importer.textureCompression != TextureImporterCompression.Uncompressed;

            if (needsReimport)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;

                // 64x64: uncompressed costs ~21KB with mips and keeps the
                // soft falloff free of block artefacts.
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(SprayParticleTexturePath);
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    // Fixes "Importer(TextureImporter) generated inconsistent result for
    // asset" on the Metallic/Roughness textures. Root cause: the previous
    // version of SampleAverageGrayscale toggled isReadable ON, reimported,
    // sampled, then toggled it back OFF and reimported AGAIN - two
    // SaveAndReimport calls on the SAME asset back-to-back, interleaved with
    // unrelated AssetDatabase work elsewhere in the same Build() call
    // (prefab/model instantiation, material creation). Unity's import
    // pipeline does not guarantee a consistent artifact when the same asset
    // is reimported twice in immediate succession like that.
    //
    // Fix: every texture import-setting change happens ONCE, permanently
    // (matching how the Normal map's texture type was already handled),
    // batched via StartAssetEditing/StopAssetEditing so Unity defers the
    // actual reimport work instead of processing each one synchronously
    // mid-build. Never toggled back off afterward - there is no cycle left
    // to go wrong. Called once, up front, before any mesh work.
    //
    // The FBX itself is made Read/Write Enabled in the same batch: splitting
    // sm_Nozzle1 into collar + actuator reads its vertices. It is a 934-vertex
    // model, so the CPU-side copy this keeps is negligible.
    static void EnsureCanImportSettings()
    {
        AssetDatabase.StartAssetEditing();
        try
        {
            SetNormalMapImportType($"{CanTexturesFolder}/LR_SprayCan_phong2_Normal.1001.png");
            SetReadableIfNeeded($"{CanTexturesFolder}/LR_SprayCan_phong2_Metallic.1001.png");
            SetReadableIfNeeded($"{CanTexturesFolder}/LR_SprayCan_phong2_Roughness.1001.png");

            if (AssetImporter.GetAtPath(CanFbxPath) is ModelImporter model && !model.isReadable)
            {
                model.isReadable = true;
                model.SaveAndReimport();
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
    }

    static void SetNormalMapImportType(string texturePath)
    {
        var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer == null || importer.textureType == TextureImporterType.NormalMap)
            return;

        importer.textureType = TextureImporterType.NormalMap;
        importer.SaveAndReimport();
    }

    // Left permanently Read/Write Enabled once set - these are small (4096
    // source, but only ever sampled at a 16x16 grid) Editor-time-only reads;
    // the CPU-side copy this keeps resident is the same trade already made
    // for other textures in this project, and toggling it back off per-call
    // was the actual bug (see EnsureCanImportSettings).
    static void SetReadableIfNeeded(string texturePath)
    {
        var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer == null || importer.isReadable)
            return;

        importer.isReadable = true;
        importer.SaveAndReimport();
    }

    // Read-only: by the time this runs, EnsureCanImportSettings has
    // already made the texture Read/Write Enabled (once, up front). This
    // function never changes import settings and never calls
    // SaveAndReimport, so it cannot trigger the reimport-cycle bug.
    static float SampleAverageGrayscale(Texture2D texture, float fallback)
    {
        if (texture == null)
            return fallback;

        var path = AssetDatabase.GetAssetPath(texture);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null && !importer.isReadable)
        {
            log.AppendLine($"AVISO: '{texture.name}' nao esta Read/Write Enabled (EnsureCanImportSettings deveria ter marcado) - usando valor padrao {fallback:F2}.");
            return fallback;
        }

        try
        {
            const int samples = 16;
            var sum = 0f;
            var count = 0;
            for (var y = 0; y < samples; y++)
            {
                for (var x = 0; x < samples; x++)
                {
                    var px = Mathf.Clamp(x * texture.width / samples, 0, texture.width - 1);
                    var py = Mathf.Clamp(y * texture.height / samples, 0, texture.height - 1);
                    sum += texture.GetPixel(px, py).grayscale;
                    count++;
                }
            }

            return count > 0 ? sum / count : fallback;
        }
        catch (UnityException)
        {
            log.AppendLine($"AVISO: nao foi possivel amostrar '{texture.name}' (formato/compressao nao legivel por GetPixel) - usando valor padrao {fallback:F2}.");
            return fallback;
        }
    }

    // Recursive name search - used instead of hardcoded child paths because
    // the real FBX bone/mesh hierarchy has several intermediate levels
    // (palm_index.L, group2, ...) that do not matter for finding a specific,
    // uniquely-named bone or mesh node.
    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name)
            return root;

        for (var i = 0; i < root.childCount; i++)
        {
            var found = FindDeep(root.GetChild(i), name);
            if (found != null)
                return found;
        }

        return null;
    }
}
