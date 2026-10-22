using Freehand.Tools;
using UnityEngine;

namespace Freehand.Customization
{
    /// <summary>
    /// "Minhas latas": the saved cans, each as a small replica of the can in its
    /// own paint, standing on a ledge behind the bench, its name on a label
    /// under it. The first place is the plain Freehand can. Touch a replica
    /// (the same gesture as picking a colour at the station) and the can takes
    /// that look and becomes the one in use - in the workshop, the hand, the
    /// game, and the next session.
    /// </summary>
    public class CanShelf : ProximityRack
    {
        [SerializeField] CanSkin skin;

        [Tooltip("One per slot (slot 0 = the plain can): the replica's renderer.")]
        [SerializeField] Renderer[] replicas;

        [Tooltip("One per slot: the name label under it.")]
        [SerializeField] Renderer[] labels;

        [Tooltip("Pixels of the replicas' preview textures (a fraction of the can's).")]
        [SerializeField] int previewSize = 256;

        const int LabelWidth = 256;
        const int LabelHeight = 64;

        // Runtime caches: never serialized (a domain reload would turn a null
        // array into an empty one).
        [System.NonSerialized] Texture2D[] previews;
        [System.NonSerialized] Texture2D[] labelTextures;
        [System.NonSerialized] Color32[] labelPixels;
        [System.NonSerialized] Color32[] previewPixels;
        [System.NonSerialized] bool built;

        protected override void OnEnable()
        {
            base.OnEnable();
            if (skin != null)
                skin.Changed += OnSkinChanged;
        }

        void OnDisable()
        {
            if (skin != null)
                skin.Changed -= OnSkinChanged;
        }

        void OnSkinChanged()
        {
            if (!built)
                Rebuild();
            else
                ShowActive();
        }

        protected override void OnSlotSelected(int index)
        {
            if (skin == null)
                return;

            if (index == 0)
            {
                skin.Load(null);
                return;
            }

            var entries = skin.Index.skins;
            if (index - 1 < entries.Count)
                skin.Load(entries[index - 1]);
        }

        /// <summary>Redraws every replica and label from the saved cans.</summary>
        public void Rebuild()
        {
            if (skin == null || skin.Canvas == null || skin.Canvas.Pixels == null || replicas == null)
                return;

            built = true;
            var count = replicas.Length;
            if (previews == null || previews.Length != count)
            {
                previews = new Texture2D[count];
                labelTextures = new Texture2D[count];
            }

            var entries = skin.Index.skins;
            for (var i = 0; i < count; i++)
            {
                var replica = replicas[i];
                if (replica == null)
                    continue;

                var entry = i == 0 ? null : (i - 1 < entries.Count ? entries[i - 1] : null);
                var occupied = i == 0 || entry != null;

                // The whole replica (body, valve, cap): an empty place is empty.
                var root = slots != null && i < slots.Length && slots[i] != null ? slots[i].gameObject : replica.gameObject;
                root.SetActive(occupied);
                if (labels != null && i < labels.Length && labels[i] != null)
                    labels[i].gameObject.SetActive(occupied);
                if (!occupied)
                    continue;

                // The replica in the can's own colours (base + that can's paint).
                Color32[] source;
                if (entry == null)
                    source = skin.Canvas.BasePixels;
                else if (!skin.Compose(entry, out source))
                    source = skin.Canvas.BasePixels;
                SetPreview(i, source, skin.Canvas.TextureSize);
                SetLabel(i, entry == null ? "PADRAO" : entry.name);
            }

            ShowActive();
        }

        void ShowActive()
        {
            var active = skin != null ? skin.Active : null;
            var index = 0;
            if (active != null)
                index = skin.Index.skins.IndexOf(active) + 1;
            Visualize(index);
        }

        void SetPreview(int slot, Color32[] source, int sourceSize)
        {
            if (previews[slot] == null)
            {
                previews[slot] = new Texture2D(previewSize, previewSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "CanPreview" + slot };
                replicas[slot].material.SetTexture("_BaseMap", previews[slot]);
            }

            if (previewPixels == null || previewPixels.Length != previewSize * previewSize)
                previewPixels = new Color32[previewSize * previewSize];

            // Box-filtered down from the can's texture.
            var step = Mathf.Max(1, sourceSize / previewSize);
            for (var y = 0; y < previewSize; y++)
            {
                for (var x = 0; x < previewSize; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (var sy = 0; sy < step; sy++)
                    {
                        var row = (y * step + sy) * sourceSize;
                        for (var sx = 0; sx < step; sx++)
                        {
                            var c = source[row + x * step + sx];
                            r += c.r;
                            g += c.g;
                            b += c.b;
                        }
                    }

                    var n = step * step;
                    previewPixels[y * previewSize + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }

            previews[slot].SetPixels32(previewPixels);
            previews[slot].Apply(false, false);
        }

        void SetLabel(int slot, string text)
        {
            if (labels == null || slot >= labels.Length || labels[slot] == null)
                return;

            if (labelTextures[slot] == null)
            {
                labelTextures[slot] = new Texture2D(LabelWidth, LabelHeight, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "CanLabel" + slot };
                labels[slot].material.SetTexture("_BaseMap", labelTextures[slot]);
            }

            if (labelPixels == null || labelPixels.Length != LabelWidth * LabelHeight)
                labelPixels = new Color32[LabelWidth * LabelHeight];
            var paper = new Color32(214, 206, 186, 255);
            for (var i = 0; i < labelPixels.Length; i++)
                labelPixels[i] = paper;

            var height = 34f;
            var width = StencilText.Measure(text) * height;
            StencilText.Draw(labelPixels, LabelWidth, LabelHeight, text, new Vector2((LabelWidth - width) * 0.5f, 15f), height, 5.5f, new Color32(30, 30, 32, 255));
            labelTextures[slot].SetPixels32(labelPixels);
            labelTextures[slot].Apply(false, false);
        }

        void OnDestroy()
        {
            if (previews != null)
                foreach (var t in previews)
                    if (t != null) Destroy(t);
            if (labelTextures != null)
                foreach (var t in labelTextures)
                    if (t != null) Destroy(t);
        }
    }
}
