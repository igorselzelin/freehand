using System;
using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// The single palette definition for the game, plus which color is active.
    /// Selection is triggered externally (the physical ColorPalette rack in the
    /// room, or a button) so this class stays pure palette state - the same
    /// split CapManager uses.
    /// </summary>
    public class ColorManager : MonoBehaviour
    {
        // Slightly off pure primaries: fully saturated 255 channels read as
        // plastic under URP lighting, these look more like real spray paint.
        public static readonly Color[] Palette =
        {
            new Color(0.85f, 0.11f, 0.13f), // Red
            new Color(0.10f, 0.32f, 0.80f), // Blue
            new Color(0.13f, 0.66f, 0.24f), // Green
            new Color(0.97f, 0.80f, 0.10f), // Yellow
            new Color(0.95f, 0.95f, 0.93f), // White
            new Color(0.07f, 0.07f, 0.08f), // Black
        };

        public static readonly string[] PaletteNames =
        {
            "Red", "Blue", "Green", "Yellow", "White", "Black",
        };

        int currentIndex;
        Color customColor = Color.white;
        bool usingCustom;

        public event Action<Color> ColorChanged;

        /// <summary>The active color - a fixed palette swatch, or, after SelectCustom (the Color Orb), an arbitrary picked one. There is still exactly one "current color" and one place that owns it.</summary>
        public Color CurrentColor => usingCustom ? customColor : Palette[currentIndex];
        public int CurrentIndex => currentIndex;
        public bool IsCustomColor => usingCustom;
        public static int Count => Palette.Length;

        public Color Select(int index)
        {
            var clamped = Mathf.Clamp(index, 0, Palette.Length - 1);
            if (!usingCustom && clamped == currentIndex)
                return CurrentColor;

            usingCustom = false;
            currentIndex = clamped;
            ColorChanged?.Invoke(CurrentColor);
            return CurrentColor;
        }

        public Color CycleNext() => Select((currentIndex + 1) % Palette.Length);

        /// <summary>Loads an arbitrary color picked on the Color Orb. Choosing a palette color afterward (Select - the rack or an orb bead) switches back to a fixed palette color - there is only one "current color" at a time, never two color systems disagreeing.</summary>
        public Color SelectCustom(Color color)
        {
            usingCustom = true;
            customColor = color;
            ColorChanged?.Invoke(CurrentColor);
            return CurrentColor;
        }
    }
}
