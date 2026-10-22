using Freehand.Audio;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// Orchestrates a single spray can: keeps the current color in sync between
    /// ColorManager, the emitter and the can's own visible body, and fires the
    /// grab sound.
    ///
    /// Color selection moved out of this class in V0.4: it now comes from the
    /// physical ColorPalette rack in the room rather than a controller button,
    /// so the freed button could be given to Undo. This class still owns
    /// "applying" a color, so anything that selects one only needs to call
    /// ApplyColor.
    /// </summary>
    [RequireComponent(typeof(ColorManager))]
    public class SprayCanController : MonoBehaviour
    {
        [Tooltip("Renderers on the can that should show the loaded paint color.")]
        [SerializeField] Renderer[] colorTintRenderers;

        [SerializeField] SprayEmitter sprayEmitter;
        [SerializeField] VRGrabInteractable grabInteractable;
        [SerializeField] SprayCanSfx sfx;

        ColorManager colorManager;
        Rigidbody body;
        Material[] tintInstances;
        bool wasGrabbed;

        void Awake()
        {
            colorManager = GetComponent<ColorManager>();
            body = GetComponent<Rigidbody>();

            if (colorTintRenderers != null)
            {
                tintInstances = new Material[colorTintRenderers.Length];
                for (var i = 0; i < colorTintRenderers.Length; i++)
                {
                    if (colorTintRenderers[i] != null)
                        tintInstances[i] = colorTintRenderers[i].material;
                }
            }

            ApplyColor(colorManager.CurrentColor);
        }

        void Update()
        {
            if (grabInteractable == null)
                return;

            if (grabInteractable.IsGrabbed && !wasGrabbed && sfx != null)
                sfx.PlayGrab(grabInteractable.GrabHardness);
            else if (!grabInteractable.IsGrabbed && wasGrabbed && sfx != null)
                sfx.PlayRelease(body != null ? Mathf.Clamp01(body.linearVelocity.magnitude / 3f) : 0f);

            wasGrabbed = grabInteractable.IsGrabbed;
        }

        /// <summary>Loads a color into the can: tints the visible can and tells the emitter, so particles and paint always match the swatch that was touched.</summary>
        public void ApplyColor(Color color)
        {
            if (tintInstances != null)
            {
                foreach (var material in tintInstances)
                {
                    if (material != null)
                        material.SetColor("_BaseColor", color);
                }
            }

            if (sprayEmitter != null)
                sprayEmitter.SetColor(color);

            // The color-change sound belongs to whoever performed the
            // selection (the palette), not to applying the color - otherwise
            // touching a swatch plays it twice.
        }
    }
}
