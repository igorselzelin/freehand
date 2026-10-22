using System.Collections.Generic;
using System.IO;
using Freehand.Audio;
using Freehand.Customization;
using Freehand.Environment;
using Freehand.Interaction;
using Freehand.Paint;
using Freehand.Spray;
using Freehand.Tools;
using Freehand.VR;
using UnityEditor;
using UnityEngine;

// V0.12: the Freehand can's own look, the can workshop, the player's skin.
// Idempotent: textures are rewritten only when their bytes change, materials
// are assets updated in place, the workshop's children are found by name,
// the workshop can is rebuilt from the player's can every run.
public static partial class V04SceneBuilder
{
    const string CanBasePath = GeneratedCanFolder + "/FreehandCan_Base.png";
    const string CanMetalPath = GeneratedCanFolder + "/FreehandCan_Metal.png";
    const string CanMaterialsFolder = "Assets/Materials/Can";
    const string WorkshopTexturesFolder = GeneratedCanFolder + "/Workshop";
    const int CanTextureSize = 256;
    const int CanMetalSize = 128;

    // Measured from the body mesh: where its side is in the texture, how
    // densely, and which way round.
    struct CanPaintLayout
    {
        public Rect Side;
        public float UPerMeter;
        public float VPerMeter;
        public float BackU;        // u of the body side facing -Z (the hand / player)
        public bool UIncreasesRight;
    }

    static void BuildV012(GameObject sprayCan, GameObject room, GameObject left, GameObject right, GameObject xrOrigin, InputActionAssetRef inputActions, RoomLayoutProfile layout)
    {
        var body = FindDeep(sprayCan.transform, "sm_SprayBottle1");
        var valve = FindDeep(sprayCan.transform, "ValveCup");
        var bodyRenderer = body != null ? body.GetComponent<MeshRenderer>() : null;
        var bodyMesh = body != null ? body.GetComponent<MeshFilter>().sharedMesh : null;
        if (bodyRenderer == null || bodyMesh == null || !bodyMesh.isReadable)
        {
            log.AppendLine("ERRO V0.12: corpo da lata (sm_SprayBottle1, legivel) nao encontrado - oficina nao construida.");
            return;
        }

        var canLayout = MeasureCanPaintLayout(sprayCan.transform, body, bodyMesh);
        var valveMesh = valve != null ? valve.GetComponent<MeshFilter>().sharedMesh : null;
        var standardCap = sprayCan.GetComponentInChildren<PhysicalCap>(true);
        var capMesh = standardCap != null ? standardCap.GetComponent<MeshFilter>().sharedMesh : null;
        BuildCanLookTextures(sprayCan.transform, body, bodyMesh, valve, valveMesh, standardCap != null ? standardCap.transform : null, capMesh, canLayout, out var baseTexture, out var metalTexture);

        BuildCanMaterials(baseTexture, metalTexture, out var bodyMaterial, out var capMaterial, out var workshopMaterial);
        bodyRenderer.sharedMaterial = bodyMaterial;
        if (valve != null)
            valve.GetComponent<MeshRenderer>().sharedMaterial = bodyMaterial;
        if (standardCap != null)
            standardCap.GetComponent<MeshRenderer>().sharedMaterial = capMaterial;

        // The spray passes through things that asked not to be hit
        // (Ignore Raycast): a can set on the workshop's turntable.
        foreach (var emitter in Object.FindObjectsByType<SprayEmitter>(FindObjectsInactive.Include))
            SetInt(emitter, "paintableMask", ~(1 << 2));

        var head = FindHead(xrOrigin);
        var workshop = BuildWorkshop(room, layout, left, right, sprayCan, body, valve, standardCap, bodyMaterial, capMaterial, out var parts);
        var canvas = BuildCanCanvas(body, bodyMesh, bodyRenderer, baseTexture, canLayout);

        var skin = GetOrAdd<CanSkin>(sprayCan);
        SetField(skin, "canvas", canvas);
        SetField(skin, "baseMetalMap", metalTexture);
        EditorUtility.SetDirty(skin);

        var stand = GetOrAdd<CanWorkshopStand>(workshop);
        SetField(stand, "can", sprayCan.GetComponent<VRGrabInteractable>());
        SetField(stand, "canBody", bodyRenderer);
        SetField(stand, "turntable", parts.Turntable);
        SetField(stand, "mount", parts.Mount);
        SetField(stand, "canvas", canvas);
        SetField(stand, "inputActions", inputActions.Asset);
        SetField(stand, "head", head);
        EditorUtility.SetDirty(stand);

        var shelf = GetOrAdd<CanShelf>(parts.Shelf.gameObject);
        SetField(shelf, "skin", skin);
        SetObjectArray(shelf, "slots", parts.Slots.ToArray());
        SetObjectArray(shelf, "replicas", parts.Replicas.ToArray());
        SetObjectArray(shelf, "labels", parts.SlotLabels.ToArray());
        SetField(shelf, "leftHand", left != null ? left.transform : null);
        SetField(shelf, "rightHand", right != null ? right.transform : null);
        SetField(shelf, "selectionMarker", parts.Marker);
        SetVector3(shelf, "markerOffset", new Vector3(0f, -0.004f, -0.07f));
        SetFloat(shelf, "selectRadius", 0.065f);
        SetFloat(shelf, "selectedPopScale", 1.12f);
        EditorUtility.SetDirty(shelf);

        var controller = GetOrAdd<CanWorkshop>(workshop);
        SetField(controller, "skin", skin);
        SetField(controller, "shelf", shelf);
        SetField(controller, "saveButton", parts.Save);
        SetField(controller, "clearButton", parts.Clear);
        SetField(controller, "nameButton", parts.Name);
        SetField(controller, "deleteButton", parts.Delete);
        SetField(controller, "nameLabel", parts.NameLabel);
        SetField(controller, "lamp", parts.Lamp);
        EditorUtility.SetDirty(controller);

        foreach (var button in new[] { parts.Save, parts.Clear, parts.Name, parts.Delete })
        {
            SetField(button, "leftController", left != null ? left.transform : null);
            SetField(button, "rightController", right != null ? right.transform : null);
            EditorUtility.SetDirty(button);
        }

        var workshopCan = BuildWorkshopCan(sprayCan, workshop.transform, parts.CanRest, workshopMaterial, capMaterial);

        var adaptive = Object.FindAnyObjectByType<AdaptiveRoomController>(FindObjectsInactive.Include);
        if (adaptive != null)
        {
            SetField(adaptive, "workshopRoot", workshop.transform);
            EditorUtility.SetDirty(adaptive);
        }

        log.AppendLine($"V0.12 Lata: textura base propria {CanTextureSize}px (aluminio acetinado, sem marca de terceiros), mapa metalico {CanMetalSize}px; corpo pintavel UV {canLayout.Side} ({canLayout.UPerMeter:F2} x {canLayout.VPerMeter:F2} UV/m).");
        log.AppendLine($"V0.12 Can Workshop: bancada atras do jogador, suporte giratorio (analogico), botoes SALVAR/LIMPAR/NOME, prateleira com {parts.Slots.Count} lugares, lata de oficina '{(workshopCan != null ? workshopCan.name : "-")}'.");
    }

    // ---------- measuring the can ----------

    static CanPaintLayout MeasureCanPaintLayout(Transform canRoot, Transform body, Mesh mesh)
    {
        var toCan = canRoot.worldToLocalMatrix * body.localToWorldMatrix;
        var vertices = mesh.vertices;
        var uvs = mesh.uv;
        var normals = mesh.normals;
        var triangles = mesh.triangles;

        var min = new Vector2(1f, 1f);
        var max = new Vector2(0f, 0f);
        float minY = float.MaxValue, maxY = float.MinValue, radius = 0f;
        var backDot = -2f;
        var backU = 0.5f;
        var uRight = true;
        for (var t = 0; t < triangles.Length; t += 3)
        {
            var i0 = triangles[t];
            var i1 = triangles[t + 1];
            var i2 = triangles[t + 2];
            var n = toCan.MultiplyVector(normals[i0] + normals[i1] + normals[i2]).normalized;
            var centroidV = (uvs[i0].y + uvs[i1].y + uvs[i2].y) / 3f;
            if (Mathf.Abs(n.y) > 0.35f || centroidV > 0.705f)
                continue;

            foreach (var i in new[] { i0, i1, i2 })
            {
                min = Vector2.Min(min, uvs[i]);
                max = Vector2.Max(max, uvs[i]);
                var p = toCan.MultiplyPoint3x4(vertices[i]);
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
                radius = Mathf.Max(radius, new Vector2(p.x, p.z).magnitude);
            }

            // The face toward -Z: where the wordmark goes, and which way u runs there.
            var dot = Vector3.Dot(n, Vector3.back);
            if (dot > backDot)
            {
                backDot = dot;
                backU = (uvs[i0].x + uvs[i1].x + uvs[i2].x) / 3f;
                var p0 = toCan.MultiplyPoint3x4(vertices[i0]);
                var p1 = toCan.MultiplyPoint3x4(vertices[i1]);
                var p2 = toCan.MultiplyPoint3x4(vertices[i2]);
                var right = Vector3.Cross(Vector3.up, -n).normalized; // seen from outside, facing the surface
                var du = Gradient(p0, p1, p2, uvs[i0].x, uvs[i1].x, uvs[i2].x);
                uRight = Vector3.Dot(du, right) > 0f;
            }
        }

        var circumference = 2f * Mathf.PI * radius;
        return new CanPaintLayout
        {
            Side = Rect.MinMaxRect(min.x, min.y, max.x, max.y),
            UPerMeter = (max.x - min.x) / Mathf.Max(0.001f, circumference),
            VPerMeter = (max.y - min.y) / Mathf.Max(0.001f, maxY - minY),
            BackU = backU,
            UIncreasesRight = uRight,
        };
    }

    // The direction in which a value interpolated over a triangle grows fastest (per meter).
    static Vector3 Gradient(Vector3 p0, Vector3 p1, Vector3 p2, float a0, float a1, float a2)
    {
        var e1 = p1 - p0;
        var e2 = p2 - p0;
        var n = Vector3.Cross(e1, e2);
        var area2 = n.sqrMagnitude;
        if (area2 < 1e-12f)
            return Vector3.zero;
        return (Vector3.Cross(e2, n) * (a1 - a0) + Vector3.Cross(n, e1) * (a2 - a0)) / area2;
    }

    // ---------- the Freehand can's own look ----------

    enum CanRegion : byte { None, Side, Shoulder, End, Valve, Cap }

    static void BuildCanLookTextures(Transform canRoot, Transform body, Mesh bodyMesh, Transform valve, Mesh valveMesh, Transform cap, Mesh capMesh, CanPaintLayout canLayout, out Texture2D baseTexture, out Texture2D metalTexture)
    {
        const int size = CanTextureSize;
        var regions = new CanRegion[size * size];
        var toCan = canRoot.worldToLocalMatrix * body.localToWorldMatrix;

        // Which part of the can every texel belongs to, from the meshes' own UVs.
        RasterizeRegions(bodyMesh, toCan, regions, size, (n, v) =>
            Mathf.Abs(n.y) <= 0.35f && v <= 0.705f ? CanRegion.Side : (Mathf.Abs(n.y) > 0.8f && v > 0.8f ? CanRegion.End : CanRegion.Shoulder));
        if (valveMesh != null && valveMesh.isReadable)
            RasterizeRegions(valveMesh, canRoot.worldToLocalMatrix * valve.localToWorldMatrix, regions, size, (n, v) => CanRegion.Valve);
        if (capMesh != null && capMesh.isReadable)
            RasterizeRegions(capMesh, canRoot.worldToLocalMatrix * cap.localToWorldMatrix, regions, size, (n, v) => CanRegion.Cap);

        // Ends: spun rings around each disc's own centre.
        var endCenters = new List<Vector2>();
        FindEndCentres(regions, size, endCenters);

        var rng = new System.Random(4242);
        var streaks = new float[size];
        for (var y = 0; y < size; y++)
            streaks[y] = (float)rng.NextDouble();

        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size;
                var v = (y + 0.5f) / size;
                Color c;
                switch (regions[y * size + x])
                {
                    case CanRegion.Side:
                    {
                        // Satin aluminium brushed round the can: fine
                        // horizontal streaks, a slow sheen variation.
                        var streak = (streaks[y] - 0.5f) * 0.05f + (streaks[(y + 1) % size] - 0.5f) * 0.025f;
                        var sheen = 0.02f * Mathf.Sin(u * 37f) * Mathf.Sin(v * 11f);
                        var g = 0.72f + streak + sheen;

                        // Rolled edges: a slightly darker line just inside the top and bottom.
                        var fromBottom = (v - canLayout.Side.yMin) * size;
                        var fromTop = (canLayout.Side.yMax - v) * size;
                        if (fromBottom < 4f || fromTop < 4f)
                            g -= 0.08f;
                        c = new Color(g, g + 0.006f, g + 0.014f);
                        break;
                    }
                    case CanRegion.Shoulder:
                    {
                        var g = 0.64f + (streaks[y] - 0.5f) * 0.05f;
                        c = new Color(g, g + 0.005f, g + 0.012f);
                        break;
                    }
                    case CanRegion.End:
                    {
                        var r = NearestRadius(endCenters, u, v) * size;
                        var g = 0.66f + 0.035f * Mathf.Sin(r * 1.7f) + 0.02f * Mathf.Sin(r * 5.3f);
                        c = new Color(g, g + 0.005f, g + 0.012f);
                        break;
                    }
                    case CanRegion.Valve:
                        c = new Color(0.58f, 0.59f, 0.61f);
                        break;
                    case CanRegion.Cap:
                        c = new Color(0.86f, 0.85f, 0.82f);
                        break;
                    default:
                        c = new Color(0.5f, 0.5f, 0.52f);
                        break;
                }

                pixels[y * size + x] = c;
            }
        }

        // Unused texels take their neighbours' colour, so filtering never
        // pulls a foreign colour across an island's edge.
        Dilate(pixels, regions, size, 6);

        DrawWordmark(pixels, size, canLayout);

        baseTexture = SaveCanTexture(CanBasePath, pixels, size, size);

        // Metallic (R) and smoothness (A) by region: bare aluminium is metal; the cap is plastic.
        var metal = new Color32[CanMetalSize * CanMetalSize];
        var step = size / CanMetalSize;
        for (var y = 0; y < CanMetalSize; y++)
        {
            for (var x = 0; x < CanMetalSize; x++)
            {
                var region = regions[(y * step + step / 2) * size + x * step + step / 2];
                metal[y * CanMetalSize + x] = region switch
                {
                    CanRegion.Side => new Color32(190, 0, 0, 140),
                    CanRegion.Shoulder => new Color32(205, 0, 0, 128),
                    CanRegion.End => new Color32(215, 0, 0, 150),
                    CanRegion.Valve => new Color32(215, 0, 0, 165),
                    CanRegion.Cap => new Color32(0, 0, 0, 90),
                    _ => new Color32(190, 0, 0, 140),
                };
            }
        }

        metalTexture = SaveCanTexture(CanMetalPath, metal, CanMetalSize, CanMetalSize);
    }

    static void RasterizeRegions(Mesh mesh, Matrix4x4 toCan, CanRegion[] regions, int size, System.Func<Vector3, float, CanRegion> classify)
    {
        var uvs = mesh.uv;
        var normals = mesh.normals;
        var triangles = mesh.triangles;
        for (var t = 0; t < triangles.Length; t += 3)
        {
            var i0 = triangles[t];
            var i1 = triangles[t + 1];
            var i2 = triangles[t + 2];
            var n = toCan.MultiplyVector(normals[i0] + normals[i1] + normals[i2]).normalized;
            var region = classify(n, (uvs[i0].y + uvs[i1].y + uvs[i2].y) / 3f);
            FillTriangle(regions, size, uvs[i0] * size, uvs[i1] * size, uvs[i2] * size, region);
        }
    }

    static void FillTriangle(CanRegion[] regions, int size, Vector2 a, Vector2 b, Vector2 c, CanRegion region)
    {
        var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))) - 1);
        var maxX = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))) + 1);
        var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))) - 1);
        var maxY = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))) + 1);
        var area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
        if (Mathf.Abs(area) < 1e-6f)
            return;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var w0 = ((b.x - p.x) * (c.y - p.y) - (c.x - p.x) * (b.y - p.y)) / area;
                var w1 = ((c.x - p.x) * (a.y - p.y) - (a.x - p.x) * (c.y - p.y)) / area;
                var w2 = 1f - w0 - w1;
                const float edge = -0.02f;
                if (w0 >= edge && w1 >= edge && w2 >= edge)
                    regions[y * size + x] = region;
            }
        }
    }

    static void FindEndCentres(CanRegion[] regions, int size, List<Vector2> centres)
    {
        // The end discs sit in the atlas' top-left; split them by u.
        var sums = new Vector2[2];
        var counts = new int[2];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (regions[y * size + x] != CanRegion.End)
                    continue;
                var u = (x + 0.5f) / size;
                var k = u < 0.205f ? 0 : 1;
                sums[k] += new Vector2(u, (y + 0.5f) / size);
                counts[k]++;
            }
        }

        for (var k = 0; k < 2; k++)
        {
            if (counts[k] > 0)
                centres.Add(sums[k] / counts[k]);
        }
    }

    static float NearestRadius(List<Vector2> centres, float u, float v)
    {
        var best = float.MaxValue;
        foreach (var c in centres)
            best = Mathf.Min(best, Vector2.Distance(c, new Vector2(u, v)));
        return best == float.MaxValue ? 0f : best;
    }

    static void Dilate(Color32[] pixels, CanRegion[] regions, int size, int passes)
    {
        var filled = new bool[regions.Length];
        for (var i = 0; i < regions.Length; i++)
            filled[i] = regions[i] != CanRegion.None;

        for (var pass = 0; pass < passes; pass++)
        {
            var next = (bool[])filled.Clone();
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = y * size + x;
                    if (filled[i])
                        continue;
                    for (var k = 0; k < 4; k++)
                    {
                        var nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                        var ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size || !filled[ny * size + nx])
                            continue;
                        pixels[i] = pixels[ny * size + nx];
                        next[i] = true;
                        break;
                    }
                }
            }

            filled = next;
        }
    }

    // A small FREEHAND near the bottom of the side the hand holds (toward
    // the player), and a thin dark band under the shoulder - the minimal
    // identity of a can that is meant to be painted.
    static void DrawWordmark(Color32[] pixels, int size, CanPaintLayout canLayout)
    {
        var pxPerMeterU = canLayout.UPerMeter * size;
        var pxPerMeterV = canLayout.VPerMeter * size;
        var xScale = pxPerMeterU / Mathf.Max(1f, pxPerMeterV);
        var ink = new Color32(40, 42, 46, 255);

        // Band under the shoulder, all round.
        var bandY0 = Mathf.RoundToInt((canLayout.Side.yMax - 0.028f) * size);
        var x0 = Mathf.RoundToInt(canLayout.Side.xMin * size);
        var x1 = Mathf.RoundToInt(canLayout.Side.xMax * size);
        for (var y = bandY0; y < bandY0 + 3; y++)
            for (var x = x0; x < x1; x++)
                pixels[y * size + x] = ink;

        // The wordmark, drawn into its own small buffer then placed (flipped
        // if u runs leftward on that side, so it reads from outside the can).
        const string word = "FREEHAND";
        var letterMeters = 0.011f;
        var letterPx = letterMeters * pxPerMeterV;
        var widthPx = StencilText.Measure(word) * letterPx * xScale;
        var w = Mathf.CeilToInt(widthPx) + 8;
        var h = Mathf.CeilToInt(letterPx) + 8;
        var buffer = new Color32[w * h];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = new Color32(0, 0, 0, 0);
        StencilText.Draw(buffer, w, h, word, new Vector2(4f, 4f), letterPx, Mathf.Max(1.6f, letterPx * 0.15f), ink, xScale);

        var centerX = canLayout.BackU * size;
        var baseY = (canLayout.Side.yMin + 0.03f) * size;
        for (var by = 0; by < h; by++)
        {
            for (var bx = 0; bx < w; bx++)
            {
                var a = buffer[by * w + bx].a / 255f;
                if (a <= 0f)
                    continue;
                var sx = canLayout.UIncreasesRight ? bx - w / 2 : w / 2 - bx;
                var x = Mathf.RoundToInt(centerX + sx);
                var y = Mathf.RoundToInt(baseY + by);
                if (x < 0 || y < 0 || x >= size || y >= size)
                    continue;
                var under = pixels[y * size + x];
                pixels[y * size + x] = Color32.Lerp(under, ink, a);
            }
        }
    }

    static Texture2D SaveCanTexture(string path, Color32[] pixels, int width, int height)
    {
        EnsureFolder("Assets/Art", "Generated");
        EnsureFolder("Assets/Art/Generated", "SprayCan");
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

    static void BuildCanMaterials(Texture2D baseTexture, Texture2D metalTexture, out Material body, out Material cap, out Material workshop)
    {
        EnsureFolder("Assets/Materials", "Can");
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{CanTexturesFolder}/LR_SprayCan_phong2_Normal.1001.png");

        Material Make(string name)
        {
            var path = $"{CanMaterialsFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetTexture("_BaseMap", baseTexture);
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
            }

            return material;
        }

        // Aluminium: metallic and smoothness from the map (paint lowers both at runtime - CanSkin).
        body = Make("FreehandCan_Body");
        body.SetColor("_BaseColor", Color.white);
        body.SetTexture("_MetallicGlossMap", metalTexture);
        body.EnableKeyword("_METALLICSPECGLOSSMAP");
        body.SetFloat("_Smoothness", 1f);
        body.SetFloat("_Metallic", 1f);
        EditorUtility.SetDirty(body);

        // The workshop's can: the same can in shop grey, so the two never get mixed up.
        workshop = Make("FreehandCan_Workshop");
        workshop.SetColor("_BaseColor", new Color(0.42f, 0.44f, 0.47f));
        workshop.SetTexture("_MetallicGlossMap", metalTexture);
        workshop.EnableKeyword("_METALLICSPECGLOSSMAP");
        workshop.SetFloat("_Smoothness", 0.8f);
        workshop.SetFloat("_Metallic", 1f);
        EditorUtility.SetDirty(workshop);

        // The actuator: neutral matte plastic.
        cap = Make("FreehandCan_Cap");
        cap.SetColor("_BaseColor", Color.white);
        cap.SetTexture("_MetallicGlossMap", null);
        cap.DisableKeyword("_METALLICSPECGLOSSMAP");
        cap.SetFloat("_Metallic", 0f);
        cap.SetFloat("_Smoothness", 0.35f);
        EditorUtility.SetDirty(cap);
    }

    // ---------- the canvas the spray paints the can through ----------

    static PaintSurface BuildCanCanvas(Transform body, Mesh bodyMesh, MeshRenderer bodyRenderer, Texture2D baseTexture, CanPaintLayout canLayout)
    {
        var existing = GameObject.Find("CanWorkshop_Canvas");
        var go = existing != null ? existing : new GameObject("CanWorkshop_Canvas");
        go.transform.SetPositionAndRotation(body.position, body.rotation);
        go.transform.localScale = body.lossyScale;

        var collider = GetOrAdd<MeshCollider>(go);
        collider.sharedMesh = bodyMesh;
        collider.convex = false;
        collider.enabled = false;
        var rb = GetOrAdd<Rigidbody>(go);
        rb.isKinematic = true;
        rb.useGravity = false;

        var surface = GetOrAdd<PaintSurface>(go);
        var so = new SerializedObject(surface);
        so.FindProperty("textureSize").intValue = CanTextureSize;
        so.FindProperty("surfaceId").stringValue = "CanSkin";
        so.FindProperty("targetRenderer").objectReferenceValue = bodyRenderer;
        so.FindProperty("baseTexture").objectReferenceValue = baseTexture;
        so.FindProperty("paintableUv").rectValue = canLayout.Side;
        so.FindProperty("wrapPaintableU").boolValue = true;
        so.FindProperty("includeInClearAll").boolValue = false;
        so.FindProperty("materialKind").enumValueIndex = (int)SurfaceMaterialKind.Flat;
        so.FindProperty("preExistingGraffitiMarks").intValue = 0;
        so.FindProperty("reliefThroughPaint").floatValue = 0.15f;

        // The full texture's extent in meters, and the texel shape, from the body's UV density.
        var sizeMeters = new Vector2(1f / canLayout.UPerMeter, 1f / canLayout.VPerMeter);
        so.FindProperty("surfaceSizeMeters").vector2Value = sizeMeters;
        var ppm = CanTextureSize / ((sizeMeters.x + sizeMeters.y) * 0.5f);
        so.FindProperty("texelAspectOverride").vector2Value = new Vector2(ppm / (CanTextureSize * canLayout.UPerMeter), ppm / (CanTextureSize * canLayout.VPerMeter));

        // Drips at the can's scale (its texels are ~12x finer than a wall's).
        so.FindProperty("wetnessCellPixels").intValue = 24;
        so.FindProperty("dripSpeedPixelsPerSecond").floatValue = 220f;
        so.FindProperty("maxActiveDrips").intValue = 10;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(go);
        return surface;
    }

    // ---------- the workshop ----------

    sealed class WorkshopParts
    {
        public Transform Turntable;
        public Transform Mount;
        public Transform CanRest;
        public Transform Shelf;
        public Transform Marker;
        public List<Transform> Slots = new List<Transform>();
        public List<Renderer> Replicas = new List<Renderer>();
        public List<Renderer> SlotLabels = new List<Renderer>();
        public WorkshopButton Save;
        public WorkshopButton Clear;
        public WorkshopButton Name;
        public WorkshopButton Delete;
        public Renderer NameLabel;
        public Renderer Lamp;
    }

    static GameObject BuildWorkshop(GameObject room, RoomLayoutProfile layout, GameObject left, GameObject right, GameObject sprayCan, Transform body, Transform valve, PhysicalCap cap, Material bodyMaterial, Material capMaterial, out WorkshopParts parts)
    {
        parts = new WorkshopParts();
        var root = Place(room.transform, "CanWorkshop", Vector3.zero);
        root.transform.SetPositionAndRotation(layout.WorkshopPosition(Vector3.zero), RoomLayoutProfile.WorkshopRotation);
        var t = root.transform;

        var steel = CreateMaterial("Workshop_Steel", new Color(0.2f, 0.21f, 0.23f), 0.45f, 0.6f);
        var top = CreateMaterial("Workshop_Top", new Color(0.33f, 0.34f, 0.36f), 0.3f, 0.5f);
        var rubber = CreateMaterial("Workshop_Rubber", new Color(0.07f, 0.07f, 0.08f), 0.2f);
        var accent = CreateMaterial("Workshop_Accent", new Color(0.93f, 0.62f, 0.12f), 0.35f);

        // Bench: a steel workbench, its front toward the player.
        var benchTop = MakeBox(t, "BenchTop", new Vector3(0f, 0.86f, 0f), new Vector3(1.2f, 0.04f, 0.55f), top, true);
        foreach (var (name, x, z) in new[] { ("Leg_FL", -0.56f, -0.235f), ("Leg_FR", 0.56f, -0.235f), ("Leg_BL", -0.56f, 0.235f), ("Leg_BR", 0.56f, 0.235f) })
            MakeBox(t, name, new Vector3(x, 0.42f, z), new Vector3(0.05f, 0.84f, 0.05f), steel, false);
        MakeBox(t, "LowerShelf", new Vector3(0f, 0.2f, 0.02f), new Vector3(1.12f, 0.025f, 0.46f), steel, true);
        MakeBox(t, "Apron", new Vector3(0f, 0.8f, -0.27f), new Vector3(1.2f, 0.08f, 0.02f), steel, false);
        MakeBox(t, "ApronStripe", new Vector3(0f, 0.77f, -0.281f), new Vector3(1.2f, 0.012f, 0.002f), accent, false);

        // Riser at the back: the "minhas latas" ledge; a low backboard with its header.
        MakeBox(t, "Riser", new Vector3(0f, 0.94f, 0.2f), new Vector3(1.16f, 0.12f, 0.13f), steel, true);
        MakeBox(t, "Backboard", new Vector3(0f, 1.12f, 0.275f), new Vector3(1.2f, 0.28f, 0.02f), steel, true);
        TextPlate(t, "Header_MinhasLatas", "MINHAS LATAS", new Vector3(0f, 1.2f, 0.264f), new Vector2(0.42f, 0.06f), new Color32(232, 228, 214, 255), new Color32(36, 37, 40, 255));

        // Sign on two posts, a lamp bar under it, and the light that falls on the bench.
        MakeBox(t, "SignPost_L", new Vector3(-0.58f, 1.31f, 0.26f), new Vector3(0.035f, 0.86f, 0.035f), steel, false);
        MakeBox(t, "SignPost_R", new Vector3(0.58f, 1.31f, 0.26f), new Vector3(0.035f, 0.86f, 0.035f), steel, false);
        MakeBox(t, "SignBoard", new Vector3(0f, 1.66f, 0.262f), new Vector3(0.84f, 0.17f, 0.02f), steel, false);
        TextPlate(t, "Sign_CanWorkshop", "CAN WORKSHOP", new Vector3(0f, 1.66f, 0.25f), new Vector2(0.78f, 0.13f), new Color32(240, 170, 40, 255), new Color32(24, 25, 27, 255));
        var lampBar = MakeBox(t, "LampBar", new Vector3(0f, 1.555f, 0.215f), new Vector3(0.7f, 0.022f, 0.05f), EmissiveMaterial("Workshop_LampBar", new Color(1f, 0.93f, 0.8f), 1.6f), false);
        lampBar.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var lightGo = Place(t, "BenchLight", new Vector3(0f, 1.48f, -0.08f));
        var light = GetOrAdd<Light>(lightGo);
        light.type = LightType.Point;
        light.color = new Color(1f, 0.9f, 0.78f);
        light.range = 1.4f;
        light.intensity = 1.3f;
        light.shadows = LightShadows.None;
        EditorUtility.SetDirty(light);

        // Turntable: a pedestal, and a plate that turns (ticks on its rim show the turn).
        MakeCylinder(t, "Pedestal", new Vector3(-0.05f, 0.93f, -0.05f), new Vector3(0.16f, 0.05f, 0.16f), steel, false);
        TextPlate(t, "Label_Girar", "GIRAR: ANALOGICO", new Vector3(-0.05f, 0.925f, -0.132f), new Vector2(0.13f, 0.022f), new Color32(220, 216, 204, 255), new Color32(28, 29, 31, 255));
        var turntable = Place(t, "Turntable", new Vector3(-0.05f, 0.98f, -0.05f)).transform;
        turntable.localRotation = Quaternion.identity;
        MakeCylinder(turntable, "Plate", new Vector3(0f, 0.006f, 0f), new Vector3(0.15f, 0.006f, 0.15f), rubber, false);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * 45f;
            var tick = MakeBox(turntable, $"Tick_{i}", Quaternion.Euler(0f, angle, 0f) * new Vector3(0f, 0.0125f, 0.066f), new Vector3(0.006f, 0.002f, 0.014f), i == 0 ? accent : steel, false);
            tick.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
        }

        parts.Turntable = turntable;
        parts.Mount = Place(turntable, "Mount", new Vector3(0f, 0.012f, 0f)).transform;

        // Where the workshop's own can stands.
        MakeCylinder(t, "CanRestPad", new Vector3(-0.42f, 0.883f, -0.08f), new Vector3(0.09f, 0.003f, 0.09f), rubber, false);
        parts.CanRest = Place(t, "CanRest", new Vector3(-0.42f, 0.887f, -0.08f)).transform;

        // Controls: four push buttons (V0.14: APAGAR, set a little apart - it asks for a second press), their
        // labels, the can's name plate and its lamp.
        MakeBox(t, "ControlPanel", new Vector3(0.41f, 0.9f, -0.13f), new Vector3(0.33f, 0.04f, 0.14f), steel, true);
        parts.Save = BuildButton(t, "Button_Salvar", new Vector3(0.285f, 0.92f, -0.13f), new Color(0.3f, 0.75f, 0.35f), steel);
        parts.Clear = BuildButton(t, "Button_Limpar", new Vector3(0.365f, 0.92f, -0.13f), new Color(0.85f, 0.32f, 0.2f), steel);
        parts.Name = BuildButton(t, "Button_Nome", new Vector3(0.445f, 0.92f, -0.13f), new Color(0.35f, 0.45f, 0.62f), steel);
        parts.Delete = BuildButton(t, "Button_Apagar", new Vector3(0.535f, 0.92f, -0.13f), new Color(0.45f, 0.08f, 0.08f), steel);
        TextPlate(t, "Label_Salvar", "SALVAR", new Vector3(0.285f, 0.9f, -0.2015f), new Vector2(0.07f, 0.02f), new Color32(220, 216, 204, 255), new Color32(28, 29, 31, 255));
        TextPlate(t, "Label_Limpar", "LIMPAR", new Vector3(0.365f, 0.9f, -0.2015f), new Vector2(0.07f, 0.02f), new Color32(220, 216, 204, 255), new Color32(28, 29, 31, 255));
        TextPlate(t, "Label_Nome", "NOME", new Vector3(0.445f, 0.9f, -0.2015f), new Vector2(0.07f, 0.02f), new Color32(220, 216, 204, 255), new Color32(28, 29, 31, 255));
        TextPlate(t, "Label_Apagar", "APAGAR", new Vector3(0.535f, 0.9f, -0.2015f), new Vector2(0.07f, 0.02f), new Color32(236, 150, 140, 255), new Color32(28, 29, 31, 255));
        MakeBox(t, "NamePlate", new Vector3(0.38f, 1.0f, -0.06f), new Vector3(0.3f, 0.075f, 0.015f), steel, false);
        var nameLabel = MakeQuad(t, "NameLabel", new Vector3(0.38f, 1.0f, -0.0685f), new Vector3(0.28f, 0.0525f, 1f), LabelMaterial("Workshop_NameLabel", null));
        parts.NameLabel = nameLabel.GetComponent<MeshRenderer>();
        var lamp = MakeCylinder(t, "StatusLamp", new Vector3(0.555f, 1.0f, -0.07f), new Vector3(0.02f, 0.004f, 0.02f), EmissiveMaterial("Workshop_StatusLamp", new Color(1f, 0.65f, 0.15f), 1.2f), false);
        lamp.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        parts.Lamp = lamp.GetComponent<MeshRenderer>();

        // The shelf: the plain can and six saved ones, each a small replica of the real can.
        var shelf = Place(t, "MyCans", new Vector3(0f, 1.0f, 0.2f)).transform;
        parts.Shelf = shelf;
        var replicaParts = ReplicaParts(sprayCan.transform, body, valve, cap);
        for (var i = 0; i < CanSkin.MaxSaved + 1; i++)
        {
            var slot = Place(shelf, $"Slot_{i}", new Vector3(-0.45f + i * 0.15f, 0f, 0f)).transform;
            slot.localRotation = Quaternion.identity;
            slot.localScale = Vector3.one * 0.5f;
            parts.Slots.Add(slot);
            parts.Replicas.Add(BuildReplica(slot, replicaParts, bodyMaterial, capMaterial));
            var label = MakeQuad(t, $"SlotLabel_{i}", new Vector3(-0.45f + i * 0.15f, 0.945f, 0.1335f), new Vector3(0.12f, 0.03f, 1f), LabelMaterial($"Workshop_SlotLabel", null));
            parts.SlotLabels.Add(label.GetComponent<MeshRenderer>());
        }

        var marker = MakeBox(shelf, "ActiveMarker", new Vector3(0f, -0.004f, -0.07f), new Vector3(0.07f, 0.006f, 0.004f), EmissiveMaterial("Workshop_ActiveMarker", new Color(1f, 0.75f, 0.3f), 1.5f), false);
        parts.Marker = marker.transform;

        // What the bench and its parts are made of, for the sound of a can set on them.
        var surface = GetOrAdd<AudioSurface>(root);
        var surfaceSo = new SerializedObject(surface);
        surfaceSo.FindProperty("sound").enumValueIndex = (int)SurfaceSound.Metal;
        surfaceSo.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(root);
        return root;
    }

    static WorkshopButton BuildButton(Transform parent, string name, Vector3 position, Color color, Material housingMaterial)
    {
        var root = Place(parent, name, position);
        root.transform.localRotation = Quaternion.identity;
        MakeCylinder(root.transform, "Housing", new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0.006f, 0.05f), housingMaterial, false);
        var cap = MakeCylinder(root.transform, "Cap", new Vector3(0f, 0.008f, 0f), new Vector3(0.036f, 0.005f, 0.036f), CreateMaterial(name + "_Cap", color, 0.45f), false);
        var button = GetOrAdd<WorkshopButton>(root);
        SetField(button, "cap", cap.transform);
        return button;
    }

    // A text plate: a quad with the words painted into its own texture.
    static void TextPlate(Transform parent, string name, string text, Vector3 position, Vector2 size, Color32 ink, Color32 background)
    {
        var aspect = size.x / size.y;
        var height = 64;
        var width = Mathf.Clamp(Mathf.RoundToInt(height * aspect / 4f) * 4, 64, 1024);
        var pixels = new Color32[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = background;
        var letter = height * 0.56f;
        var textWidth = StencilText.Measure(text) * letter;
        if (textWidth > width * 0.92f)
        {
            letter *= width * 0.92f / textWidth;
            textWidth = StencilText.Measure(text) * letter;
        }

        StencilText.Draw(pixels, width, height, text, new Vector2((width - textWidth) * 0.5f, (height - letter) * 0.5f), letter, Mathf.Max(2f, letter * 0.14f), ink);
        var texture = SaveCanTexture($"{WorkshopTexturesFolder}/{name}.png", pixels, width, height);
        MakeQuad(parent, name, position, new Vector3(size.x, size.y, 1f), LabelMaterial("Workshop_" + name, texture));
    }

    static Material LabelMaterial(string name, Texture2D texture)
    {
        var material = CreateMaterial(name, Color.white, 0.25f);
        material.SetTexture("_BaseMap", texture);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material EmissiveMaterial(string name, Color color, float intensity)
    {
        var material = CreateMaterial(name, color, 0.4f);
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", color * intensity);
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        EditorUtility.SetDirty(material);
        return material;
    }

    // The real can's visible parts (body, valve, cap) as meshes with their pose in can space.
    sealed class ReplicaPart
    {
        public string Name;
        public Mesh Mesh;
        public Matrix4x4 Pose;
        public bool IsCap;
    }

    static List<ReplicaPart> ReplicaParts(Transform canRoot, Transform body, Transform valve, PhysicalCap cap)
    {
        var list = new List<ReplicaPart>();
        void Add(string name, Transform part, bool isCap)
        {
            if (part == null)
                return;
            var filter = part.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                return;
            list.Add(new ReplicaPart { Name = name, Mesh = filter.sharedMesh, Pose = canRoot.worldToLocalMatrix * part.localToWorldMatrix, IsCap = isCap });
        }

        Add("Body", body, false);
        Add("Valve", valve, false);
        Add("Cap", cap != null ? cap.transform : null, true);
        return list;
    }

    static Renderer BuildReplica(Transform slot, List<ReplicaPart> parts, Material bodyMaterial, Material capMaterial)
    {
        Renderer bodyRenderer = null;
        foreach (var part in parts)
        {
            var existing = slot.Find(part.Name);
            var go = existing != null ? existing.gameObject : new GameObject(part.Name);
            go.transform.SetParent(slot, false);
            go.transform.localPosition = part.Pose.GetColumn(3);
            go.transform.localRotation = part.Pose.rotation;
            go.transform.localScale = part.Pose.lossyScale;
            GetOrAdd<MeshFilter>(go).sharedMesh = part.Mesh;
            var renderer = GetOrAdd<MeshRenderer>(go);
            renderer.sharedMaterial = part.IsCap ? capMaterial : bodyMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (part.Name == "Body")
                bodyRenderer = renderer;
        }

        return bodyRenderer;
    }

    // ---------- the workshop's can ----------

    // A second can, the same object as the player's in every way (grip,
    // spray, cap, sound, inspection) - built from it each run - in shop grey,
    // standing on the bench: the can that paints the player's can.
    static GameObject BuildWorkshopCan(GameObject sprayCan, Transform workshop, Transform rest, Material workshopMaterial, Material capMaterial)
    {
        foreach (var old in Object.FindObjectsByType<CanSkin>(FindObjectsInactive.Include))
        {
            if (old.gameObject != sprayCan)
                Object.DestroyImmediate(old.gameObject);
        }

        var existing = workshop.Find("WorkshopCan");
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);

        var copy = Object.Instantiate(sprayCan, workshop, false);
        copy.name = "WorkshopCan";
        copy.transform.SetPositionAndRotation(rest.position, Quaternion.identity);

        var skin = copy.GetComponent<CanSkin>();
        if (skin != null)
            Object.DestroyImmediate(skin);

        var body = FindDeep(copy.transform, "sm_SprayBottle1");
        var valve = FindDeep(copy.transform, "ValveCup");
        if (body != null)
            body.GetComponent<MeshRenderer>().sharedMaterial = workshopMaterial;
        if (valve != null)
            valve.GetComponent<MeshRenderer>().sharedMaterial = workshopMaterial;
        foreach (var cap in copy.GetComponentsInChildren<PhysicalCap>(true))
            cap.GetComponent<MeshRenderer>().sharedMaterial = capMaterial;

        EditorUtility.SetDirty(copy);
        return copy;
    }

    // FindOrCreate, but placed every run (the builder may have moved it).
    static GameObject Place(Transform parent, string name, Vector3 localPosition)
    {
        var go = FindOrCreate(parent, name, localPosition);
        go.transform.localPosition = localPosition;
        return go;
    }

    static void SetObjectArray<T>(Object target, string field, T[] values) where T : Object
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(field);
        if (property == null)
        {
            log.AppendLine($"AVISO: campo '{field}' nao encontrado em {target.GetType().Name}.");
            return;
        }

        property.arraySize = values.Length;
        for (var i = 0; i < values.Length; i++)
            property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}

// The can's own textures: kept readable (the paint and the metal map read them
// at runtime) and uncompressed (512px: 1MB; the metal map is linear data).
class GeneratedCanTextureImport : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith("Assets/Art/Generated/SprayCan/"))
            return;

        var importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Default;
        importer.isReadable = !assetPath.Contains("/Workshop/");
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.sRGBTexture = !assetPath.EndsWith("_Metal.png");
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.alphaIsTransparency = false;
    }
}
