using Freehand.Audio;
using Freehand.Spray;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Tools
{
    /// <summary>
    /// The physical color rack in the room. Touch a swatch with either hand to
    /// load that color into the can - no 2D HUD, no menu.
    ///
    /// The swatch colors come from ColorManager.Palette rather than being set
    /// in the Inspector, so the paint, the particles and the swatch a player
    /// touches can never disagree about what "red" is.
    ///
    /// The last slot is the cleaner: selecting it puts the can into erase mode,
    /// which reuses the whole spray pipeline and just blends back toward the
    /// wall's base color. That gives the paint / undo loop a third option
    /// (paint, undo the last stroke, or wipe an area) without a second tool.
    ///
    /// The rack also follows selections made elsewhere (the Color Orb): it
    /// watches ColorManager and the emitter's cleaner mode and marks the
    /// matching can - or none, for a custom color.
    /// </summary>
    public class ColorPalette : ProximityRack
    {
        [SerializeField] ColorManager colorManager;
        [SerializeField] SprayCanController canController;
        [SerializeField] SprayEmitter sprayEmitter;
        [SerializeField] SprayCanSfx sfx;

        [Tooltip("Index of the cleaner slot. -1 disables it.")]
        [SerializeField] int eraserSlotIndex = -1;

        void Start()
        {
            TintSwatches();
            ApplySelection(colorManager != null ? colorManager.CurrentIndex : 0, false);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (colorManager != null)
                colorManager.ColorChanged += OnColorChanged;
            if (sprayEmitter != null)
                sprayEmitter.EraseModeChanged += OnEraseModeChanged;
        }

        void OnDisable()
        {
            if (colorManager != null)
                colorManager.ColorChanged -= OnColorChanged;
            if (sprayEmitter != null)
                sprayEmitter.EraseModeChanged -= OnEraseModeChanged;
        }

        void OnColorChanged(Color _) => ShowCurrent();

        void OnEraseModeChanged(bool _) => ShowCurrent();

        // Whatever is loaded now, wherever it was chosen.
        void ShowCurrent()
        {
            if (sprayEmitter != null && sprayEmitter.EraseMode)
                Visualize(eraserSlotIndex);
            else if (colorManager != null)
                Visualize(colorManager.IsCustomColor ? -1 : colorManager.CurrentIndex);
        }

        void TintSwatches()
        {
            if (slots == null)
                return;

            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null || i == eraserSlotIndex || i >= ColorManager.Palette.Length)
                    continue;

                var renderer = slots[i].GetComponent<Renderer>();
                if (renderer != null)
                    renderer.material.SetColor("_BaseColor", ColorManager.Palette[i]);
            }
        }

        protected override void OnSlotSelected(int index)
        {
            // A second can (the workshop's) held in the touching hand takes
            // the color itself; otherwise it goes to the player's can.
            var hand = LastTouchedByLeftHand ? LeftHand : RightHand;
            var grabber = hand != null ? hand.GetComponent<VRGrabber>() : null;
            var held = grabber != null ? grabber.Held : null;
            var otherCan = held != null ? held.GetComponent<SprayCanController>() : null;
            if (otherCan != null && otherCan != canController)
            {
                var otherEmitter = held.GetComponent<SprayEmitter>();
                var otherColors = held.GetComponent<ColorManager>();
                var isEraser = index == eraserSlotIndex;
                if (otherEmitter != null)
                    otherEmitter.EraseMode = isEraser;
                if (!isEraser && otherColors != null)
                    otherCan.ApplyColor(otherColors.Select(index));
                if (sfx != null)
                    sfx.PlayColorChange();
                Visualize(index);
                return;
            }

            ApplySelection(index, true);
        }

        void ApplySelection(int index, bool playSound)
        {
            var isEraser = index == eraserSlotIndex;

            if (sprayEmitter != null)
                sprayEmitter.EraseMode = isEraser;

            if (!isEraser && colorManager != null)
            {
                var color = colorManager.Select(index);
                if (canController != null)
                    canController.ApplyColor(color);
            }

            if (playSound && sfx != null)
                sfx.PlayColorChange();

            Visualize(index);
        }
    }
}
