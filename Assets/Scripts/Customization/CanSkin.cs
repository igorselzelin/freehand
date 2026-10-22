using System;
using Freehand.Paint;
using UnityEngine;

namespace Freehand.Customization
{
    /// <summary>
    /// The player's can as theirs: what is painted on it, under which name, and
    /// whether that is saved. The paint itself is an ordinary PaintSurface (the
    /// workshop's canvas, which shows on this can's body), so it is laid down by
    /// the same spray, undone by the same B, and never touched by Clear All.
    ///
    ///   can model -> base material (Freehand aluminium) -> the skin's paint ->
    ///   what is seen in the hand, on the stand, in an inspection
    ///
    /// Paint is not metal: where the can is painted, a small metallic/smoothness
    /// map turns the aluminium's sheen into paint's (updated a few times a
    /// second while paint changes, never per frame).
    /// </summary>
    public class CanSkin : MonoBehaviour
    {
        public const int MaxSaved = 6;

        static readonly string[] Names = { "KAOS", "REKS", "LUMA", "ZETA", "FLUX", "VOLT", "ECHO", "DRIP", "NEON", "RUST", "ORBE", "TAGS" };

        [Tooltip("The workshop canvas that paints this can's body (its target renderer is this can's body).")]
        [SerializeField] PaintSurface canvas;

        [Tooltip("The base material's metallic (R) and smoothness (A), same UV layout as the can (readable).")]
        [SerializeField] Texture2D baseMetalMap;

        [SerializeField] int metalMapSize = 128;
        [SerializeField] [Range(0f, 1f)] float paintMetallic = 0.05f;
        [SerializeField] [Range(0f, 1f)] float paintSmoothness = 0.42f;

        [Tooltip("Seconds between two updates of the metal map while paint is changing.")]
        [SerializeField] float metalMapInterval = 0.25f;

        CanSkinIndex index;
        CanSkinEntry active;
        string pendingName;
        uint savedHash;
        int lastRevision = -1;
        float nextMetalUpdate;
        bool metalDirty;
        [NonSerialized] Texture2D metalMap;
        [NonSerialized] Color32[] metalPixels;
        [NonSerialized] Color32[] baseMetal;
        [NonSerialized] Color32[] scratch;
        int nameCursor;

        /// <summary>Something about the skin changed (paint saved or loaded, name, list of saved cans).</summary>
        public event Action Changed;

        public PaintSurface Canvas => canvas;
        public CanSkinIndex Index => index;

        /// <summary>The saved can in use, or null for the plain Freehand can.</summary>
        public CanSkinEntry Active => active;

        public string DisplayName => active != null ? active.name : pendingName;

        /// <summary>Paint on the can that is not saved yet.</summary>
        public bool HasUnsavedPaint => canvas != null && canvas.Pixels != null && canvas.PixelHash() != savedHash;

        /// <summary>V0.14: every place on the shelf is taken - a new can cannot be saved until one is deleted.</summary>
        public bool IsFull => index != null && index.skins.Count >= MaxSaved;

        void Awake()
        {
            index = CanSkinStore.LoadIndex();
            pendingName = NextFreeName();
        }

        void Start()
        {
            if (canvas == null || canvas.Pixels == null)
                return;

            BuildMetalMap();

            // The can the player was using last time.
            var last = index.Find(index.activeSkinId);
            if (last != null && Compose(last, out var composed))
            {
                canvas.ReplacePaint(composed);
                active = last;
            }

            savedHash = canvas.PixelHash();
            metalDirty = true;
            Changed?.Invoke();
        }

        void Update()
        {
            if (canvas == null)
                return;

            if (canvas.Revision != lastRevision)
            {
                lastRevision = canvas.Revision;
                metalDirty = true;
            }

            if (metalDirty && Time.unscaledTime >= nextMetalUpdate)
            {
                metalDirty = false;
                nextMetalUpdate = Time.unscaledTime + metalMapInterval;
                UpdateMetalMap();
            }
        }

        // ---------- the player's commands ----------

        /// <summary>Saves the paint on the can: into the can in use, or as a new saved can. False if it could not be written.</summary>
        public bool Save()
        {
            if (canvas == null)
                return false;

            var entry = active;
            if (entry == null)
            {
                // V0.14: a full shelf is full. Saving a new can never throws away one the player made (until V0.13
                // the oldest was replaced without a word); the workshop says so, and APAGAR makes room.
                if (IsFull)
                    return false;

                entry = new CanSkinEntry { id = CanSkinStore.NewId(), name = pendingName, baseMaterial = CanSkinStore.BaseMaterialId, created = CanSkinStore.Now() };
            }

            entry.modified = CanSkinStore.Now();
            entry.baseMaterial = CanSkinStore.BaseMaterialId;
            if (CanSkinStore.WritePaint(entry, canvas.Pixels, canvas.BasePixels, canvas.TextureSize) < 0)
                return false;

            if (!index.skins.Contains(entry))
                index.skins.Add(entry);
            active = entry;
            index.activeSkinId = entry.id;
            CanSkinStore.SaveIndex(index);
            savedHash = canvas.PixelHash();
            Changed?.Invoke();
            return true;
        }

        /// <summary>Puts a saved can's paint on the can (or the plain can, for null) and makes it the one in use. Undoable with B.</summary>
        public bool Load(CanSkinEntry entry)
        {
            if (canvas == null)
                return false;

            Color32[] target;
            if (entry == null)
                target = canvas.BasePixels;
            else if (!Compose(entry, out target))
                return false;

            PaintHistory.Run(entry != null ? "Lata: " + entry.name : "Lata padrao", () => canvas.ReplacePaint(target));
            active = entry;
            if (entry == null)
                pendingName = NextFreeName();
            index.activeSkinId = entry != null ? entry.id : "";
            CanSkinStore.SaveIndex(index);
            savedHash = canvas.PixelHash();
            metalDirty = true;
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// V0.14: deletes the saved can in use - its file and its place on the shelf. The paint stays on the can, now
        /// unsaved, so nothing on screen vanishes and it can be saved again. The plain can cannot be deleted. Not
        /// undoable (it is a file), which is why the workshop asks for a second press.
        /// </summary>
        public bool DeleteActive()
        {
            if (active == null || index == null)
                return false;

            CanSkinStore.DeletePaint(active);
            index.skins.Remove(active);
            index.activeSkinId = "";
            active = null;
            pendingName = NextFreeName();
            CanSkinStore.SaveIndex(index);
            savedHash = canvas != null && canvas.Pixels != null ? ~canvas.PixelHash() : 0u;
            Changed?.Invoke();
            return true;
        }

        /// <summary>Takes the paint off the can (the can in use stays selected until saved). Undoable with B.</summary>
        public bool ClearPaint()
        {
            if (canvas == null)
                return false;
            var cleared = PaintHistory.Run("Limpar lata", () => canvas.ClearPaint());
            metalDirty |= cleared;
            Changed?.Invoke();
            return cleared;
        }

        /// <summary>The next name in the list, for the can in use (kept) or the next one to be saved.</summary>
        public void CycleName()
        {
            nameCursor = (nameCursor + 1) % Names.Length;
            var name = Names[nameCursor];
            if (active != null)
            {
                active.name = name;
                CanSkinStore.SaveIndex(index);
            }
            else
            {
                pendingName = name;
            }

            Changed?.Invoke();
        }

        /// <summary>A saved can's full texture (paint over the current base), for the shelf's previews.</summary>
        public bool Compose(CanSkinEntry entry, out Color32[] composed)
        {
            composed = scratch != null && scratch.Length == canvas.Pixels.Length ? scratch : new Color32[canvas.Pixels.Length];
            scratch = composed;
            var ok = CanSkinStore.ReadPaint(entry, canvas.BasePixels, composed, canvas.TextureSize);
            if (!ok)
                return false;

            // A copy: scratch is reused by the next call.
            composed = (Color32[])composed.Clone();
            return true;
        }

        string NextFreeName()
        {
            for (var i = 0; i < Names.Length; i++)
            {
                var candidate = Names[(nameCursor + i) % Names.Length];
                if (index == null || index.skins.Find(s => s.name == candidate) == null)
                {
                    nameCursor = (nameCursor + i) % Names.Length;
                    return candidate;
                }
            }

            return Names[nameCursor];
        }

        // ---------- paint is not metal ----------

        void BuildMetalMap()
        {
            var renderer = canvas.TargetRenderer;
            if (renderer == null)
                return;

            metalMap = new Texture2D(metalMapSize, metalMapSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "CanSkinMetal" };
            metalPixels = new Color32[metalMapSize * metalMapSize];
            baseMetal = new Color32[metalPixels.Length];
            for (var y = 0; y < metalMapSize; y++)
            {
                for (var x = 0; x < metalMapSize; x++)
                {
                    var c = baseMetalMap != null && baseMetalMap.isReadable
                        ? baseMetalMap.GetPixelBilinear((x + 0.5f) / metalMapSize, (y + 0.5f) / metalMapSize)
                        : new Color(0.7f, 0f, 0f, 0.55f);
                    baseMetal[y * metalMapSize + x] = c;
                }
            }

            var material = renderer.material;
            material.SetTexture("_MetallicGlossMap", metalMap);
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
            material.SetFloat("_Smoothness", 1f);
        }

        void UpdateMetalMap()
        {
            if (metalMap == null)
                return;

            var pixels = canvas.Pixels;
            var basePixels = canvas.BasePixels;
            var size = canvas.TextureSize;
            var block = Mathf.Max(1, size / metalMapSize);
            var samples = block * block;
            var paintMetal = (byte)(paintMetallic * 255f);
            var paintSmooth = (byte)(paintSmoothness * 255f);
            for (var my = 0; my < metalMapSize; my++)
            {
                for (var mx = 0; mx < metalMapSize; mx++)
                {
                    var painted = 0;
                    var y0 = my * block;
                    var x0 = mx * block;
                    for (var y = y0; y < y0 + block; y++)
                    {
                        var row = y * size;
                        for (var x = x0; x < x0 + block; x++)
                        {
                            var p = pixels[row + x];
                            var b = basePixels[row + x];
                            if (p.r != b.r || p.g != b.g || p.b != b.b)
                                painted++;
                        }
                    }

                    var coverage = painted / (float)samples;
                    var m = baseMetal[my * metalMapSize + mx];
                    metalPixels[my * metalMapSize + mx] = new Color32(
                        (byte)Mathf.Lerp(m.r, paintMetal, coverage), 0, 0,
                        (byte)Mathf.Lerp(m.a, paintSmooth, coverage));
                }
            }

            metalMap.SetPixels32(metalPixels);
            metalMap.Apply(false, false);
        }

        void OnDestroy()
        {
            if (metalMap != null)
                Destroy(metalMap);
        }
    }
}
