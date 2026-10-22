using Freehand.Customization;
using Freehand.Tools;
using UnityEngine;

namespace Freehand.MR
{
    /// <summary>
    /// The small board beside the tool table: the way in and out of mixed reality, and what
    /// is going on, in words (no menu, no Canvas - the same stencil letters as the workshop).
    ///
    ///   MODO   - switches between the virtual room and the real room (passthrough)
    ///   SALA   - in the real room: asks the Quest to scan the room again (its Space Setup)
    ///
    /// The plate says the mode, the state of the wall workflow (permission, searching,
    /// how many walls) and, when something is wrong, what and what to do. The lamp is
    /// green when walls are ready, amber while it works or searches, red when something
    /// needs the player.
    /// </summary>
    public class MRPanel : MonoBehaviour
    {
        [SerializeField] MRModeController controller;
        [SerializeField] WorkshopButton modeButton;
        [SerializeField] WorkshopButton scanButton;
        [SerializeField] Renderer plate;
        [SerializeField] Renderer lamp;

        [SerializeField] Color readyColor = new Color(0.35f, 1f, 0.45f);
        [SerializeField] Color busyColor = new Color(1f, 0.65f, 0.15f);
        [SerializeField] Color problemColor = new Color(1f, 0.25f, 0.2f);

        const int PlateWidth = 512;
        const int PlateHeight = 256;

        [System.NonSerialized] Texture2D texture;
        [System.NonSerialized] Color32[] pixels;
        Material lampMaterial;
        string shownKey;
        float nextRefresh;

        void OnEnable()
        {
            if (modeButton != null) modeButton.Pressed += OnMode;
            if (scanButton != null) scanButton.Pressed += OnScan;
            if (controller != null) controller.Changed += Refresh;
        }

        void OnDisable()
        {
            if (modeButton != null) modeButton.Pressed -= OnMode;
            if (scanButton != null) scanButton.Pressed -= OnScan;
            if (controller != null) controller.Changed -= Refresh;
        }

        void Start()
        {
            if (plate != null)
            {
                texture = new Texture2D(PlateWidth, PlateHeight, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "MRPanelPlate" };
                pixels = new Color32[PlateWidth * PlateHeight];
                plate.material.SetTexture("_BaseMap", texture);
            }

            if (lamp != null)
                lampMaterial = lamp.material;
            shownKey = null;
            Refresh();
        }

        void Update()
        {
            // Counts and states move without an event of their own (walls found, permission answered).
            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 0.5f;
                Refresh();
            }
        }

        void OnMode()
        {
            if (controller != null)
                controller.Toggle();
        }

        void OnScan()
        {
            if (controller == null)
                return;
            if (!controller.RequestRoomScan())
                Refresh();
        }

        void Refresh()
        {
            if (controller == null)
                return;

            Compose(out var line1, out var line2, out var line3, out var color);
            var key = line1 + "|" + line2 + "|" + line3;
            if (key == shownKey)
                return;
            shownKey = key;
            Draw(line1, line2, line3);

            if (lampMaterial != null)
            {
                lampMaterial.SetColor("_BaseColor", color);
                lampMaterial.SetColor("_EmissionColor", color * 1.4f);
            }
        }

        // No accents: the stencil font is A-Z, 0-9 and a few marks.
        void Compose(out string line1, out string line2, out string line3, out Color color)
        {
            var walls = controller.Walls;
            var hub = controller.Hub;
            line3 = "";

            switch (controller.Mode)
            {
                case MRMode.Entering:
                    line1 = "ENTRANDO";
                    line2 = "PASSTHROUGH...";
                    color = busyColor;
                    return;

                case MRMode.Mixed:
                    line1 = "MODO MR";
                    color = busyColor;
                    switch (walls != null ? walls.Status : MRSurfaceStatus.Unavailable)
                    {
                        case MRSurfaceStatus.RequestingPermission:
                            line2 = "PERMISSAO...";
                            line3 = "ACEITE NO VISOR";
                            break;
                        case MRSurfaceStatus.PermissionDenied:
                            line2 = "SEM PERMISSAO";
                            line3 = "LIBERE EM AJUSTES";
                            color = problemColor;
                            break;
                        case MRSurfaceStatus.WaitingForRoom:
                            line2 = "BUSCANDO SALA";
                            break;
                        case MRSurfaceStatus.NoWalls:
                            line2 = "SEM PAREDES";
                            line3 = "SALA: ESCANEAR";
                            color = problemColor;
                            break;
                        case MRSurfaceStatus.Ready:
                            line2 = "PAREDES: " + (hub != null ? hub.ActiveCount : 0);
                            if (hub != null && hub.LostCount > 0)
                                line3 = "PERDIDAS: " + hub.LostCount;
                            else if (hub != null && hub.RestoredCount > 0)
                                line3 = "RESTAURADAS: " + hub.RestoredCount;
                            color = readyColor;
                            break;
                        default:
                            line2 = "SEM SUPORTE";
                            color = problemColor;
                            break;
                    }

                    return;
            }

            // Virtual room: say why the last try did not work, if it did not.
            line1 = "MODO VR";
            switch (controller.Problem)
            {
                case MRProblem.Unsupported:
                    line2 = "MR SO NO QUEST";
                    line3 = "SEM PASSTHROUGH";
                    color = problemColor;
                    break;
                case MRProblem.PassthroughFailed:
                    line2 = "PASSTHROUGH FALHOU";
                    line3 = "TENTE DE NOVO";
                    color = problemColor;
                    break;
                case MRProblem.PassthroughLost:
                    line2 = "PASSTHROUGH PERDIDO";
                    line3 = "VOLTEI AO VR";
                    color = problemColor;
                    break;
                default:
                    line2 = "APERTE MODO";
                    line3 = "PARA IR AO MR";
                    color = readyColor * 0.6f;
                    break;
            }
        }

        void Draw(string line1, string line2, string line3)
        {
            if (texture == null)
                return;

            var background = new Color32(24, 25, 27, 255);
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = background;

            DrawLine(line1, 150f, 62f, 9f, new Color32(236, 232, 220, 255));
            DrawLine(line2, 92f, 34f, 5f, new Color32(150, 215, 255, 255));
            DrawLine(line3, 40f, 28f, 4f, new Color32(190, 180, 150, 255));
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        void DrawLine(string text, float y, float height, float stroke, Color32 ink)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var width = StencilText.Measure(text) * height;
            var maxWidth = PlateWidth - 40f;
            if (width > maxWidth)
            {
                height *= maxWidth / width;
                width = maxWidth;
            }

            StencilText.Draw(pixels, PlateWidth, PlateHeight, text, new Vector2((PlateWidth - width) * 0.5f, y), height, Mathf.Max(2f, stroke * height / 40f), ink);
        }

        void OnDestroy()
        {
            if (texture != null)
                Destroy(texture);
        }
    }
}
