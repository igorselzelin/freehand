using Freehand.Audio;
using Freehand.Tools;
using UnityEngine;

namespace Freehand.Customization
{
    /// <summary>
    /// The workbench's controls, all physical: SALVAR keeps the can as it is
    /// now (into the can in use, or as a new one on the shelf), LIMPAR takes the
    /// paint off (B brings it back), NOME gives it another name. A small plate
    /// shows the can's name, and a lamp beside it is amber while there is paint
    /// that is not saved, green once it is.
    /// </summary>
    public class CanWorkshop : MonoBehaviour
    {
        [SerializeField] CanSkin skin;
        [SerializeField] CanShelf shelf;
        [SerializeField] WorkshopButton saveButton;
        [SerializeField] WorkshopButton clearButton;
        [SerializeField] WorkshopButton nameButton;

        [Tooltip("V0.14: deletes the saved can in use - after a second press within confirmSeconds.")]
        [SerializeField] WorkshopButton deleteButton;
        [SerializeField] float confirmSeconds = 3f;
        [SerializeField] Renderer nameLabel;
        [SerializeField] Renderer lamp;

        [SerializeField] Color savedColor = new Color(0.35f, 1f, 0.45f);
        [SerializeField] Color unsavedColor = new Color(1f, 0.65f, 0.15f);
        [SerializeField] Color warningColor = new Color(1f, 0.22f, 0.18f);

        [Tooltip("Seconds between two checks of whether the paint on the can is saved.")]
        [SerializeField] float checkInterval = 0.3f;

        const int LabelWidth = 512;
        const int LabelHeight = 96;

        [System.NonSerialized] Texture2D labelTexture;
        [System.NonSerialized] Color32[] labelPixels;
        Material lampMaterial;
        string shownName;
        bool shownSaved;
        float nextCheck;
        int lastRevision = -1;
        float flash;

        // V0.14: what the plate says for a moment instead of the state (a refusal, a pending delete).
        string notice;
        float noticeUntil;
        float deleteArmedUntil = -1f;

        void OnEnable()
        {
            if (saveButton != null) saveButton.Pressed += OnSave;
            if (clearButton != null) clearButton.Pressed += OnClear;
            if (nameButton != null) nameButton.Pressed += OnName;
            if (deleteButton != null) deleteButton.Pressed += OnDelete;
            if (skin != null) skin.Changed += Refresh;
        }

        void OnDisable()
        {
            if (saveButton != null) saveButton.Pressed -= OnSave;
            if (clearButton != null) clearButton.Pressed -= OnClear;
            if (nameButton != null) nameButton.Pressed -= OnName;
            if (deleteButton != null) deleteButton.Pressed -= OnDelete;
            if (skin != null) skin.Changed -= Refresh;
        }

        void Start()
        {
            if (nameLabel != null)
            {
                labelTexture = new Texture2D(LabelWidth, LabelHeight, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "WorkshopNameLabel" };
                labelPixels = new Color32[LabelWidth * LabelHeight];
                nameLabel.material.SetTexture("_BaseMap", labelTexture);
            }

            if (lamp != null)
                lampMaterial = lamp.material;
            Refresh();
        }

        void Update()
        {
            if (skin == null || skin.Canvas == null)
                return;

            // Painted since the last look: is it still saved?
            if (Time.unscaledTime >= nextCheck && skin.Canvas.Revision != lastRevision)
            {
                lastRevision = skin.Canvas.Revision;
                nextCheck = Time.unscaledTime + checkInterval;
                Refresh();
            }

            if (flash > 0f)
            {
                flash -= Time.deltaTime;
                UpdateLamp();
            }

            // A notice or a pending delete runs out: the plate goes back to the state of the can.
            if ((notice != null && Time.unscaledTime >= noticeUntil) || (deleteArmedUntil > 0f && Time.unscaledTime >= deleteArmedUntil))
            {
                notice = null;
                deleteArmedUntil = -1f;
                shownName = null;
                Refresh();
            }
        }

        /// <summary>V0.14: what the plate shows as the state, now (diagnostics, tests).</summary>
        public string PlateState { get; private set; } = "";

        /// <summary>V0.14: the buttons, for tests and diagnostics (the same as a finger press).</summary>
        public void PressSave() => OnSave();
        public void PressDelete() => OnDelete();

        void Notify(string text, float seconds)
        {
            notice = text;
            noticeUntil = Time.unscaledTime + seconds;
            shownName = null;
            Refresh();
        }

        // First press: asks (the plate says what would go, the lamp turns red). Second press within confirmSeconds:
        // deletes. The plain can has nothing to delete.
        void OnDelete()
        {
            if (skin == null)
                return;

            var sfx = SprayCanSfx.Instance;
            if (skin.Active == null)
            {
                deleteArmedUntil = -1f;
                Notify("PADRAO FICA", 2f);
                if (sfx != null)
                    sfx.PlayCapTouch(transform.position, 0.2f);
                return;
            }

            if (deleteArmedUntil < 0f || Time.unscaledTime > deleteArmedUntil)
            {
                deleteArmedUntil = Time.unscaledTime + confirmSeconds;
                notice = null;
                shownName = null;
                Refresh();
                if (sfx != null)
                    sfx.PlayCapTouch(transform.position, 0.35f);
                return;
            }

            deleteArmedUntil = -1f;
            var name = skin.Active.name;
            if (skin.DeleteActive())
            {
                if (sfx != null)
                    sfx.PlayClearAll(transform.position);
                if (shelf != null)
                    shelf.Rebuild();
                Notify(name + " APAGADA", 2f);
            }
        }

        void OnSave()
        {
            if (skin == null)
                return;

            deleteArmedUntil = -1f;
            if (skin.Save())
            {
                flash = 0.6f;
                var sfx = SprayCanSfx.Instance;
                if (sfx != null)
                    sfx.PlayAt(SfxBank.OrbConfirm, SfxCategory.Feedback, 0.7f, transform.position, 0.95f);
                if (shelf != null)
                    shelf.Rebuild();
                Notify("SALVA", 1.2f);
            }
            else if (skin.IsFull && skin.Active == null)
            {
                // Nothing is replaced: the player chooses what goes.
                Notify("CHEIA: APAGUE 1", 2.5f);
                var sfx = SprayCanSfx.Instance;
                if (sfx != null)
                    sfx.PlayCapTouch(transform.position, 0.3f);
            }
        }

        void OnClear()
        {
            deleteArmedUntil = -1f;
            if (skin != null && skin.ClearPaint())
            {
                var sfx = SprayCanSfx.Instance;
                if (sfx != null)
                    sfx.PlayClearAll(transform.position);

                // V0.15: LIMPAR only takes the paint off the can in front of you (B brings it back); APAGAR deletes a
                // saved can; Clear All is the walls. The plate says which one just happened.
                Notify("LIMPA: B DESFAZ", 2f);
            }
        }

        void OnName()
        {
            if (skin == null)
                return;
            deleteArmedUntil = -1f;
            skin.CycleName();
            flash = 0.25f;
            if (shelf != null)
                shelf.Rebuild();
        }

        void Refresh()
        {
            if (skin == null)
                return;

            var name = skin.DisplayName ?? "";
            var saved = !skin.HasUnsavedPaint && skin.Active != null;
            var armed = deleteArmedUntil > 0f && Time.unscaledTime < deleteArmedUntil;

            // What the plate says, most urgent first: a delete waiting for its second press, a notice, the state.
            string state;
            if (armed)
            {
                name = "APAGAR " + name;
                state = "CONFIRME";
            }
            else if (notice != null)
            {
                state = notice;
            }
            else if (skin.Active == null)
            {
                state = skin.IsFull && skin.HasUnsavedPaint ? "NOVA - CHEIA" : "NOVA";
            }
            else
            {
                state = saved ? "SALVA" : "ALTERADA";
            }

            if (name != shownName || saved != shownSaved || state != PlateState)
            {
                shownName = name;
                shownSaved = saved;
                PlateState = state;
                DrawLabel(name, state);
            }

            UpdateLamp();
        }

        void DrawLabel(string name, string state)
        {
            if (labelTexture == null)
                return;

            var background = new Color32(24, 25, 27, 255);
            for (var i = 0; i < labelPixels.Length; i++)
                labelPixels[i] = background;

            // The state on the right, the name in what is left (longer texts - a pending delete, a notice - shrink
            // to fit instead of running into each other).
            var stateHeight = Mathf.Min(24f, (LabelWidth * 0.48f) / Mathf.Max(0.1f, StencilText.Measure(state)));
            var stateX = LabelWidth - 18f - StencilText.Measure(state) * stateHeight;
            var nameHeight = Mathf.Min(46f, (stateX - 34f) / Mathf.Max(0.1f, StencilText.Measure(name)));
            var warning = state == "CONFIRME" || state.StartsWith("CHEIA") || state.EndsWith("APAGADA");
            StencilText.Draw(labelPixels, LabelWidth, LabelHeight, name, new Vector2(18f, 30f + (46f - nameHeight) * 0.5f), nameHeight, Mathf.Max(3f, 7f * nameHeight / 46f), new Color32(236, 232, 220, 255));
            StencilText.Draw(labelPixels, LabelWidth, LabelHeight, state, new Vector2(stateX, 34f), stateHeight, 4f, warning ? new Color32(240, 120, 100, 255) : new Color32(150, 150, 145, 255));
            labelTexture.SetPixels32(labelPixels);
            labelTexture.Apply(false, false);
        }

        void UpdateLamp()
        {
            if (lampMaterial == null)
                return;

            var warning = (deleteArmedUntil > 0f && Time.unscaledTime < deleteArmedUntil) || (notice != null && notice.StartsWith("CHEIA"));
            var color = warning ? warningColor : (shownSaved ? savedColor : unsavedColor);
            var intensity = 1.2f + (flash > 0f ? 2.5f * flash : 0f);
            lampMaterial.SetColor("_BaseColor", color);
            lampMaterial.SetColor("_EmissionColor", color * intensity);
        }

        void OnDestroy()
        {
            if (labelTexture != null)
                Destroy(labelTexture);
        }
    }
}
