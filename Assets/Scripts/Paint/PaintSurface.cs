using System.Collections.Generic;
using UnityEngine;

namespace Freehand.Paint
{
    /// <summary>The look of a surface's initial (unpainted) texture and material - purely cosmetic, generated once at Awake, never touches the paint math below.</summary>
    public enum SurfaceMaterialKind
    {
        /// <summary>Flat base color, no procedural pattern - the original V0.4 behaviour.</summary>
        Flat,
        Concrete,
        Brick,
        Metal,
    }

    /// <summary>
    /// A paintable surface backed by a single CPU-side Texture2D.
    ///
    /// Performance shape (kept from the V0.4 rewrite): stamps write into a
    /// persistent Color32 buffer with plain array indexing, and the texture is
    /// uploaded at most once per frame. The V0.2 version called
    /// GetPixel/SetPixel per pixel and Apply() per spray hit, which is what
    /// made painting feel laggy - never go back to that.
    ///
    /// Aerosol model (V0.10). Each paint tick carries a fixed amount of paint
    /// (SprayStamp.alpha: the cap's flow at that distance and trigger
    /// pressure, for that much time), spread over the stamps laid along the
    /// path the nozzle swept since the last tick - so holding still builds
    /// paint up and a fast pass lays a thin coat, like a real can. Paint
    /// accumulates by saturation (coverage = 1 - e^-paint), so coats build
    /// up gradually instead of jumping to solid. The stamp has a dense core
    /// and a soft edge made ragged by smooth noise, mottling that differs
    /// from stamp to stamp, and sparse overspray droplets past its edge.
    /// Thin coats show the wall through, and even a thick coat keeps a little
    /// of the wall's relief (mortar, pits). 8-bit rounding is stochastic, so
    /// very light passes still add up. The cleaner sprays the wall's own
    /// original texture back, not a flat color.
    ///
    /// Undo (V0.10): before changing pixels, the surface saves the 32x32
    /// tiles it is about to touch - each once per action - into
    /// PaintHistory.Recording; undoing puts those tiles back. Memory follows
    /// the area painted, not the wall size, so the history needs no fixed
    /// action count. ClearPaint restores the unpainted surface the same way.
    ///
    /// Drips run with real gravity (V0.9): each impact carries gravity
    /// projected onto the hit triangle and converted into this surface's UVs
    /// through that triangle's own texture coordinates (TryWorldToUv). The
    /// drip is born at the lower edge of the paint that built up, runs
    /// downhill at a speed scaled by how steep the surface is, carries more
    /// paint the more had built up, and does not run at all where the surface
    /// is level (floor, ceiling). Paint is always drawn into the surface's own
    /// texture, so drips can never leave it, float off it or go through it.
    /// </summary>
    public class PaintSurface : MonoBehaviour, IPaintTarget
    {
        [SerializeField] int textureSize = 512;
        [SerializeField] Color baseColor = new Color(0.75f, 0.75f, 0.75f, 1f);

        [Header("Target (V0.12)")]
        [Tooltip("The renderer that shows the paint. Empty: this object's own. (A can's skin is painted through a collider in the workshop but shown on the can.)")]
        [SerializeField] Renderer targetRenderer;

        [Tooltip("The unpainted surface as a texture (readable), resampled to textureSize. Empty: baseColor and materialKind.")]
        [SerializeField] Texture2D baseTexture;

        [Tooltip("Only texels inside this UV rectangle take paint (a can's body; its shoulder, ends and valve stay bare). Zero size: the whole texture.")]
        [SerializeField] Rect paintableUv;

        [Tooltip("The paintable rectangle wraps round in U (a cylinder's seam): spray near one edge lands across the seam too.")]
        [SerializeField] bool wrapPaintableU;

        [Tooltip("Texel shape for the spray (see MeasureFace), when the builder measured it from the mesh. Zero: measured at startup from the face the room sees.")]
        [SerializeField] Vector2 texelAspectOverride;

        [Tooltip("Whether Clear All (held B) clears this surface. A can's skin is the player's own: it is not wiped with the walls.")]
        [SerializeField] bool includeInClearAll = true;

        [Tooltip("Stable id for this surface, used by PaintHistory's stroke metadata. Empty falls back to the GameObject's name.")]
        [SerializeField] string surfaceId;

        [Header("Overlay (V0.13, mixed reality)")]
        [Tooltip("The surface starts fully transparent and paint is laid over whatever is behind it (a real wall seen through passthrough): opacity is part of the paint. The texture is uploaded premultiplied, for the overlay shader.")]
        [SerializeField] bool overlay;

        [Header("Surface look (V0.7)")]
        [SerializeField] SurfaceMaterialKind materialKind = SurfaceMaterialKind.Flat;
        [SerializeField] [Range(0f, 1f)] float baseSmoothness = 0.15f;
        [SerializeField] [Range(0f, 1f)] float baseMetallic;

        [Tooltip("How many old, faded tags this wall already carries when the session starts (drawn with the same aerosol brush the player uses) - so the spot reads as used. 0 disables it.")]
        [SerializeField] int preExistingGraffitiMarks;
        [SerializeField] int graffitiRandomSeed;

        [Header("Aerosol")]
        [Tooltip("How much of the wall's relief (mortar, pits, grain) stays visible through a full coat of paint.")]
        [SerializeField] [Range(0f, 1f)] float reliefThroughPaint = 0.4f;

        [Tooltip("How many overspray droplets land past the edge of the spray, 0-1.")]
        [SerializeField] [Range(0f, 1f)] float overspray = 0.5f;

        [Header("Cleaner")]
        [Tooltip("The Cleaner's footprint relative to paint from the same cap and distance: one pass along a line takes its soft edge and overspray too.")]
        [SerializeField] float cleanerReach = 1.4f;

        [Tooltip("How fast the Cleaner dissolves paint, relative to how fast the same pass lays it down.")]
        [SerializeField] float cleanerStrength = 1.8f;

        [Tooltip("A cleaned pixel within this many levels (0-255) of the bare wall becomes the bare wall.")]
        [SerializeField] int cleanerSnapLevels = 6;

        public string SurfaceId => string.IsNullOrEmpty(surfaceId) ? name : surfaceId;

        public bool ClearedByClearAll => includeInClearAll;

        /// <summary>Whether paint is laid over a transparent surface (a real wall in mixed reality) instead of over a drawn one.</summary>
        public bool IsOverlay => overlay;

        /// <summary>
        /// For a surface built at runtime (a detected wall): its size in meters and whether it is an
        /// overlay. Call before the component's Awake (the surface is created inactive and activated
        /// after this), because the texel density and shape are worked out once at startup.
        /// </summary>
        public void ConfigureRuntime(Vector2 sizeMeters, bool overlayPaint, string id)
        {
            surfaceSizeMeters = sizeMeters;
            overlay = overlayPaint;
            surfaceId = id;
            if (sizeMeters.x > 0f && sizeMeters.y > 0f)
            {
                // Texels are wider than tall (or the other way round) on a wall that is not
                // square: the spray's shape is worked out in round units (see MeasureFace).
                var average = (sizeMeters.x + sizeMeters.y) * 0.5f;
                texelAspectOverride = new Vector2(sizeMeters.x / average, sizeMeters.y / average);
            }
        }

        /// <summary>The renderer the paint is shown on.</summary>
        public Renderer TargetRenderer => targetRenderer != null ? targetRenderer : GetComponent<Renderer>();

        public int TextureSize => textureSize;

        /// <summary>The live paint buffer and the unpainted surface (read-only use: skins, previews).</summary>
        public Color32[] Pixels => pixels;
        public Color32[] BasePixels => basePixels;

        /// <summary>Goes up every time the paint changes on screen (each upload).</summary>
        public int Revision { get; private set; }

        // The paintable rectangle in texels (inclusive), or the whole texture.
        int paintMinX, paintMinY, paintMaxX, paintMaxY;

        [Header("Drips")]
        [SerializeField] int wetnessCellPixels = 16;
        [SerializeField] float dripThreshold = 7f;
        [SerializeField] int maxActiveDrips = 24;

        [Tooltip("Run speed on a vertical surface; slower as the surface leans back, zero on a level one.")]
        [SerializeField] float dripSpeedPixelsPerSecond = 26f;
        [SerializeField] float dripLifeSeconds = 2.6f;

        [Tooltip("Below this share of gravity along the surface (floors, ceilings, gentle slopes) paint pools instead of running.")]
        [SerializeField] float minDripSlope = 0.3f;

        [Tooltip("Seconds for accumulated wet paint to dry to about a third: holding the spray on one spot builds up and drips, a sweep that comes back later does not.")]
        [SerializeField] float wetnessDrySeconds = 4f;

        [Header("Size")]
        [SerializeField]
        [Tooltip("Paintable face size in meters. Left at zero it is taken from the renderer bounds at startup.")]
        Vector2 surfaceSizeMeters = Vector2.zero;

        const int TileSize = 32;

        // The room-facing face (MeasureFace): texel steps per round texel
        // along the wall's width and height, and the per-axis scale that
        // turns a texel offset into round texels for the spray's shape.
        Vector2 faceRight = Vector2.right;
        Vector2 faceUp = Vector2.up;
        Vector2 texelAspect = Vector2.one;

        struct Drip
        {
            public float X;
            public float Y;
            public float DirX;
            public float DirY;
            public float Speed;
            public float Width;
            public float Alpha;
            public float Life;
            public Color32 Color;
            public bool Active;
        }

        // A tile of pixels saved for undo, its buffer pooled.
        sealed class TileState : IPooledUndoState
        {
            public PaintSurface Owner;
            public int Tile;
            public Color32[] Pixels;

            public void ReturnToPool()
            {
                if (Pixels != null && TilePool.Count < MaxPooledTiles)
                    TilePool.Push(Pixels);
                Pixels = null;
            }
        }

        const int MaxPooledTiles = 512;
        static readonly Stack<Color32[]> TilePool = new Stack<Color32[]>();

        Texture2D paintTexture;
        Color32[] pixels;
        Color32[] basePixels;
        byte[] baseShade;
        Color32 baseColor32;
        bool dirty;
        float pixelsPerMeter = 100f;
        int tilesPerRow;
        int stampSeed;

        // Overlay surfaces: the premultiplied copy that is uploaded, and the changed rectangle.
        [System.NonSerialized] Color32[] uploadPixels;
        int touchMinX = int.MaxValue, touchMinY = int.MaxValue, touchMaxX = -1, touchMaxY = -1;

        PaintHistory.Record captureRecord;
        bool[] capturedTiles;

        float[] wetness;
        int wetnessWidth;
        bool anyWetness;
        Drip[] drips;
        int activeDripCount;

        // This surface's collider mesh, read once: the triangles the spray
        // raycast hits, for converting world directions into UV.
        Vector3[] meshVertices;
        Vector2[] meshUvs;
        int[] meshTriangles;

        /// <summary>Pixels currently different from the unpainted surface (diagnostics, tests).</summary>
        public int CountPaintedPixels()
        {
            var count = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                if (!Same(pixels[i], basePixels[i]))
                    count++;
            }

            return count;
        }

        /// <summary>A cheap fingerprint of the current pixels (diagnostics, tests: undo must restore it exactly).</summary>
        public uint PixelHash()
        {
            unchecked
            {
                var hash = 2166136261u;
                for (var i = 0; i < pixels.Length; i += 3)
                {
                    var p = pixels[i];
                    hash = (hash ^ (uint)(p.r | p.g << 8 | p.b << 16 | (overlay ? p.a << 24 : 0))) * 16777619u;
                }

                return hash;
            }
        }

        void Awake()
        {
            paintTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            baseColor32 = baseColor;
            pixels = new Color32[textureSize * textureSize];
            if (!overlay)
            {
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = baseColor32;
            }
            if (baseTexture != null)
                CopyBaseTexture();

            if (paintableUv.width > 0f && paintableUv.height > 0f)
            {
                paintMinX = Mathf.Clamp(Mathf.FloorToInt(paintableUv.xMin * textureSize), 0, textureSize - 1);
                paintMaxX = Mathf.Clamp(Mathf.CeilToInt(paintableUv.xMax * textureSize) - 1, 0, textureSize - 1);
                paintMinY = Mathf.Clamp(Mathf.FloorToInt(paintableUv.yMin * textureSize), 0, textureSize - 1);
                paintMaxY = Mathf.Clamp(Mathf.CeilToInt(paintableUv.yMax * textureSize) - 1, 0, textureSize - 1);
            }
            else
            {
                paintMinX = paintMinY = 0;
                paintMaxX = paintMaxY = textureSize - 1;
            }

            ResolveSize();
            CacheCollisionMesh();
            MeasureFace();

            if (!overlay)
            {
                ApplyMaterialKindPattern();
                BuildBaseShade();
                if (preExistingGraffitiMarks > 0)
                    StampPreExistingGraffiti();
            }
            else
            {
                // Nothing to read relief from: a real wall is not ours to shade.
                baseShade = new byte[pixels.Length];
            }

            // The unpainted surface - environment marks included - is what
            // the cleaner sprays back and what Clear All returns to.
            basePixels = new Color32[pixels.Length];
            System.Array.Copy(pixels, basePixels, pixels.Length);

            tilesPerRow = (textureSize + TileSize - 1) / TileSize;
            capturedTiles = new bool[tilesPerRow * tilesPerRow];

            paintTexture.SetPixels32(pixels);
            paintTexture.Apply(false, false);

            var material = TargetRenderer.material;
            material.SetTexture("_BaseMap", paintTexture);
            if (baseTexture == null)
            {
                material.SetFloat("_Smoothness", baseSmoothness);
                material.SetFloat("_Metallic", baseMetallic);
            }

            wetnessWidth = Mathf.Max(1, textureSize / Mathf.Max(1, wetnessCellPixels));
            wetness = new float[wetnessWidth * wetnessWidth];
            drips = new Drip[Mathf.Max(1, maxActiveDrips)];
        }

        // The face the room sees, measured once: which way its texture runs
        // (a cube's faces carry mirrored and rotated UVs) and how big its
        // texels are along the wall's width and height - a 3.6 x 2.5 m face
        // on a square texture has texels 1.44x wider than tall, and a stamp
        // drawn round in texels would land on the wall as a flat ellipse.
        void MeasureFace()
        {
            faceRight = Vector2.right;
            faceUp = Vector2.up;
            texelAspect = Vector2.one;
            if (texelAspectOverride.x > 0f && texelAspectOverride.y > 0f)
            {
                texelAspect = texelAspectOverride;
                return;
            }

            if (meshTriangles == null)
                return;

            var bounds = TargetRenderer.bounds;
            var room = transform.parent != null ? transform.parent.position : Vector3.zero;
            var toRoom = new Vector3(room.x - bounds.center.x, 0f, room.z - bounds.center.z);
            if (toRoom.sqrMagnitude < 1e-6f)
                return;
            toRoom.Normalize();

            var bestTriangle = -1;
            var bestNormal = Vector3.zero;
            var bestFacing = 0.5f;
            for (var t = 0; t < meshTriangles.Length / 3; t++)
            {
                var p0 = transform.TransformPoint(meshVertices[meshTriangles[t * 3]]);
                var p1 = transform.TransformPoint(meshVertices[meshTriangles[t * 3 + 1]]);
                var p2 = transform.TransformPoint(meshVertices[meshTriangles[t * 3 + 2]]);
                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                if (normal.sqrMagnitude < 1e-12f)
                    continue;

                normal.Normalize();
                if (Vector3.Dot(normal, (p0 + p1 + p2) / 3f - bounds.center) < 0f)
                    normal = -normal;

                var facing = Vector3.Dot(normal, toRoom);
                if (facing > bestFacing)
                {
                    bestFacing = facing;
                    bestTriangle = t;
                    bestNormal = normal;
                }
            }

            // Seen from the room: right is up x the view direction (-normal).
            var right = Vector3.Cross(Vector3.up, -bestNormal);
            var up = Vector3.ProjectOnPlane(Vector3.up, bestNormal);
            if (bestTriangle < 0 || right.sqrMagnitude < 1e-6f || up.sqrMagnitude < 1e-6f
                || !TryWorldToUv(bestTriangle, right.normalized, out var uvRight)
                || !TryWorldToUv(bestTriangle, up.normalized, out var uvUp))
                return;

            // Texel steps per "round" texel (pixelsPerMeter) along the face.
            var r = uvRight * (textureSize / pixelsPerMeter);
            var u = uvUp * (textureSize / pixelsPerMeter);
            var det = r.x * u.y - u.x * r.y;
            if (Mathf.Abs(det) < 1e-6f)
                return;

            faceRight = r;
            faceUp = u;
            texelAspect = new Vector2(
                Mathf.Sqrt(u.y * u.y + r.y * r.y) / Mathf.Abs(det),
                Mathf.Sqrt(u.x * u.x + r.x * r.x) / Mathf.Abs(det));
        }

        void OnEnable() => PaintHistory.Register(this);

        void OnDisable() => PaintHistory.Unregister(this);

        // Surfaces built at runtime (detected walls) come and go with the mode: give back what
        // they made. The ones in the scene live as long as the scene does.
        void OnDestroy()
        {
            if (!overlay)
                return;

            if (paintTexture != null)
                Destroy(paintTexture);
            var renderer = TargetRenderer;
            if (renderer != null && renderer.sharedMaterial != null)
                Destroy(renderer.sharedMaterial);
        }

        void CacheCollisionMesh()
        {
            var meshCollider = GetComponent<MeshCollider>();
            var mesh = meshCollider != null ? meshCollider.sharedMesh : null;
            if (mesh == null || !mesh.isReadable)
            {
                Debug.LogError($"{nameof(PaintSurface)} '{SurfaceId}': needs a readable MeshCollider mesh to know which way is down on its texture - drips and stretched stamps disabled here.", this);
                return;
            }

            meshVertices = mesh.vertices;
            meshUvs = mesh.uv;
            meshTriangles = mesh.triangles;
        }

        /// <summary>
        /// A world-space direction lying on this surface at the raycast-hit triangle, expressed in UV
        /// units per meter (through the triangle's own texture coordinates, so any face orientation,
        /// mirrored or rotated UVs included, maps correctly). False if the triangle is unknown.
        /// </summary>
        public bool TryWorldToUv(int triangleIndex, Vector3 worldDirection, out Vector2 uv)
        {
            uv = Vector2.zero;
            if (meshTriangles == null || meshUvs == null || meshUvs.Length != meshVertices.Length
                || triangleIndex < 0 || triangleIndex * 3 + 2 >= meshTriangles.Length)
                return false;

            var i0 = meshTriangles[triangleIndex * 3];
            var i1 = meshTriangles[triangleIndex * 3 + 1];
            var i2 = meshTriangles[triangleIndex * 3 + 2];
            var p0 = transform.TransformPoint(meshVertices[i0]);
            var e1 = transform.TransformPoint(meshVertices[i1]) - p0;
            var e2 = transform.TransformPoint(meshVertices[i2]) - p0;

            // worldDirection = a * e1 + b * e2 (least squares on the triangle's plane).
            var g11 = Vector3.Dot(e1, e1);
            var g12 = Vector3.Dot(e1, e2);
            var g22 = Vector3.Dot(e2, e2);
            var det = g11 * g22 - g12 * g12;
            if (Mathf.Abs(det) < 1e-12f)
                return false;

            var r1 = Vector3.Dot(e1, worldDirection);
            var r2 = Vector3.Dot(e2, worldDirection);
            var a = (r1 * g22 - r2 * g12) / det;
            var b = (r2 * g11 - r1 * g12) / det;
            uv = a * (meshUvs[i1] - meshUvs[i0]) + b * (meshUvs[i2] - meshUvs[i0]);
            return true;
        }

        void ResolveSize()
        {
            if (surfaceSizeMeters.sqrMagnitude <= 0f)
            {
                // Largest two of the three bounds axes are the paintable face;
                // the third is the wall's thickness.
                var size = TargetRenderer.bounds.size;
                var largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                var smallest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
                var middle = size.x + size.y + size.z - largest - smallest;
                surfaceSizeMeters = new Vector2(largest, middle);
            }

            var averageMeters = Mathf.Max(0.01f, (surfaceSizeMeters.x + surfaceSizeMeters.y) * 0.5f);
            pixelsPerMeter = textureSize / averageMeters;
        }

        // ---------- the unpainted surface ----------

        // Cosmetic, one-shot, pixel-space. Smooth (interpolated) noise only:
        // per-pixel or per-block hashes read as squares and static in the
        // headset.
        void ApplyMaterialKindPattern()
        {
            switch (materialKind)
            {
                case SurfaceMaterialKind.Concrete:
                    ApplyConcrete();
                    break;
                case SurfaceMaterialKind.Brick:
                    ApplyBrick();
                    break;
                case SurfaceMaterialKind.Metal:
                    ApplyMetal();
                    break;
            }
        }

        // Cast concrete: broad blotches, finer grain, a few darker stains and
        // small pits.
        void ApplyConcrete()
        {
            for (var y = 0; y < textureSize; y++)
            {
                var row = y * textureSize;
                for (var x = 0; x < textureSize; x++)
                {
                    var blotch = Fbm(x * 0.045f, y * 0.045f);
                    var grain = SmoothNoise(x * 0.55f + 17f, y * 0.55f + 5f);
                    var stain = Mathf.InverseLerp(0.58f, 0.8f, Fbm(x * 0.012f + 31f, y * 0.012f + 7f));
                    var luminance = 1f + (blotch - 0.5f) * 0.26f + (grain - 0.5f) * 0.07f - stain * 0.12f;
                    if (Hash01(x, y, 991) < 0.004f)
                        luminance *= 0.78f;
                    pixels[row + x] = ScaleColor32(baseColor32, luminance);
                }
            }
        }

        // Running-bond brick: mortar recessed (darker, with a shadow line
        // under each course), every brick its own tint, mottled faces and
        // soft chips at the corners.
        void ApplyBrick()
        {
            const int brickWidth = 46;
            const int brickHeight = 22;
            const int mortarPixels = 3;

            for (var y = 0; y < textureSize; y++)
            {
                var row = y * textureSize;
                var brickRow = y / brickHeight;
                var rowOffset = (brickRow % 2 == 0) ? 0 : brickWidth / 2;
                var withinRow = y % brickHeight;

                for (var x = 0; x < textureSize; x++)
                {
                    var withinCol = (x + rowOffset) % brickWidth;
                    var brickCol = (x + rowOffset) / brickWidth;
                    var mortarNoise = SmoothNoise(x * 0.4f, y * 0.4f);

                    // Chipped corners: mortar eats into the brick where the
                    // noise says so.
                    var edge = Mathf.Min(Mathf.Min(withinRow, brickHeight - withinRow), Mathf.Min(withinCol, brickWidth - withinCol));
                    var chip = SmoothNoise(x * 0.21f + 9f, y * 0.21f + 3f) * 2.2f;
                    if (withinRow < mortarPixels || withinCol < mortarPixels || edge < chip - 0.6f)
                    {
                        pixels[row + x] = ScaleColor32(baseColor32, 0.5f + mortarNoise * 0.12f);
                        continue;
                    }

                    var tint = 1f + (Hash01(brickCol, brickRow, 17) - 0.5f) * 0.22f;
                    var face = 1f + (Fbm(x * 0.09f, y * 0.09f) - 0.5f) * 0.18f + (SmoothNoise(x * 0.7f, y * 0.7f) - 0.5f) * 0.06f;
                    var shadow = withinRow == mortarPixels ? 0.82f : 1f;
                    var hue = Hash01(brickCol, brickRow, 31);
                    var c = ScaleColor32(baseColor32, tint * face * shadow);
                    if (hue > 0.8f)
                        c = ScaleColor32(new Color32((byte)(c.r * 0.9f), (byte)(c.g * 0.95f), c.b, 255), 0.92f);
                    pixels[row + x] = c;
                }
            }
        }

        // Painted steel panels: soft vertical streaks, panel seams with rivets,
        // and rust bleeding from a few spots.
        void ApplyMetal()
        {
            var seamY = textureSize / 2 + Mathf.FloorToInt(Hash01(1, 1, 5) * 20f);
            var panelWidth = Mathf.Max(64, Mathf.RoundToInt(pixelsPerMeterGuess() * 1.2f));
            var rust = new Color32(118, 62, 34, 255);

            for (var y = 0; y < textureSize; y++)
            {
                var row = y * textureSize;
                var nearSeam = Mathf.Abs(y - seamY) <= 1;
                for (var x = 0; x < textureSize; x++)
                {
                    var verticalSeam = x % panelWidth <= 1;
                    if (nearSeam || verticalSeam)
                    {
                        pixels[row + x] = ScaleColor32(baseColor32, 0.55f);
                        continue;
                    }

                    var rivet = (Mathf.Abs(y - seamY) == 6 || x % panelWidth == 6) && (x + y) % 18 < 2;
                    var streak = SmoothNoise(x * 0.35f, y * 0.012f);
                    var luminance = 1f + (streak - 0.5f) * 0.16f + (SmoothNoise(x * 0.8f, y * 0.8f) - 0.5f) * 0.05f;
                    if (rivet)
                        luminance *= 1.25f;

                    var c = ScaleColor32(baseColor32, luminance);
                    var rustAmount = Mathf.InverseLerp(0.62f, 0.85f, Fbm(x * 0.02f + 13f, y * 0.035f + 2f));
                    if (rustAmount > 0f)
                        c = Color32.Lerp(c, rust, rustAmount * 0.6f);
                    pixels[row + x] = c;
                }
            }
        }

        // Before ResolveSize has run: the texture over a ~3m wall.
        float pixelsPerMeterGuess() => textureSize / 3f;

        // How the wall's relief shades paint laid on it: its luminance against
        // its average, as a 0.6-1.4 factor in a byte.
        void BuildBaseShade()
        {
            baseShade = new byte[pixels.Length];
            double sum = 0;
            for (var i = 0; i < pixels.Length; i++)
                sum += Luminance(pixels[i]);
            var average = Mathf.Max(1f, (float)(sum / pixels.Length));

            for (var i = 0; i < pixels.Length; i++)
            {
                var factor = Mathf.Clamp(Luminance(pixels[i]) / average, 0.6f, 1.4f);
                baseShade[i] = (byte)Mathf.RoundToInt((factor - 0.6f) / 0.8f * 255f);
            }
        }

        static float Luminance(Color32 c) => c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;

        static Color32 ScaleColor32(Color32 color, float scale)
        {
            return new Color32(
                (byte)Mathf.Clamp(color.r * scale, 0f, 255f),
                (byte)Mathf.Clamp(color.g * scale, 0f, 255f),
                (byte)Mathf.Clamp(color.b * scale, 0f, 255f),
                color.a);
        }

        // Single-stroke capitals for the old tags, in a 0.7 x 1 box (y up):
        // the polylines a writer's hand follows, corners rounded by the
        // spline - a hand style, not a font.
        static readonly Dictionary<char, Vector2[][]> TagLetters = new Dictionary<char, Vector2[][]>
        {
            ['A'] = new[] { Path(0f, 0f, 0.33f, 1f, 0.66f, 0f), Path(0.14f, 0.4f, 0.52f, 0.4f) },
            ['D'] = new[] { Path(0f, 0f, 0f, 1f, 0.38f, 0.92f, 0.62f, 0.55f, 0.5f, 0.15f, 0f, 0f) },
            ['E'] = new[] { Path(0.6f, 1f, 0f, 1f, 0f, 0f, 0.6f, 0f), Path(0f, 0.52f, 0.45f, 0.52f) },
            ['K'] = new[] { Path(0f, 1f, 0f, 0f), Path(0.58f, 1f, 0.04f, 0.45f, 0.62f, 0f) },
            ['M'] = new[] { Path(0f, 0f, 0.06f, 1f, 0.36f, 0.35f, 0.66f, 1f, 0.72f, 0f) },
            ['N'] = new[] { Path(0f, 0f, 0.02f, 1f, 0.6f, 0f, 0.62f, 1f) },
            ['O'] = new[] { Path(0.35f, 1f, 0.05f, 0.8f, 0f, 0.4f, 0.2f, 0.03f, 0.5f, 0.05f, 0.66f, 0.45f, 0.58f, 0.85f, 0.35f, 1f, 0.2f, 0.94f) },
            ['R'] = new[] { Path(0f, 0f, 0f, 1f, 0.45f, 0.96f, 0.6f, 0.75f, 0.45f, 0.55f, 0.02f, 0.5f, 0.62f, 0f) },
            ['S'] = new[] { Path(0.6f, 0.88f, 0.35f, 1f, 0.06f, 0.88f, 0.08f, 0.6f, 0.5f, 0.45f, 0.62f, 0.18f, 0.35f, 0f, 0f, 0.12f) },
            ['T'] = new[] { Path(0f, 1f, 0.7f, 1f), Path(0.35f, 1f, 0.33f, 0f) },
            ['X'] = new[] { Path(0f, 1f, 0.64f, 0f), Path(0.64f, 1f, 0f, 0f) },
            ['Z'] = new[] { Path(0f, 1f, 0.62f, 1f, 0f, 0f, 0.66f, 0f) },
        };

        static readonly string[] TagWords = { "REKS", "KAOS", "ZEN", "DOSE", "NOX", "TREK", "MAZE", "ROK", "AXE", "SKOR" };

        static Vector2[] Path(params float[] xy)
        {
            var points = new Vector2[xy.Length / 2];
            for (var i = 0; i < points.Length; i++)
                points[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            return points;
        }

        // A few old, faded tags - slanted hand-style letters and sometimes an
        // underline swoosh - drawn with the player's own aerosol brush: the
        // spot reads as used, and nothing on the wall looks like a blob or a
        // marker line.
        void StampPreExistingGraffiti()
        {
            var rng = new System.Random(graffitiRandomSeed);
            var inks = new[]
            {
                new Color(0.08f, 0.08f, 0.09f),
                new Color(0.86f, 0.85f, 0.82f),
                new Color(0.6f, 0.62f, 0.66f),
                new Color(0.48f, 0.13f, 0.12f),
                new Color(0.13f, 0.22f, 0.45f),
            };

            // Laid out in round texels along the face as the room sees it
            // (faceRight / faceUp), so the letters read left to right and
            // keep their proportions on any face.
            var points = new List<Vector2>();
            var firstWord = rng.Next(TagWords.Length);
            for (var i = 0; i < preExistingGraffitiMarks; i++)
            {
                // Different writers: no word twice on one wall.
                var word = TagWords[(firstWord + i * 3) % TagWords.Length];
                var height = rng.Next(46, 76);
                var advance = height * 0.82f;
                var width = word.Length * advance;

                // Somewhere in the upper two thirds, wholly on the face.
                var halfX = (Mathf.Abs(faceRight.x) * width + Mathf.Abs(faceUp.x) * height) * 0.5f + 8f;
                var halfY = (Mathf.Abs(faceRight.y) * width + Mathf.Abs(faceUp.y) * height) * 0.5f + 8f;
                var center = new Vector2(
                    Mathf.Lerp(halfX, textureSize - halfX, (float)rng.NextDouble()),
                    Mathf.Lerp(Mathf.Max(halfY, textureSize * 0.35f), textureSize - halfY, (float)rng.NextDouble()));
                var origin = center - faceRight * (width * 0.5f) - faceUp * (height * 0.5f);

                var slant = 0.15f + (float)rng.NextDouble() * 0.2f;
                var radius = 2f + (float)rng.NextDouble() * 1.2f;
                var stamp = new SprayStamp { color = inks[rng.Next(inks.Length)], alpha = 1f, stretch = 1f };
                var fade = 0.5f + (float)rng.NextDouble() * 0.35f;

                for (var c = 0; c < word.Length; c++)
                {
                    if (!TagLetters.TryGetValue(word[c], out var strokes))
                        continue;

                    // Each letter sits a little off the line, a little off size.
                    var scale = height * (0.9f + (float)rng.NextDouble() * 0.2f);
                    var letterX = c * advance;
                    var letterY = ((float)rng.NextDouble() - 0.5f) * 0.16f * height;
                    foreach (var stroke in strokes)
                    {
                        points.Clear();
                        foreach (var p in stroke)
                        {
                            var x = letterX + (p.x + p.y * slant) * scale + ((float)rng.NextDouble() - 0.5f) * 0.05f * height;
                            var y = letterY + p.y * scale + ((float)rng.NextDouble() - 0.5f) * 0.05f * height;
                            points.Add(origin + faceRight * x + faceUp * y);
                        }

                        DrawPathTag(points, radius, in stamp, 1.3f * fade);
                    }
                }

                if (rng.NextDouble() < 0.5)
                {
                    points.Clear();
                    points.Add(origin + faceRight * (-0.1f * height) + faceUp * (-0.2f * height));
                    points.Add(origin + faceRight * (width * 0.5f) + faceUp * (-0.38f * height));
                    points.Add(origin + faceRight * (width + 0.25f * height) + faceUp * (-0.05f * height));
                    DrawPathTag(points, radius * 0.8f, in stamp, fade);
                }
            }
        }

        // Catmull-Rom through the points, stamped densely enough to be a line.
        void DrawPathTag(List<Vector2> points, float radius, in SprayStamp stamp, float deposit)
        {
            var spacing = Mathf.Max(0.8f, radius * 0.45f);
            var previous = points[0];
            for (var i = 0; i < points.Count - 1; i++)
            {
                var p0 = points[Mathf.Max(0, i - 1)];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = points[Mathf.Min(points.Count - 1, i + 2)];
                for (var s = 1; s <= 10; s++)
                {
                    var t = s / 10f;
                    var point = 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t + (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t);
                    var steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(previous, point) / spacing));
                    for (var k = 1; k <= steps; k++)
                    {
                        var at = Vector2.Lerp(previous, point, k / (float)steps);
                        StampSpray(at.x, at.y, radius, in stamp, deposit / Mathf.Max(1f, 2f * radius / spacing) * 2f);
                    }

                    previous = point;
                }
            }
        }

        // ---------- painting ----------

        void LateUpdate()
        {
            DryWetness(Time.deltaTime);
            UpdateDrips(Time.deltaTime);

            if (!dirty)
                return;

            if (overlay)
            {
                UploadPremultiplied();
            }
            else
            {
                paintTexture.SetPixels32(pixels);
                paintTexture.Apply(false, false);
            }

            dirty = false;
            Revision++;
        }

        // The overlay's texture is premultiplied (colour already scaled by its opacity), so
        // bilinear filtering at the edge of paint blends colour and opacity together instead of
        // pulling in the black of the bare, transparent texels. Only the texels that changed
        // since the last upload are converted.
        void UploadPremultiplied()
        {
            uploadPixels ??= new Color32[pixels.Length];
            if (touchMaxX >= touchMinX && touchMaxY >= touchMinY)
            {
                for (var y = touchMinY; y <= touchMaxY; y++)
                {
                    var row = y * textureSize;
                    for (var x = touchMinX; x <= touchMaxX; x++)
                    {
                        var p = pixels[row + x];
                        uploadPixels[row + x] = new Color32((byte)((p.r * p.a + 127) / 255), (byte)((p.g * p.a + 127) / 255), (byte)((p.b * p.a + 127) / 255), p.a);
                    }
                }
            }

            touchMinX = touchMinY = int.MaxValue;
            touchMaxX = touchMaxY = -1;
            paintTexture.SetPixels32(uploadPixels);
            paintTexture.Apply(false, false);
        }

        // The texels changed since the last upload (overlay surfaces only).
        void Touch(int minX, int minY, int maxX, int maxY)
        {
            if (!overlay)
                return;
            if (minX < touchMinX) touchMinX = minX;
            if (minY < touchMinY) touchMinY = minY;
            if (maxX > touchMaxX) touchMaxX = maxX;
            if (maxY > touchMaxY) touchMaxY = maxY;
        }

        void TouchAll() => Touch(0, 0, textureSize - 1, textureSize - 1);

        // Over-compositing in straight alpha: `cover` of the paint colour on top of what is there.
        static Color32 BlendOver(Color32 current, Color32 paint, float cover)
        {
            var a = current.a / 255f;
            var outA = cover + a * (1f - cover);
            if (outA <= 0.0005f)
                return default;
            var w = cover / outA;
            var alpha = ToByte(outA * 255f + 0.5f);
            if (alpha == 0)
                return default;
            return new Color32(
                ToByte(current.r + (paint.r - current.r) * w + 0.5f),
                ToByte(current.g + (paint.g - current.g) * w + 0.5f),
                ToByte(current.b + (paint.b - current.b) * w + 0.5f),
                alpha);
        }

        // The given texture, resampled to this surface's size (bilinear when sizes differ).
        void CopyBaseTexture()
        {
            if (!baseTexture.isReadable)
            {
                Debug.LogError($"{nameof(PaintSurface)} '{SurfaceId}': base texture '{baseTexture.name}' is not readable - using baseColor.", this);
                return;
            }

            if (baseTexture.width == textureSize && baseTexture.height == textureSize)
            {
                var source = baseTexture.GetPixels32();
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(source[i].r, source[i].g, source[i].b, 255);
                return;
            }

            for (var y = 0; y < textureSize; y++)
            {
                for (var x = 0; x < textureSize; x++)
                {
                    var c = baseTexture.GetPixelBilinear((x + 0.5f) / textureSize, (y + 0.5f) / textureSize);
                    pixels[y * textureSize + x] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), 255);
                }
            }
        }

        /// <summary>
        /// Replaces the whole paint with `source` (same size; a saved skin being loaded). Saved for
        /// undo when PaintHistory is recording. False if nothing changed.
        /// </summary>
        public bool ReplacePaint(Color32[] source)
        {
            if (source == null || source.Length != pixels.Length)
                return false;

            var changed = false;
            var tileCount = tilesPerRow * tilesPerRow;
            for (var tile = 0; tile < tileCount; tile++)
            {
                var x0 = tile % tilesPerRow * TileSize;
                var y0 = tile / tilesPerRow * TileSize;
                var x1 = Mathf.Min(textureSize, x0 + TileSize) - 1;
                var y1 = Mathf.Min(textureSize, y0 + TileSize) - 1;
                if (TileMatches(source, x0, y0, x1, y1))
                    continue;

                EnsureCaptured(x0, y0, x1, y1);
                for (var y = y0; y <= y1; y++)
                    System.Array.Copy(source, y * textureSize + x0, pixels, y * textureSize + x0, x1 - x0 + 1);
                changed = true;
            }

            StopRunningPaint();
            dirty |= changed;
            if (changed)
                TouchAll();
            return changed;
        }

        /// <summary>
        /// Puts saved paint on the surface without recording an undo step (a wall's artwork restored
        /// when it is found again): it is not something the player did, so B must not take it away.
        /// </summary>
        public bool ReplacePaintSilently(Color32[] source)
        {
            if (source == null || source.Length != pixels.Length)
                return false;

            System.Array.Copy(source, pixels, pixels.Length);
            StopRunningPaint();
            dirty = true;
            TouchAll();
            return true;
        }

        bool TileMatches(Color32[] source, int x0, int y0, int x1, int y1)
        {
            for (var y = y0; y <= y1; y++)
            {
                var row = y * textureSize;
                for (var x = x0; x <= x1; x++)
                {
                    if (!Same(pixels[row + x], source[row + x]))
                        return false;
                }
            }

            return true;
        }

        public void PaintSegment(Vector2 uvFrom, Vector2 uvTo, in SprayStamp stamp)
        {
            // The Cleaner reaches past the line it follows (its soft edge
            // and overspray) and dissolves faster than paint builds up.
            var radiusPixels = Mathf.Max(1.5f, stamp.radiusMeters * pixelsPerMeter * (stamp.erase ? cleanerReach : 1f));

            var from = new Vector2(uvFrom.x * textureSize, uvFrom.y * textureSize);
            var to = new Vector2(uvTo.x * textureSize, uvTo.y * textureSize);
            var distance = Vector2.Distance(Vector2.Scale(from, texelAspect), Vector2.Scale(to, texelAspect));

            // The tick's paint goes where the nozzle swept since the last
            // tick: stamps overlap so the line is continuous, and share the
            // paint between them - a fast sweep is thinner than a slow one.
            // Capped so a teleport across the wall cannot stall a frame.
            var spacing = Mathf.Max(1f, radiusPixels * 0.45f);
            var steps = Mathf.Clamp(Mathf.CeilToInt(distance / spacing), 1, 48);
            var deposit = stamp.alpha * (stamp.erase ? cleanerStrength : 1f) / steps;

            var wrapWidth = paintMaxX - paintMinX + 1;
            var wrapReach = radiusPixels * 1.3f * Mathf.Clamp(stamp.stretch, 1f, 4f) / texelAspect.x + 2f;
            for (var i = 1; i <= steps; i++)
            {
                var point = Vector2.Lerp(from, to, i / (float)steps);
                StampSpray(point.x, point.y, radiusPixels, in stamp, deposit);

                // Round a cylinder's seam: what spills over one edge of the
                // paintable rectangle lands at the other.
                if (wrapPaintableU)
                {
                    if (point.x - wrapReach < paintMinX)
                        StampSpray(point.x + wrapWidth, point.y, radiusPixels, in stamp, deposit);
                    if (point.x + wrapReach > paintMaxX)
                        StampSpray(point.x - wrapWidth, point.y, radiusPixels, in stamp, deposit);
                }
            }

            AccumulateWetness(to, radiusPixels, in stamp);
            dirty = true;
        }

        // One aerosol stamp. `deposit` is the paint at its centre (coverage
        // there becomes 1 - e^-deposit on bare wall).
        void StampSpray(float centerX, float centerY, float radius, in SprayStamp stamp, float deposit)
        {
            if (deposit <= 0f)
                return;

            stampSeed++;
            var stretch = Mathf.Clamp(stamp.stretch, 1f, 4f);
            const float fringe = 1.3f;

            // The shape is worked out in round texels (texelAspect), so it
            // lands on the wall round however the face's texels are shaped.
            var aspectX = texelAspect.x;
            var aspectY = texelAspect.y;
            var reach = radius * fringe * stretch;
            var reachX = Mathf.CeilToInt(reach / aspectX) + 1;
            var reachY = Mathf.CeilToInt(reach / aspectY) + 1;

            // Only inside the paintable rectangle (the whole texture on a wall).
            var minX = Mathf.Max(Mathf.FloorToInt(centerX) - reachX, paintMinX);
            var maxX = Mathf.Min(Mathf.CeilToInt(centerX) + reachX, paintMaxX);
            var minY = Mathf.Max(Mathf.FloorToInt(centerY) - reachY, paintMinY);
            var maxY = Mathf.Min(Mathf.CeilToInt(centerY) + reachY, paintMaxY);
            if (minX > maxX || minY > maxY)
                return;

            EnsureCaptured(minX, minY, maxX, maxY);

            var stretchAngle = Mathf.Atan2(Mathf.Sin(stamp.stretchAngle) * aspectY, Mathf.Cos(stamp.stretchAngle) * aspectX);
            var cos = Mathf.Cos(-stretchAngle);
            var sin = Mathf.Sin(-stretchAngle);
            var invRadius = 1f / radius;
            var erase = stamp.erase;
            var color = (Color)stamp.color;

            // Every stamp's own noise offsets: mottling and ragged edges
            // average out over many passes instead of printing one pattern.
            var ox = Hash01(stampSeed, 3, 101) * 64f;
            var oy = Hash01(stampSeed, 7, 103) * 64f;
            var edgeScale = Mathf.Clamp(6f / radius, 0.05f, 0.6f);
            var droplets = overspray * 0.02f;
            var keepRelief = overlay ? 0f : reliefThroughPaint;

            for (var y = minY; y <= maxY; y++)
            {
                var row = y * textureSize;
                var dy = (y - centerY) * aspectY;

                for (var x = minX; x <= maxX; x++)
                {
                    var dx = (x - centerX) * aspectX;

                    // Rotate into stamp space, then squash the long axis so an
                    // angled hit leaves an ellipse instead of a circle.
                    var localX = (dx * cos - dy * sin) / stretch;
                    var localY = dx * sin + dy * cos;
                    var d = Mathf.Sqrt(localX * localX + localY * localY) * invRadius;
                    if (d > fringe)
                        continue;

                    // Ragged edge: the stamp's radius wanders +-18% around it.
                    var ragged = 0.82f + 0.36f * SmoothNoise(x * edgeScale + ox, y * edgeScale + oy);
                    var r = d / ragged;
                    var amount = 0f;
                    if (r < 1f)
                    {
                        var q = 1f - r * r;
                        amount = q * q * deposit * (0.78f + 0.44f * SmoothNoise(x * 0.33f + oy, y * 0.33f + ox));
                    }

                    // Overspray: sparse droplets around and past the edge.
                    if (r > 0.55f && r < fringe && Hash01(x, y, stampSeed) < droplets * (1f - Mathf.Abs(r - 0.95f) * 2.2f))
                        amount += deposit * 0.9f + 0.15f;

                    if (amount < 0.0015f)
                        continue;

                    var coverage = 1f - Mathf.Exp(-amount);
                    var index = row + x;
                    var current = pixels[index];
                    var basePixel = default(Color32);
                    float tr, tg, tb;
                    if (erase)
                    {
                        basePixel = basePixels[index];
                        tr = basePixel.r;
                        tg = basePixel.g;
                        tb = basePixel.b;
                    }
                    else
                    {
                        var shade = 1f + ((0.6f + baseShade[index] * (0.8f / 255f)) - 1f) * keepRelief;
                        tr = color.r * 255f * shade;
                        tg = color.g * 255f * shade;
                        tb = color.b * 255f * shade;
                    }

                    // Stochastic rounding: a light pass still adds up over
                    // many stamps instead of rounding away.
                    var dither = Hash01(x, y, stampSeed * 7 + 1);
                    if (overlay)
                    {
                        // Over a transparent surface: opacity is the paint's coverage, colour
                        // is the paint's own (never dragged toward black by the bare surface).
                        var a = current.a / 255f;
                        if (erase)
                        {
                            var left = a * (1f - coverage);
                            pixels[index] = left * 255f < cleanerSnapLevels ? default : new Color32(current.r, current.g, current.b, ToByte(left * 255f + dither));
                        }
                        else
                        {
                            var outA = coverage + a * (1f - coverage);
                            var w = coverage / outA;
                            var alphaByte = ToByte(outA * 255f + dither);

                            // A texel with no opacity has no colour either: bare is bare (0,0,0,0),
                            // so "painted" means the same thing here, in a save file and on screen.
                            pixels[index] = alphaByte == 0 ? default : new Color32(
                                ToByte(current.r + (tr - current.r) * w + dither),
                                ToByte(current.g + (tg - current.g) * w + dither),
                                ToByte(current.b + (tb - current.b) * w + dither),
                                alphaByte);
                        }

                        continue;
                    }

                    var result = new Color32(
                        ToByte(current.r + (tr - current.r) * coverage + dither),
                        ToByte(current.g + (tg - current.g) * coverage + dither),
                        ToByte(current.b + (tb - current.b) * coverage + dither),
                        255);

                    // What the Cleaner leaves within a few levels of the bare
                    // wall is the bare wall: no tint that never quite goes.
                    if (erase && Mathf.Abs(result.r - basePixel.r) <= cleanerSnapLevels && Mathf.Abs(result.g - basePixel.g) <= cleanerSnapLevels && Mathf.Abs(result.b - basePixel.b) <= cleanerSnapLevels)
                        result = basePixel;

                    pixels[index] = result;
                }
            }

            Touch(minX, minY, maxX, maxY);
        }

        static byte ToByte(float value) => value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)value;

        // ---------- undo ----------

        // Saves every tile in the region that this action has not saved yet,
        // before anything writes to it.
        void EnsureCaptured(int minX, int minY, int maxX, int maxY)
        {
            var record = PaintHistory.Recording;
            if (record == null)
                return;

            if (record != captureRecord)
            {
                captureRecord = record;
                System.Array.Clear(capturedTiles, 0, capturedTiles.Length);
            }

            var tx0 = minX / TileSize;
            var tx1 = maxX / TileSize;
            var ty0 = minY / TileSize;
            var ty1 = maxY / TileSize;
            for (var ty = ty0; ty <= ty1; ty++)
            {
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    var tile = ty * tilesPerRow + tx;
                    if (capturedTiles[tile])
                        continue;

                    capturedTiles[tile] = true;
                    record.Save(this, SaveTile(tile), TileSize * TileSize * 4 + 48);
                }
            }
        }

        TileState SaveTile(int tile)
        {
            var buffer = TilePool.Count > 0 ? TilePool.Pop() : new Color32[TileSize * TileSize];
            var x0 = tile % tilesPerRow * TileSize;
            var y0 = tile / tilesPerRow * TileSize;
            var width = Mathf.Min(TileSize, textureSize - x0);
            var height = Mathf.Min(TileSize, textureSize - y0);
            for (var y = 0; y < height; y++)
                System.Array.Copy(pixels, (y0 + y) * textureSize + x0, buffer, y * TileSize, width);

            return new TileState { Owner = this, Tile = tile, Pixels = buffer };
        }

        public void RestoreUndoState(object state)
        {
            if (state is not TileState saved || saved.Owner != this || saved.Pixels == null)
                return;

            var x0 = saved.Tile % tilesPerRow * TileSize;
            var y0 = saved.Tile / tilesPerRow * TileSize;
            var width = Mathf.Min(TileSize, textureSize - x0);
            var height = Mathf.Min(TileSize, textureSize - y0);
            for (var y = 0; y < height; y++)
                System.Array.Copy(saved.Pixels, y * TileSize, pixels, (y0 + y) * textureSize + x0, width);

            Touch(x0, y0, x0 + width - 1, y0 + height - 1);
            StopRunningPaint();
            dirty = true;
        }

        public bool ClearPaint()
        {
            var cleared = false;
            var tileCount = tilesPerRow * tilesPerRow;
            for (var tile = 0; tile < tileCount; tile++)
            {
                var x0 = tile % tilesPerRow * TileSize;
                var y0 = tile / tilesPerRow * TileSize;
                var x1 = Mathf.Min(textureSize, x0 + TileSize) - 1;
                var y1 = Mathf.Min(textureSize, y0 + TileSize) - 1;
                if (TileIsUnpainted(x0, y0, x1, y1))
                    continue;

                EnsureCaptured(x0, y0, x1, y1);
                for (var y = y0; y <= y1; y++)
                    System.Array.Copy(basePixels, y * textureSize + x0, pixels, y * textureSize + x0, x1 - x0 + 1);
                cleared = true;
            }

            StopRunningPaint();
            dirty |= cleared;
            if (cleared)
                TouchAll();
            return cleared;
        }

        bool TileIsUnpainted(int x0, int y0, int x1, int y1)
        {
            for (var y = y0; y <= y1; y++)
            {
                var row = y * textureSize;
                for (var x = x0; x <= x1; x++)
                {
                    if (!Same(pixels[row + x], basePixels[row + x]))
                        return false;
                }
            }

            return true;
        }

        // Alpha is 255 everywhere on an ordinary surface; on an overlay it is part of the paint.
        static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        // Wet paint and running drips belong to what was just undone or
        // cleared: they stop with it.
        void StopRunningPaint()
        {
            System.Array.Clear(wetness, 0, wetness.Length);
            anyWetness = false;
            for (var i = 0; i < drips.Length; i++)
                drips[i].Active = false;
            activeDripCount = 0;
        }

        // ---------- noise ----------

        const int NoiseSize = 64;
        static float[] noiseTable;

        // Value noise on a wrapping 64x64 lattice, smoothly interpolated: no
        // blocks, no seams.
        static float SmoothNoise(float x, float y)
        {
            noiseTable ??= BuildNoiseTable();
            var xi = Mathf.FloorToInt(x);
            var yi = Mathf.FloorToInt(y);
            var fx = x - xi;
            var fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            var x0 = xi & (NoiseSize - 1);
            var y0 = yi & (NoiseSize - 1);
            var x1 = (x0 + 1) & (NoiseSize - 1);
            var y1 = (y0 + 1) & (NoiseSize - 1);
            var a = noiseTable[y0 * NoiseSize + x0];
            var b = noiseTable[y0 * NoiseSize + x1];
            var c = noiseTable[y1 * NoiseSize + x0];
            var d = noiseTable[y1 * NoiseSize + x1];
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }

        static float Fbm(float x, float y) =>
            SmoothNoise(x, y) * 0.55f + SmoothNoise(x * 2.03f + 19f, y * 2.03f + 7f) * 0.3f + SmoothNoise(x * 4.1f + 3f, y * 4.1f + 29f) * 0.15f;

        static float[] BuildNoiseTable()
        {
            var table = new float[NoiseSize * NoiseSize];
            var rng = new System.Random(7341);
            for (var i = 0; i < table.Length; i++)
                table[i] = (float)rng.NextDouble();
            return table;
        }

        static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                var h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h & 0xffffff) / 16777216f;
            }
        }

        // ---------- wet paint and drips ----------

        // Paint "mass" per coarse cell. Holding the spray in one place builds
        // up fast; a passing sweep barely registers - that difference is what
        // makes drips feel earned rather than random.
        void AccumulateWetness(Vector2 point, float radiusPixels, in SprayStamp stamp)
        {
            var cellX = Mathf.Clamp((int)(point.x / wetnessCellPixels), 0, wetnessWidth - 1);
            var cellY = Mathf.Clamp((int)(point.y / wetnessCellPixels), 0, wetnessWidth - 1);
            var index = cellY * wetnessWidth + cellX;

            if (stamp.erase)
            {
                wetness[index] = 0f;
                return;
            }

            wetness[index] += stamp.alpha * (radiusPixels / 18f);
            anyWetness = true;

            if (wetness[index] < dripThreshold)
                return;

            // How much paint built up past the threshold: a long hold on one
            // spot drips thicker and longer than just reaching it.
            var volume = Mathf.Clamp(wetness[index] / dripThreshold, 1f, 2f);
            wetness[index] = 0f;
            SpawnDrip(point, radiusPixels, in stamp, volume);
        }

        void SpawnDrip(Vector2 point, float radiusPixels, in SprayStamp stamp, float volume)
        {
            // Level surface (floor, ceiling): the paint pools, it does not run.
            if (stamp.slope < minDripSlope || stamp.downhillUv.sqrMagnitude < 1e-12f || activeDripCount >= drips.Length)
                return;

            // UV and texel space share the same axes (square texture).
            var direction = stamp.downhillUv.normalized;
            var across = new Vector2(-direction.y, direction.x);

            for (var i = 0; i < drips.Length; i++)
            {
                if (drips[i].Active)
                    continue;

                // Born at the lower edge of the paint that built up.
                var start = point + direction * (radiusPixels * 0.7f) + across * (Random.Range(-radiusPixels, radiusPixels) * 0.4f);
                drips[i] = new Drip
                {
                    X = start.x,
                    Y = start.y,
                    DirX = direction.x,
                    DirY = direction.y,
                    Speed = dripSpeedPixelsPerSecond * stamp.slope,
                    Width = Mathf.Max(1f, radiusPixels * Random.Range(0.12f, 0.26f) * Mathf.Sqrt(volume)),
                    Alpha = Mathf.Clamp01(Mathf.Max(0.35f, stamp.alpha) * Random.Range(0.7f, 1f)),
                    Life = dripLifeSeconds * Random.Range(0.6f, 1f) * volume,
                    Color = stamp.color,
                    Active = true,
                };

                activeDripCount++;
                return;
            }
        }

        void DryWetness(float deltaTime)
        {
            if (!anyWetness)
                return;

            var keep = Mathf.Exp(-deltaTime / Mathf.Max(0.01f, wetnessDrySeconds));
            var stillWet = false;
            for (var i = 0; i < wetness.Length; i++)
            {
                if (wetness[i] <= 0f)
                    continue;

                wetness[i] = wetness[i] < 0.01f ? 0f : wetness[i] * keep;
                stillWet |= wetness[i] > 0f;
            }

            anyWetness = stillWet;
        }

        void UpdateDrips(float deltaTime)
        {
            if (drips == null || activeDripCount == 0)
                return;

            for (var i = 0; i < drips.Length; i++)
            {
                if (!drips[i].Active)
                    continue;

                var drip = drips[i];
                var startX = drip.X;
                var startY = drip.Y;

                var step = drip.Speed * deltaTime;
                drip.X += drip.DirX * step;
                drip.Y += drip.DirY * step;
                drip.Life -= deltaTime;

                // Thins and fades as it runs out of paint, so it tapers to a
                // stop instead of ending in a hard line.
                var fade = Mathf.Clamp01(drip.Life / dripLifeSeconds);
                DrawDripSegment(startX, startY, drip.X, drip.Y, drip.Width * fade, drip.Alpha * fade * 0.5f, drip.Color);

                var outOfBounds = drip.X < 0f || drip.Y < 0f || drip.X >= textureSize || drip.Y >= textureSize;
                if (drip.Life <= 0f || outOfBounds)
                {
                    drip.Active = false;
                    activeDripCount--;
                }

                drips[i] = drip;
                dirty = true;
            }
        }

        void DrawDripSegment(float x0, float y0, float x1, float y1, float width, float alpha, Color32 color)
        {
            if (alpha <= 0.004f)
                return;

            var steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(new Vector2(x0, y0), new Vector2(x1, y1))), 1, 12);
            // `width` is the run's full width: a real run is a few
            // millimetres, so at most a texel or two either side.
            var halfWidth = Mathf.Max(1, Mathf.RoundToInt(width * 0.5f));

            // A drip still running after its stroke ended is part of the
            // newest action: saved before it paints, undone with it.
            EnsureCaptured(
                Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(x0, x1)) - halfWidth - 1, 0, textureSize - 1),
                Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(y0, y1)) - 1, 0, textureSize - 1),
                Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(x0, x1)) + halfWidth + 1, 0, textureSize - 1),
                Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(y0, y1)) + 1, 0, textureSize - 1));

            for (var s = 0; s <= steps; s++)
            {
                var t = s / (float)steps;
                var px = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
                var py = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));

                if (py < paintMinY || py > paintMaxY)
                    continue;

                var row = py * textureSize;
                for (var w = -halfWidth; w <= halfWidth; w++)
                {
                    var x = px + w;
                    if (x < paintMinX || x > paintMaxX)
                        continue;

                    var edge = 1f - Mathf.Abs(w) / (float)(halfWidth + 1);
                    var index = row + x;
                    pixels[index] = overlay ? BlendOver(pixels[index], color, alpha * edge) : Color32.Lerp(pixels[index], color, alpha * edge);
                }
            }

            Touch(
                Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(x0, x1)) - halfWidth - 1, 0, textureSize - 1),
                Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(y0, y1)) - 1, 0, textureSize - 1),
                Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(x0, x1)) + halfWidth + 1, 0, textureSize - 1),
                Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(y0, y1)) + 1, 0, textureSize - 1));
        }
    }
}
