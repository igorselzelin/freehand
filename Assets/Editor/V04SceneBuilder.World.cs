using System.Collections.Generic;
using System.IO;
using Freehand.Environment;
using Freehand.Spray;
using Freehand.Tools;
using Freehand.VR;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// V0.9 world pass: the color station that replaced the palette's debug
// cubes, the color picker's frame, the cap tray and a light dressing pass
// over the blockout room (concrete, block wall, posters and stickers).
//
// Everything is generated here - small meshes and small textures, nothing
// imported - and kept cheap for Quest: each station can is one renderer,
// all the holder rings are one combined mesh, every poster and sticker
// shares one atlas material, and the room's textures are 128px tiles.
public static partial class V04SceneBuilder
{
    const string GeneratedWorldFolder = "Assets/Art/Generated/World";

    // ---------- V0.9 color station ----------

    // Station space: floor at y=0, the front (-Z) facing the player, +X to
    // the player's right. The cans hang in wire rings in front of a
    // pegboard, 4 to a row; the cleaner is apart from the colors, at the
    // end of the bottom row.
    const int StationColumns = 4;
    const float StationColumnSpacing = 0.14f;
    const float StationTopRowY = 1.17f;
    const float StationRowSpacing = 0.24f;
    const float StationBoardZ = 0.06f;

    // Selection light relative to the selected can (slot space, so it
    // follows the can's selection pop): on the pegboard face just under the
    // popped can - a lit shelf tag, not a marker floating in the air.
    static readonly Vector3 StationMarkerOffset = new Vector3(0f, -0.1035f, 0.0435f);


    // A 400ml graffiti can, 66mm x 188mm, pivot at its middle so the
    // rack's selection pop grows it evenly without touching anything.
    // (radius, height, submesh of the band that ends at this point):
    // 0 = the paint's color (body and actuator), 1 = bare metal, 2 = label.
    static readonly (float r, float y, int sub)[] StationCanProfile =
    {
        (0f, -0.095f, 1),
        (0.029f, -0.095f, 1),
        (0.033f, -0.09f, 1),
        (0.033f, 0.03f, 0),
        (0.033f, 0.052f, 2),
        (0.033f, 0.06f, 0),
        (0.03f, 0.068f, 1),
        (0.022f, 0.074f, 1),
        (0.013f, 0.077f, 1),
        (0.012f, 0.08f, 1),
        (0.0085f, 0.08f, 1),
        (0.0085f, 0.092f, 0),
        (0f, 0.093f, 0),
    };

    // The cleaner: a solvent bottle, not a can, so it never reads as a
    // seventh color. 0 = bottle, 1 = cap, 2 = label.
    static readonly (float r, float y, int sub)[] StationCleanerProfile =
    {
        (0f, -0.095f, 0),
        (0.034f, -0.095f, 0),
        (0.036f, -0.089f, 0),
        (0.036f, -0.05f, 0),
        (0.036f, 0.02f, 2),
        (0.036f, 0.04f, 0),
        (0.03f, 0.058f, 0),
        (0.016f, 0.07f, 0),
        (0.013f, 0.074f, 0),
        (0.015f, 0.074f, 1),
        (0.015f, 0.093f, 1),
        (0f, 0.093f, 1),
    };

    // A wire ring 4mm wide, 5mm tall, around a can with 4.5mm to spare.
    static readonly (float r, float y, int sub)[] HolderRingProfile =
    {
        (0.0375f, 0f, 0),
        (0.0415f, 0f, 0),
        (0.0415f, 0.005f, 0),
        (0.0375f, 0.005f, 0),
        (0.0375f, 0f, 0),
    };

    static Vector3 StationSlotPosition(int cell)
    {
        var row = cell / StationColumns;
        var column = cell % StationColumns;
        return new Vector3((column - (StationColumns - 1) * 0.5f) * StationColumnSpacing, StationTopRowY - row * StationRowSpacing, 0f);
    }

    // Front (-Z) toward the player standing at the ring's centre. (The
    // earlier Euler(0, -angle, 0) turned the rack's long side toward the
    // player instead: its nearest swatch ended up ~25cm from them.)
    static Quaternion RingRotation(float angleDegrees) => Quaternion.Euler(0f, angleDegrees, 0f);

    // Rebuilds everything under the palette root except the color picker:
    // the old Stand/Swatch/SelectionMarker cubes and any station from a
    // previous run. Returns the slots in ColorManager.Palette order plus the
    // cleaner last - the order ColorPalette expects.
    static (Transform[] slots, Transform marker) BuildColorStation(Transform root)
    {
        // Everything under the palette root is rebuilt - including the V0.6-V0.9
        // HSV panel ("ColorPicker"), which the Color Orb replaced in V0.10.
        for (var i = root.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(root.GetChild(i).gameObject);

        var steel = CreateMaterial("RackStand", new Color(0.16f, 0.17f, 0.18f), 0.45f, 0.6f);
        var wire = CreateMaterial("RackWire", new Color(0.62f, 0.63f, 0.65f), 0.65f, 0.85f);
        var pegboard = CreateTexturedMaterial("Pegboard", BuildPegboardTexture(), new Vector2(24f, 23.2f), Color.white, 0.35f, 0.4f);
        var hazard = CreateTexturedMaterial("HazardTape", BuildHazardTexture(), new Vector2(7.5f, 1f), Color.white, 0.3f, 0f);

        // Frame: floor plate, two square-tube uprights, the pegboard between
        // them and a header bar on top. Solid, so a thrown can bounces off.
        MakeBox(root, "Base", new Vector3(0f, 0.015f, 0.03f), new Vector3(0.7f, 0.03f, 0.3f), steel, true);
        MakeBox(root, "Upright_L", new Vector3(-0.3175f, 0.7f, StationBoardZ), new Vector3(0.035f, 1.34f, 0.035f), steel, true);
        MakeBox(root, "Upright_R", new Vector3(0.3175f, 0.7f, StationBoardZ), new Vector3(0.035f, 1.34f, 0.035f), steel, true);
        MakeBox(root, "Pegboard", new Vector3(0f, 1.04f, StationBoardZ), new Vector3(0.6f, 0.58f, 0.01f), pegboard, true);
        MakeBox(root, "Header", new Vector3(0f, 1.35f, 0.058f), new Vector3(0.67f, 0.04f, 0.02f), steel, true);
        MakeBox(root, "HeaderTape", new Vector3(0f, 1.35f, 0.0475f), new Vector3(0.6f, 0.02f, 0.001f), hazard, false);

        var canMesh = SaveMeshAsset(BuildLatheMesh("StationCan", StationCanProfile, 3, 20), $"{GeneratedWorldFolder}/StationCan.asset");
        var cleanerMesh = SaveMeshAsset(BuildLatheMesh("StationCleaner", StationCleanerProfile, 3, 20), $"{GeneratedWorldFolder}/StationCleaner.asset");
        var canMetal = CreateMaterial("CanMetal", new Color(0.72f, 0.72f, 0.74f), 0.7f, 0.9f);
        var canLabel = CreateMaterial("CanLabel", new Color(0.88f, 0.86f, 0.8f), 0.3f);

        var slots = new Transform[ColorManager.Count + 1];
        for (var i = 0; i < ColorManager.Count; i++)
        {
            // Material 0 is the paint color: the one ColorPalette re-tints
            // from ColorManager.Palette at runtime, so the can on the rack
            // and the paint it loads can never disagree.
            var paint = CreateMaterial($"Swatch_{ColorManager.PaletteNames[i]}", ColorManager.Palette[i], 0.6f);
            slots[i] = MakeStationItem(root, $"Can_{ColorManager.PaletteNames[i]}", StationSlotPosition(i), canMesh, paint, canMetal, canLabel);
        }

        // Last cell of the colors' last row, a gap away from them.
        var cleanerCell = (ColorManager.Count - 1) / StationColumns * StationColumns + StationColumns - 1;
        if (cleanerCell < ColorManager.Count)
            cleanerCell = ColorManager.Count;

        slots[ColorManager.Count] = MakeStationItem(root, "Can_Cleaner", StationSlotPosition(cleanerCell), cleanerMesh,
            CreateMaterial("Swatch_Cleaner", new Color(0.55f, 0.75f, 0.85f), 0.45f),
            CreateMaterial("CleanerCap", new Color(0.08f, 0.08f, 0.09f), 0.35f),
            canLabel);

        BuildStationHolders(root, slots, wire);

        // The selected can is shown by a small lit tag on the pegboard under
        // it (V0.10 - the floating ring read as a debug marker in the headset).
        var marker = MakeBox(root, "SelectionMarker", slots[0].localPosition + StationMarkerOffset, new Vector3(0.046f, 0.007f, 0.003f),
            CreateUnlitMaterial("SelectionGlow", new Color(1f, 0.82f, 0.45f)), false);
        marker.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

        log.AppendLine($"Estacao de cores: painel perfurado com {ColorManager.Count} latas + frasco de limpeza em aneis de arame (1 renderer por lata, aneis em 1 malha), luz de selecao no painel sob a lata escolhida; frente virada para o jogador.");
        return (slots, marker.transform);
    }

    static Transform MakeStationItem(Transform root, string name, Vector3 position, Mesh mesh, params Material[] materials)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        go.transform.localPosition = position;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = materials;
        return go.transform;
    }

    // Every ring and its two wire arms back to the pegboard, as one mesh.
    static void BuildStationHolders(Transform root, Transform[] slots, Material wire)
    {
        var ring = BuildLatheMesh("HolderRing", HolderRingProfile, 1, 24);
        var cubeSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var cube = cubeSource.GetComponent<MeshFilter>().sharedMesh;

        var parts = new List<CombineInstance>();
        foreach (var slot in slots)
        {
            var ringPosition = slot.localPosition + Vector3.down * 0.035f;
            parts.Add(new CombineInstance { mesh = ring, transform = Matrix4x4.Translate(ringPosition) });
            for (var side = -1; side <= 1; side += 2)
            {
                parts.Add(new CombineInstance
                {
                    mesh = cube,
                    transform = Matrix4x4.TRS(ringPosition + new Vector3(side * 0.016f, 0.0025f, 0.0465f), Quaternion.identity, new Vector3(0.004f, 0.004f, 0.018f)),
                });
            }
        }

        var combined = new Mesh { name = "StationHolders" };
        combined.CombineMeshes(parts.ToArray(), true, true);
        Object.DestroyImmediate(cubeSource);
        Object.DestroyImmediate(ring);

        var holders = new GameObject("Holders");
        holders.transform.SetParent(root, false);
        holders.AddComponent<MeshFilter>().sharedMesh = SaveMeshAsset(combined, $"{GeneratedWorldFolder}/StationHolders.asset");
        var renderer = holders.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = wire;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
    }

    // ---------- V0.9 cap tray ----------

    // The cap rack's board becomes a shallow steel tray - lips on all four
    // sides and a strip of masking tape on the front (+Z, toward the
    // player). Visual only: no colliders on the lips, so reaching a parked
    // cap with the fingertips is exactly as before.
    static void DressCapTray(Transform capRoot, float width, float depth, float boardHeight)
    {
        var steel = CreateMaterial("RackStand", new Color(0.16f, 0.17f, 0.18f), 0.45f, 0.6f);
        var tape = CreateMaterial("MaskingTape", new Color(0.86f, 0.8f, 0.62f), 0.2f);
        const float lip = 0.008f;
        const float thickness = 0.003f;
        var lipY = boardHeight + lip * 0.5f;

        MakeBox(capRoot, "Lip_Front", new Vector3(0f, lipY, depth * 0.5f - thickness * 0.5f), new Vector3(width, lip, thickness), steel, false);
        MakeBox(capRoot, "Lip_Back", new Vector3(0f, lipY, -depth * 0.5f + thickness * 0.5f), new Vector3(width, lip, thickness), steel, false);
        MakeBox(capRoot, "Lip_Left", new Vector3(-width * 0.5f + thickness * 0.5f, lipY, 0f), new Vector3(thickness, lip, depth), steel, false);
        MakeBox(capRoot, "Lip_Right", new Vector3(width * 0.5f - thickness * 0.5f, lipY, 0f), new Vector3(thickness, lip, depth), steel, false);
        MakeBox(capRoot, "Tape", new Vector3(0f, boardHeight * 0.5f, depth * 0.5f + 0.0005f), new Vector3(width * 0.6f, 0.012f, 0.001f), tape, false);
    }

    // ---------- V0.9 room dressing ----------

    // A steel sheet over the tool table's top, flush with it (the can and
    // the cap tray rest on the table's own top, which does not move). The
    // table is a scaled cube, so the child's numbers are divided by its
    // size to come out in meters.
    static void DressToolTable(Transform table, Vector3 size)
    {
        var steel = CreateMaterial("TableSteel", new Color(0.5f, 0.51f, 0.53f), 0.55f, 0.7f);
        var sheet = new Vector3(size.x + 0.01f, 0.012f, size.z + 0.01f);
        var centerY = size.y * 0.5f - sheet.y * 0.5f + 0.0005f;
        MakeBox(table, "SteelTop", new Vector3(0f, centerY / size.y, 0f), new Vector3(sheet.x / size.x, sheet.y / size.y, sheet.z / size.z), steel, false);
    }

    static readonly RectInt PosterGigRegion = new RectInt(0, 0, 128, 192);
    static readonly RectInt PosterHalftoneRegion = new RectInt(128, 0, 128, 192);
    static readonly RectInt StickerHelloRegion = new RectInt(256, 0, 128, 88);
    static readonly RectInt StickerTargetRegion = new RectInt(384, 0, 64, 64);
    static readonly RectInt StickerSlapRegion = new RectInt(384, 64, 128, 64);
    const int StreetAtlasWidth = 512;
    const int StreetAtlasHeight = 256;

    // Two posters on the back wall beside the door and a few stickers on the
    // door and the bin. Children of UrbanProps: static like the rest of the
    // props, and in the same place relative to the back wall.
    static void BuildStreetPaper(Transform propsRoot, RoomLayoutProfile layout, Vector3 binCenter, float binRadius)
    {
        var paperRoot = FindOrCreate(propsRoot, "StreetPaper", Vector3.zero).transform;
        paperRoot.localPosition = Vector3.zero;
        paperRoot.localRotation = Quaternion.identity;
        for (var i = paperRoot.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(paperRoot.GetChild(i).gameObject);

        var material = CreateTexturedMaterial("StreetPaper", BuildStreetPaperAtlas(), Vector2.one, Color.white, 0.12f, 0f);
        var gig = SaveMeshAsset(BuildAtlasQuad("Street_PosterGig", PosterGigRegion, 0.42f), $"{GeneratedWorldFolder}/Street_PosterGig.asset");
        var halftone = SaveMeshAsset(BuildAtlasQuad("Street_PosterHalftone", PosterHalftoneRegion, 0.42f), $"{GeneratedWorldFolder}/Street_PosterHalftone.asset");
        var hello = SaveMeshAsset(BuildAtlasQuad("Street_StickerHello", StickerHelloRegion, 0.1f), $"{GeneratedWorldFolder}/Street_StickerHello.asset");
        var target = SaveMeshAsset(BuildAtlasQuad("Street_StickerTarget", StickerTargetRegion, 0.075f), $"{GeneratedWorldFolder}/Street_StickerTarget.asset");
        var slap = SaveMeshAsset(BuildAtlasQuad("Street_StickerSlap", StickerSlapRegion, 0.11f), $"{GeneratedWorldFolder}/Street_StickerSlap.asset");

        var wallFace = -layout.roomHalfDepth + 0.1f;
        var doorFace = wallFace + MetalDoorSize.z;
        PlaceStreetPaper(paperRoot, "Poster_Gig", gig, material, new Vector3(-1.02f, 1.45f, wallFace), Vector3.forward, 1.5f);
        PlaceStreetPaper(paperRoot, "Poster_Halftone", halftone, material, new Vector3(0.92f, 1.38f, wallFace), Vector3.forward, -2f);
        PlaceStreetPaper(paperRoot, "Sticker_Hello", hello, material, new Vector3(0.2f, 1.32f, doorFace), Vector3.forward, -6f);
        PlaceStreetPaper(paperRoot, "Sticker_Splat", target, material, new Vector3(-0.24f, 1.6f, doorFace), Vector3.forward, 0f);
        PlaceStreetPaper(paperRoot, "Sticker_Slap", slap, material, new Vector3(-0.17f, 0.95f, doorFace), Vector3.forward, 4f);

        // On the bin, on the side that faces the player.
        var towardPlayer = new Vector3(-binCenter.x, 0f, -binCenter.z).normalized;
        PlaceStreetPaper(paperRoot, "Sticker_Bin", hello, material, binCenter + towardPlayer * binRadius, towardPlayer, 3f);

        log.AppendLine("Set dressing: 2 cartazes na parede do fundo + 4 adesivos (porta, lixeira), 1 atlas 512x256 e 1 material para todos.");
    }

    static void PlaceStreetPaper(Transform parent, string name, Mesh mesh, Material material, Vector3 surfacePoint, Vector3 outward, float rollDegrees)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = surfacePoint + outward * 0.002f;
        go.transform.localRotation = Quaternion.LookRotation(-outward, Vector3.up) * Quaternion.Euler(0f, 0f, rollDegrees);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
    }

    // ---------- V0.9 generated meshes ----------

    // Surface of revolution around +Y. `profile` runs along the outside
    // from bottom to top (radius, height); each consecutive pair is one band
    // in the submesh the second point names. Bands get their own vertices,
    // so the edges between them stay sharp (a rim, a cap's top) while each
    // band is smooth around.
    static Mesh BuildLatheMesh(string name, (float r, float y, int sub)[] profile, int submeshCount, int sides)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>[submeshCount];
        for (var s = 0; s < submeshCount; s++)
            triangles[s] = new List<int>();

        for (var b = 1; b < profile.Length; b++)
        {
            var from = new Vector2(profile[b - 1].r, profile[b - 1].y);
            var to = new Vector2(profile[b].r, profile[b].y);
            if ((to - from).sqrMagnitude < 1e-12f)
                continue;

            // Outward normal of the band in the (radius, height) half-plane.
            var n = new Vector2(to.y - from.y, -(to.x - from.x)).normalized;
            var start = positions.Count;
            for (var i = 0; i < sides; i++)
            {
                var angle = i * Mathf.PI * 2f / sides;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                var normal = (direction * n.x + Vector3.up * n.y).normalized;
                positions.Add(direction * from.x + Vector3.up * from.y);
                positions.Add(direction * to.x + Vector3.up * to.y);
                normals.Add(normal);
                normals.Add(normal);
            }

            var list = triangles[profile[b].sub];
            for (var i = 0; i < sides; i++)
            {
                var p = start + i * 2;
                var q = start + (i + 1) % sides * 2;
                var outward = normals[p] + normals[q];
                AddFacing(list, positions, p, p + 1, q + 1, outward);
                AddFacing(list, positions, p, q + 1, q, outward);
            }
        }

        var mesh = new Mesh { name = name };
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.subMeshCount = submeshCount;
        for (var s = 0; s < submeshCount; s++)
            mesh.SetTriangles(triangles[s], s);
        mesh.RecalculateBounds();
        return mesh;
    }

    // A quad (visible from -Z) showing one region of the street-paper atlas,
    // `width` meters wide with the region's own aspect.
    static Mesh BuildAtlasQuad(string name, RectInt region, float width)
    {
        var height = width * region.height / region.width;
        var positions = new List<Vector3>
        {
            new Vector3(-width * 0.5f, -height * 0.5f, 0f),
            new Vector3(width * 0.5f, -height * 0.5f, 0f),
            new Vector3(-width * 0.5f, height * 0.5f, 0f),
            new Vector3(width * 0.5f, height * 0.5f, 0f),
        };

        // Half a texel in, so filtering never reaches the neighbour region.
        var u0 = (region.xMin + 0.5f) / StreetAtlasWidth;
        var u1 = (region.xMax - 0.5f) / StreetAtlasWidth;
        var v0 = (region.yMin + 0.5f) / StreetAtlasHeight;
        var v1 = (region.yMax - 0.5f) / StreetAtlasHeight;

        var triangles = new List<int>();
        AddFacing(triangles, positions, 0, 3, 1, Vector3.back);
        AddFacing(triangles, positions, 0, 2, 3, Vector3.back);

        var mesh = new Mesh { name = name };
        mesh.SetVertices(positions);
        mesh.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
        mesh.SetUVs(0, new List<Vector2> { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1) });
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Winds a triangle so its front face points along `outward` (Unity
    // front faces satisfy Cross(b - a, c - a) . normal > 0); degenerate
    // triangles (the centre of a disc band) are dropped.
    static void AddFacing(List<int> triangles, List<Vector3> positions, int a, int b, int c, Vector3 outward)
    {
        var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
        if (cross.sqrMagnitude < 1e-18f)
            return;

        if (Vector3.Dot(cross, outward) < 0f)
            (b, c) = (c, b);

        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    // ---------- V0.9 generated textures ----------

    static Material CreateTexturedMaterial(string name, Texture2D texture, Vector2 tiling, Color tint, float smoothness, float metallic)
    {
        var material = CreateMaterial(name, tint, smoothness, metallic);
        if (material == null)
            return null;

        material.SetTexture("_BaseMap", texture);
        material.SetTextureScale("_BaseMap", tiling);
        EditorUtility.SetDirty(material);
        return material;
    }

    // Written only when the bytes change, so a re-run does not reimport.
    // Import settings come from GeneratedWorldTextureImport, below, inside
    // the one import this makes.
    static Texture2D SaveGeneratedTexture(string path, Raster raster)
    {
        EnsureFolder("Assets/Art", "Generated");
        EnsureFolder("Assets/Art/Generated", "World");

        var temp = new Texture2D(raster.Width, raster.Height, TextureFormat.RGBA32, false);
        temp.SetPixels32(raster.ToColor32());
        temp.Apply();
        var png = temp.EncodeToPNG();
        Object.DestroyImmediate(temp);

        var fullPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, path);
        if (!File.Exists(fullPath) || !SameBytes(File.ReadAllBytes(fullPath), png))
        {
            File.WriteAllBytes(fullPath, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // Cast concrete, one 1.2m slab per tile: two octaves of blotches, fine
    // grain, a few darker stains and the saw-cut joint along two edges (so
    // tiles meet on one joint line).
    static Texture2D BuildConcreteTexture()
    {
        const int size = 128;
        var rng = new System.Random(1201);
        var noise = new TileNoise(rng, 4, 8, 16, 32);
        var stains = new TileNoise(rng, 3, 6);
        var raster = new Raster(size, size, Color.gray);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size;
                var v = (y + 0.5f) / size;
                var g = 0.5f + (noise.Sample(u, v) - 0.5f) * 0.22f;
                g *= Mathf.Lerp(1f, 0.82f, Mathf.InverseLerp(0.6f, 0.78f, stains.Sample(u, v)));
                g += ((float)rng.NextDouble() - 0.5f) * 0.05f;
                if (x == 0 || y == 0)
                    g *= 0.45f;
                else if (x == 1 || y == 1)
                    g *= 0.8f;

                raster.Set(x, y, new Color(g, g * 0.99f, g * 0.96f));
            }
        }

        return SaveGeneratedTexture($"{GeneratedWorldFolder}/Concrete.png", raster);
    }

    // Concrete block wall, 0.8m x 0.8m per tile: 400x200mm blocks in
    // running bond, 10mm mortar joints with a shadow line under each course.
    static Texture2D BuildBlockTexture()
    {
        const int size = 128;
        const int blockWidth = 64;
        const int blockHeight = 32;
        var rng = new System.Random(4020);
        var noise = new TileNoise(rng, 8, 16, 32);
        var shades = new float[8];
        for (var i = 0; i < shades.Length; i++)
            shades[i] = ((float)rng.NextDouble() - 0.5f) * 0.06f;

        var raster = new Raster(size, size, Color.gray);
        for (var y = 0; y < size; y++)
        {
            var course = y / blockHeight;
            var inCourse = y % blockHeight;
            var offset = course % 2 == 1 ? blockWidth / 2 : 0;

            for (var x = 0; x < size; x++)
            {
                var shifted = (x + offset) % size;
                var inBlock = shifted % blockWidth;
                var block = course * 2 + shifted / blockWidth;
                var u = (x + 0.5f) / size;
                var v = (y + 0.5f) / size;

                float g;
                if (inCourse < 2 || inBlock < 2)
                    g = 0.56f + (noise.Sample(u, v) - 0.5f) * 0.08f;
                else
                {
                    g = 0.47f + shades[block % shades.Length] + (noise.Sample(u, v) - 0.5f) * 0.14f;
                    g += ((float)rng.NextDouble() - 0.5f) * 0.06f;
                    if (inCourse == 2)
                        g *= 0.78f;
                }

                raster.Set(x, y, new Color(g, g * 0.99f, g * 0.97f));
            }
        }

        return SaveGeneratedTexture($"{GeneratedWorldFolder}/Blocks.png", raster);
    }

    // Galvanized pegboard: one 6mm hole per 25mm tile.
    static Texture2D BuildPegboardTexture()
    {
        var raster = new Raster(32, 32, new Color(0.56f, 0.57f, 0.58f));
        raster.Disc(16f, 16f, 5.2f, new Color(0.66f, 0.67f, 0.68f));
        raster.Disc(16f, 16f, 3.9f, new Color(0.05f, 0.05f, 0.06f));
        return SaveGeneratedTexture($"{GeneratedWorldFolder}/Pegboard.png", raster);
    }

    // Yellow/black diagonal hazard stripes, tiling along the strip.
    static Texture2D BuildHazardTexture()
    {
        var raster = new Raster(64, 16, Color.black);
        var yellow = new Color(0.95f, 0.74f, 0.1f);
        var black = new Color(0.07f, 0.07f, 0.07f);
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 64; x++)
                raster.Set(x, y, (x + y) % 32 < 16 ? yellow : black);

        return SaveGeneratedTexture($"{GeneratedWorldFolder}/HazardTape.png", raster);
    }

    static Texture2D BuildStreetPaperAtlas()
    {
        var rng = new System.Random(9051);
        var raster = new Raster(StreetAtlasWidth, StreetAtlasHeight, new Color(0.1f, 0.1f, 0.1f));
        DrawGigPoster(raster, PosterGigRegion, rng);
        DrawHalftonePoster(raster, PosterHalftoneRegion, rng);
        DrawHelloSticker(raster, StickerHelloRegion, rng);
        DrawSplatSticker(raster, StickerTargetRegion, rng);
        DrawSlapSticker(raster, StickerSlapRegion, rng);
        return SaveGeneratedTexture($"{GeneratedWorldFolder}/StreetPaper.png", raster);
    }

    // A gig poster: bold headline, a red record-like disc, a yellow band of
    // small print. Blocks stand in for type - from arm's length it reads as
    // a poster, and there is no font to ship.
    static void DrawGigPoster(Raster r, RectInt g, System.Random rng)
    {
        var paper = new Color(0.92f, 0.89f, 0.8f);
        var ink = new Color(0.08f, 0.08f, 0.09f);
        var red = new Color(0.84f, 0.16f, 0.12f);
        var yellow = new Color(0.96f, 0.78f, 0.12f);

        r.Clip = g;
        r.Rect(g.x, g.y, g.xMax, g.yMax, paper);
        FakeWords(r, g.x + 10, g.y + 164, g.x + 118, 18, 2, ink, paper, rng);
        // A paint splat with drips (V0.10: a red disc in a black ring read
        // as a target/debug marker in the headset).
        Splat(r, g.x + 64, g.y + 112, 40f, red, rng, drips: 3);
        FakeWords(r, g.x + 16, g.y + 50, g.x + 112, 9, 3, ink, paper, rng);
        r.Rect(g.x, g.y + 8, g.xMax, g.y + 42, yellow);
        for (var line = 0; line < 3; line++)
            FakeWords(r, g.x + 10, g.y + 32 - line * 9, g.x + 118, 4, 6, ink, yellow, rng);

        r.Grain(g, 0.05f, rng);
        r.Vignette(g, 0.25f);
    }

    // Black poster with a cyan halftone gradient and a stencil headline.
    static void DrawHalftonePoster(Raster r, RectInt g, System.Random rng)
    {
        var black = new Color(0.07f, 0.07f, 0.08f);
        var cyan = new Color(0.1f, 0.72f, 0.82f);
        var white = new Color(0.94f, 0.94f, 0.9f);
        var magenta = new Color(0.9f, 0.2f, 0.55f);

        r.Clip = g;
        r.Rect(g.x, g.y, g.xMax, g.yMax, black);
        for (var row = 0; row * 7 + 44 < 150; row++)
        {
            var y = 44 + row * 7;
            var radius = Mathf.Lerp(0.5f, 3.6f, (y - 44) / 106f);
            for (var x = 4f + row % 2 * 3.5f; x < 128f; x += 7f)
                r.Disc(g.x + x, g.y + y, radius, cyan);
        }

        FakeWords(r, g.x + 8, g.y + 158, g.x + 120, 22, 2, white, black, rng);
        r.Rect(g.x + 8, g.y + 20, g.x + 120, g.y + 34, magenta);
        FakeWords(r, g.x + 14, g.y + 24, g.x + 114, 6, 4, black, magenta, rng);
        FakeWords(r, g.x + 8, g.y + 8, g.x + 80, 4, 3, white, black, rng);

        r.Grain(g, 0.05f, rng);
        r.Vignette(g, 0.2f);
    }

    // "HELLO my name is" with a marker tag in the white panel.
    static void DrawHelloSticker(Raster r, RectInt g, System.Random rng)
    {
        var red = new Color(0.82f, 0.1f, 0.12f);
        var white = new Color(0.97f, 0.97f, 0.95f);
        var ink = new Color(0.05f, 0.05f, 0.06f);

        r.Clip = g;
        r.Rect(g.x, g.y, g.xMax, g.yMax, red);
        FakeWords(r, g.x + 38, g.y + 68, g.x + 90, 14, 1, white, red, rng);
        FakeWords(r, g.x + 40, g.y + 57, g.x + 88, 5, 3, white, red, rng);
        r.Rect(g.x + 6, g.y + 8, g.xMax - 6, g.y + 50, white);

        // Catmull-Rom through jittered points, left to right: a tag.
        var points = new List<Vector2>();
        for (var i = 0; i < 9; i++)
            points.Add(new Vector2(g.x + 16 + i * 12 + (float)rng.NextDouble() * 6f, g.y + 16 + (float)rng.NextDouble() * 26f));

        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Mathf.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Mathf.Min(points.Count - 1, i + 2)];
            var previous = p1;
            for (var s = 1; s <= 8; s++)
            {
                var t = s / 8f;
                var point = 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t + (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t);
                r.Stroke(previous, point, 3f, ink);
                previous = point;
            }
        }

        r.Stroke(new Vector2(g.x + 14, g.y + 13), new Vector2(g.x + 114, g.y + 17), 2f, ink);
        r.Grain(g, 0.03f, rng);
    }

    // A black paint splat on a white sticker.
    static void DrawSplatSticker(Raster r, RectInt g, System.Random rng)
    {
        r.Clip = g;
        r.Rect(g.x, g.y, g.xMax, g.yMax, new Color(0.96f, 0.96f, 0.94f));
        Splat(r, g.x + 32, g.y + 36, 19f, new Color(0.07f, 0.07f, 0.08f), rng, drips: 2);
        r.Grain(g, 0.03f, rng);
    }

    // An irregular blob of paint: a core, lobes around it, flung droplets,
    // and drips running down from its lower edge.
    static void Splat(Raster r, float cx, float cy, float radius, Color color, System.Random rng, int drips)
    {
        r.Disc(cx, cy, radius * 0.68f, color);
        for (var i = 0; i < 9; i++)
        {
            var angle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var distance = radius * (0.45f + (float)rng.NextDouble() * 0.4f);
            r.Disc(cx + Mathf.Cos(angle) * distance, cy + Mathf.Sin(angle) * distance, radius * (0.16f + (float)rng.NextDouble() * 0.2f), color);
        }

        for (var i = 0; i < 11; i++)
        {
            var angle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var distance = radius * (1.0f + (float)rng.NextDouble() * 0.6f);
            r.Disc(cx + Mathf.Cos(angle) * distance, cy + Mathf.Sin(angle) * distance, radius * (0.04f + (float)rng.NextDouble() * 0.08f), color);
        }

        for (var i = 0; i < drips; i++)
        {
            var x = cx + ((float)rng.NextDouble() - 0.5f) * radius * 1.1f;
            var top = cy - radius * 0.4f;
            var length = radius * (0.6f + (float)rng.NextDouble() * 0.9f);
            var width = radius * (0.1f + (float)rng.NextDouble() * 0.08f);
            r.Stroke(new Vector2(x, top), new Vector2(x, top - length), width, color);
            r.Disc(x, top - length, width * 0.75f, color);
        }
    }

    static void DrawSlapSticker(Raster r, RectInt g, System.Random rng)
    {
        var blue = new Color(0.12f, 0.3f, 0.75f);
        var white = new Color(0.96f, 0.96f, 0.94f);
        var yellow = new Color(0.96f, 0.78f, 0.12f);

        r.Clip = g;
        r.Rect(g.x, g.y, g.xMax, g.yMax, white);
        r.Rect(g.x + 3, g.y + 3, g.xMax - 3, g.yMax - 3, blue);
        r.Stroke(new Vector2(g.x, g.y + 8), new Vector2(g.xMax, g.yMax - 8), 9f, white);
        FakeWords(r, g.x + 12, g.y + 38, g.x + 70, 14, 1, yellow, blue, rng);
        FakeWords(r, g.x + 60, g.y + 12, g.x + 116, 8, 2, yellow, blue, rng);
        r.Grain(g, 0.03f, rng);
    }

    // Blocks shaped like a line of bold type: `count` words across
    // [x0, x1], each split into letters, some with a counter knocked out.
    static void FakeWords(Raster r, float x0, float y, float x1, float height, int count, Color ink, Color background, System.Random rng)
    {
        var wordGap = Mathf.Max(2f, height * 0.4f);
        var weights = new float[count];
        var total = 0f;
        for (var i = 0; i < count; i++)
        {
            weights[i] = 0.6f + (float)rng.NextDouble();
            total += weights[i];
        }

        var available = x1 - x0 - wordGap * (count - 1);
        var x = x0;
        for (var i = 0; i < count; i++)
        {
            var wordWidth = available * weights[i] / total;
            var letterGap = Mathf.Max(1f, height * 0.12f);
            var letters = Mathf.Max(1, Mathf.RoundToInt((wordWidth + letterGap) / (height * 0.62f + letterGap)));
            var letterWidth = (wordWidth - letterGap * (letters - 1)) / letters;

            for (var l = 0; l < letters; l++)
            {
                var lx = x + l * (letterWidth + letterGap);
                r.Rect(lx, y, lx + letterWidth, y + height, ink);
                if (height >= 8f && rng.NextDouble() < 0.6)
                    r.Rect(lx + letterWidth * 0.35f, y + height * 0.3f, lx + letterWidth * 0.65f, y + height * 0.62f, background);
            }

            x += wordWidth + wordGap;
        }
    }

    // Value noise on wrapping lattices (one per octave), so a texture made
    // from it tiles without a seam. Octaves are summed with halving weight.
    sealed class TileNoise
    {
        readonly int[] cells;
        readonly float[][] lattices;

        public TileNoise(System.Random rng, params int[] octaveCells)
        {
            cells = octaveCells;
            lattices = new float[octaveCells.Length][];
            for (var o = 0; o < octaveCells.Length; o++)
            {
                lattices[o] = new float[octaveCells[o] * octaveCells[o]];
                for (var i = 0; i < lattices[o].Length; i++)
                    lattices[o][i] = (float)rng.NextDouble();
            }
        }

        public float Sample(float u, float v)
        {
            var sum = 0f;
            var weight = 0.5f;
            var total = 0f;
            for (var o = 0; o < cells.Length; o++)
            {
                sum += Octave(o, u, v) * weight;
                total += weight;
                weight *= 0.5f;
            }

            return sum / total;
        }

        float Octave(int o, float u, float v)
        {
            var n = cells[o];
            var x = u * n;
            var y = v * n;
            var x0 = Mathf.FloorToInt(x);
            var y0 = Mathf.FloorToInt(y);
            var fx = x - x0;
            var fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            float At(int i, int j) => lattices[o][(j % n + n) % n * n + (i % n + n) % n];
            return Mathf.Lerp(Mathf.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Mathf.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
        }
    }

    // A tiny software canvas for the generated textures: rectangles, discs,
    // rings and strokes with 1px anti-aliased edges, clipped to `Clip`.
    sealed class Raster
    {
        public readonly int Width;
        public readonly int Height;
        readonly Color[] pixels;
        public RectInt Clip;

        public Raster(int width, int height, Color fill)
        {
            Width = width;
            Height = height;
            Clip = new RectInt(0, 0, width, height);
            pixels = new Color[width * height];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = fill;
        }

        public void Set(int x, int y, Color c) => pixels[y * Width + x] = c;

        public void Rect(float x0, float y0, float x1, float y1, Color c)
        {
            for (var y = Mathf.Max(Clip.yMin, Mathf.RoundToInt(y0)); y < Mathf.Min(Clip.yMax, Mathf.RoundToInt(y1)); y++)
                for (var x = Mathf.Max(Clip.xMin, Mathf.RoundToInt(x0)); x < Mathf.Min(Clip.xMax, Mathf.RoundToInt(x1)); x++)
                    pixels[y * Width + x] = c;
        }

        public void Disc(float cx, float cy, float radius, Color c) => Ring(cx, cy, 0f, radius, c);

        public void Ring(float cx, float cy, float inner, float outer, Color c)
        {
            ForBox(cx - outer - 1f, cy - outer - 1f, cx + outer + 1f, cy + outer + 1f, (x, y) =>
            {
                var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                var coverage = Mathf.Clamp01(outer - d + 0.5f);
                if (inner > 0f)
                    coverage *= Mathf.Clamp01(d - inner + 0.5f);
                return coverage;
            }, c);
        }

        public void Stroke(Vector2 a, Vector2 b, float width, Color c)
        {
            var half = width * 0.5f;
            ForBox(Mathf.Min(a.x, b.x) - half - 1f, Mathf.Min(a.y, b.y) - half - 1f, Mathf.Max(a.x, b.x) + half + 1f, Mathf.Max(a.y, b.y) + half + 1f, (x, y) =>
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var ab = b - a;
                var t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                return Mathf.Clamp01(half - Vector2.Distance(p, a + ab * t) + 0.5f);
            }, c);
        }

        public void Grain(RectInt region, float amount, System.Random rng)
        {
            for (var y = region.yMin; y < region.yMax; y++)
            {
                for (var x = region.xMin; x < region.xMax; x++)
                {
                    var k = 1f + ((float)rng.NextDouble() - 0.5f) * 2f * amount;
                    var p = pixels[y * Width + x];
                    pixels[y * Width + x] = new Color(p.r * k, p.g * k, p.b * k);
                }
            }
        }

        // Darkens toward the region's edges, like paper that has been
        // outside for a while.
        public void Vignette(RectInt region, float amount)
        {
            var margin = Mathf.Min(region.width, region.height) * 0.18f;
            for (var y = region.yMin; y < region.yMax; y++)
            {
                for (var x = region.xMin; x < region.xMax; x++)
                {
                    var edge = Mathf.Min(Mathf.Min(x - region.xMin, region.xMax - 1 - x), Mathf.Min(y - region.yMin, region.yMax - 1 - y));
                    var k = 1f - amount * (1f - Mathf.Clamp01(edge / margin));
                    var p = pixels[y * Width + x];
                    pixels[y * Width + x] = new Color(p.r * k, p.g * k, p.b * k);
                }
            }
        }

        public Color32[] ToColor32()
        {
            var result = new Color32[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                result[i] = new Color(Mathf.Clamp01(p.r), Mathf.Clamp01(p.g), Mathf.Clamp01(p.b), 1f);
            }

            return result;
        }

        void ForBox(float x0, float y0, float x1, float y1, System.Func<int, int, float> coverage, Color c)
        {
            for (var y = Mathf.Max(Clip.yMin, Mathf.FloorToInt(y0)); y < Mathf.Min(Clip.yMax, Mathf.CeilToInt(y1)); y++)
            {
                for (var x = Mathf.Max(Clip.xMin, Mathf.FloorToInt(x0)); x < Mathf.Min(Clip.xMax, Mathf.CeilToInt(x1)); x++)
                {
                    var a = coverage(x, y);
                    if (a > 0f)
                        pixels[y * Width + x] = Color.Lerp(pixels[y * Width + x], c, a);
                }
            }
        }
    }

    // ---------- V0.9 validation ----------

    static void ValidateV09(System.Action<bool, string, string> check)
    {
        // Can physics: a convex hull that rolls and bounces, and the tool's
        // finger wiring.
        var sprayCan = GameObject.Find("SprayCan");
        if (sprayCan != null)
        {
            var hull = sprayCan.GetComponent<MeshCollider>();
            check(hull != null && hull.convex && hull.sharedMesh != null && !hull.isTrigger, "Lata: collider convexo",
                hull == null ? "ausente" : $"convex={hull.convex}, mesh={(hull.sharedMesh != null ? hull.sharedMesh.name : "NULA")}, trigger={hull.isTrigger}");
            check(hull != null && hull.sharedMaterial != null, "Lata: material fisico", hull != null && hull.sharedMaterial != null ? hull.sharedMaterial.name : "ausente");
            check(sprayCan.GetComponent<BoxCollider>() == null && sprayCan.GetComponent<CapsuleCollider>() == null, "Lata: sem collider antigo", "Box/Capsule removidos");

            // Hand polish: the spray is kept clear by the holding fingers, and
            // every cap's mesh can be read (the index finds the cap's real top
            // from it).
            var canGrabInteractable = sprayCan.GetComponent<VRGrabInteractable>();
            var keepClear = canGrabInteractable != null ? new SerializedObject(canGrabInteractable).FindProperty("keepClearOf")?.objectReferenceValue as Transform : null;
            check(keepClear != null && keepClear.name == "SprayOrigin", "Lata: dedos livres do jato", keepClear != null ? $"keepClearOf = '{keepClear.name}'" : "keepClearOf NULO - dedos podem ficar na frente do spray");
            foreach (var cap in Object.FindObjectsByType<PhysicalCap>(FindObjectsInactive.Include))
            {
                var mesh = cap.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                check(mesh != null && mesh.isReadable, $"Cap '{cap.name}': malha legivel", mesh == null ? "sem malha" : (mesh.isReadable ? "sim (topo real para o indicador)" : "NAO - o indicador nao encontra o topo"));
            }

            foreach (var pose in Object.FindObjectsByType<HandBonePoseController>(FindObjectsInactive.Include))
            {
                var so = new SerializedObject(pose);
                var abduction = so.FindProperty("indexAbduction")?.floatValue ?? 0f;
                var allowance = so.FindProperty("fistShoulderAllowance")?.floatValue ?? 0f;
                check(Mathf.Approximately(abduction, 30f) && Mathf.Approximately(allowance, 0.016f), $"{pose.name}: pegada da lata", $"abducao do indicador {abduction:F0} graus, medio ate {allowance * 1000f:F0}mm sobre o ombro");
            }

            var nozzle = sprayCan.GetComponent<NozzleAnimator>();
            var canGrab = nozzle != null ? new SerializedObject(nozzle).FindProperty("canGrab")?.objectReferenceValue : null;
            check(canGrab != null && canGrab == sprayCan.GetComponent<VRGrabInteractable>(), "NozzleAnimator.canGrab", canGrab != null ? "ligado ao grab da lata (FingerApproach)" : "NULO - o indicador nao sai do repouso");
        }

        foreach (var grabber in Object.FindObjectsByType<VRGrabber>(FindObjectsInactive.Include))
        {
            var rig = new SerializedObject(grabber).FindProperty("handRig")?.objectReferenceValue;
            check(rig != null, $"{grabber.name} VRGrabber.handRig", rig != null ? "ligado (alcance pelas pontas dos dedos para caps)" : "NULO - caps so pela palma");
        }

        // Drips convert gravity through the hit triangle's UVs: each paint
        // surface's collider mesh must be readable on the device.
        foreach (var surface in Object.FindObjectsByType<Freehand.Paint.PaintSurface>(FindObjectsInactive.Include))
        {
            var mesh = surface.TryGetComponent<MeshCollider>(out var meshCollider) ? meshCollider.sharedMesh : null;
            check(mesh != null && mesh.isReadable, $"{surface.SurfaceId} malha legivel", mesh == null ? "sem malha" : (mesh.isReadable ? $"'{mesh.name}' legivel (escorrimento com gravidade)" : "NAO legivel - escorrimento desativado"));
        }

        // Color station.
        var palette = Object.FindAnyObjectByType<ColorPalette>(FindObjectsInactive.Include);
        if (palette != null)
        {
            var so = new SerializedObject(palette);
            var slots = so.FindProperty("slots");
            var count = slots != null ? slots.arraySize : 0;
            check(count == ColorManager.Count + 1, "Estacao: slots", $"{count} (esperado {ColorManager.Count} cores + limpeza)");

            var colorsOk = true;
            for (var i = 0; i < Mathf.Min(count, ColorManager.Count); i++)
            {
                var slot = slots.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                var renderer = slot != null ? slot.GetComponent<MeshRenderer>() : null;
                var material = renderer != null ? renderer.sharedMaterial : null;
                colorsOk &= material != null && material.GetColor("_BaseColor") == ColorManager.Palette[i];
            }

            check(colorsOk, "Estacao: cor de cada lata", colorsOk ? "material 0 = ColorManager.Palette na mesma ordem" : "lata sem renderer ou com cor diferente da paleta");
            check(so.FindProperty("selectionMarker")?.objectReferenceValue != null, "Estacao: marcador", "ligado");

            var toPlayer = (Vector3.zero - palette.transform.position).normalized;
            toPlayer.y = 0f;
            var facing = Vector3.Dot(-palette.transform.forward, toPlayer.normalized);
            check(facing > 0.95f, "Estacao: de frente para o jogador", $"cos={facing:F2}");
        }
    }
}

// Import settings for the generated world textures, applied inside their
// first import. (Setting them on the importer afterwards meant importing a
// just-imported asset a second time, which Unity reports as "generated
// inconsistent result".) Color, mipmapped, no alpha - all of them are
// opaque; the street-paper atlas is placed by UV rect, so it clamps, the
// rest tile.
class GeneratedWorldTextureImport : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith("Assets/Art/Generated/World/"))
            return;

        var importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Default;
        importer.mipmapEnabled = true;
        importer.alphaSource = TextureImporterAlphaSource.None;
        importer.wrapMode = assetPath.EndsWith("/StreetPaper.png") ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
    }
}
