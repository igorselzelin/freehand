using System.Collections.Generic;
using UnityEngine;

namespace Freehand.Tools
{
    /// <summary>
    /// Text drawn into pixels with a single-stroke stencil alphabet - the kind
    /// of lettering painted on workshop signs and tool labels. Used where the
    /// game needs words (the can's wordmark, the workshop sign, the names of
    /// saved cans) without a font asset or a UI canvas: the result is an
    /// ordinary texture on an ordinary surface in the room.
    ///
    /// Glyphs live in a 0.6 x 1 box (y up); strokes are anti-aliased capsules.
    /// </summary>
    public static class StencilText
    {
        const float Advance = 0.82f;
        const float SpaceAdvance = 0.55f;

        static Dictionary<char, Vector2[][]> glyphs;

        static Vector2[] P(params float[] xy)
        {
            var points = new Vector2[xy.Length / 2];
            for (var i = 0; i < points.Length; i++)
                points[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            return points;
        }

        static Dictionary<char, Vector2[][]> Glyphs
        {
            get
            {
                if (glyphs != null)
                    return glyphs;

                var o = P(0.15f, 0f, 0.45f, 0f, 0.6f, 0.2f, 0.6f, 0.8f, 0.45f, 1f, 0.15f, 1f, 0f, 0.8f, 0f, 0.2f, 0.15f, 0f);
                var p = P(0f, 0f, 0f, 1f, 0.42f, 1f, 0.58f, 0.86f, 0.58f, 0.62f, 0.42f, 0.48f, 0f, 0.48f);
                glyphs = new Dictionary<char, Vector2[][]>
                {
                    ['A'] = new[] { P(0f, 0f, 0.3f, 1f, 0.6f, 0f), P(0.12f, 0.38f, 0.48f, 0.38f) },
                    ['B'] = new[] { P(0f, 0f, 0f, 1f, 0.42f, 1f, 0.55f, 0.88f, 0.55f, 0.62f, 0.42f, 0.52f, 0f, 0.52f), P(0.42f, 0.52f, 0.58f, 0.4f, 0.58f, 0.12f, 0.45f, 0f, 0f, 0f) },
                    ['C'] = new[] { P(0.58f, 0.85f, 0.45f, 1f, 0.15f, 1f, 0f, 0.8f, 0f, 0.2f, 0.15f, 0f, 0.45f, 0f, 0.58f, 0.15f) },
                    ['D'] = new[] { P(0f, 0f, 0f, 1f, 0.38f, 1f, 0.58f, 0.78f, 0.58f, 0.22f, 0.38f, 0f, 0f, 0f) },
                    ['E'] = new[] { P(0.58f, 1f, 0f, 1f, 0f, 0f, 0.58f, 0f), P(0f, 0.52f, 0.45f, 0.52f) },
                    ['F'] = new[] { P(0.58f, 1f, 0f, 1f, 0f, 0f), P(0f, 0.52f, 0.45f, 0.52f) },
                    ['G'] = new[] { P(0.58f, 0.85f, 0.45f, 1f, 0.15f, 1f, 0f, 0.8f, 0f, 0.2f, 0.15f, 0f, 0.45f, 0f, 0.58f, 0.15f, 0.58f, 0.45f, 0.32f, 0.45f) },
                    ['H'] = new[] { P(0f, 0f, 0f, 1f), P(0.58f, 0f, 0.58f, 1f), P(0f, 0.52f, 0.58f, 0.52f) },
                    ['I'] = new[] { P(0.3f, 0f, 0.3f, 1f), P(0.12f, 1f, 0.48f, 1f), P(0.12f, 0f, 0.48f, 0f) },
                    ['J'] = new[] { P(0.58f, 1f, 0.58f, 0.2f, 0.42f, 0f, 0.15f, 0f, 0f, 0.2f) },
                    ['K'] = new[] { P(0f, 0f, 0f, 1f), P(0.56f, 1f, 0.03f, 0.45f), P(0.2f, 0.6f, 0.58f, 0f) },
                    ['L'] = new[] { P(0f, 1f, 0f, 0f, 0.55f, 0f) },
                    ['M'] = new[] { P(0f, 0f, 0f, 1f, 0.3f, 0.45f, 0.6f, 1f, 0.6f, 0f) },
                    ['N'] = new[] { P(0f, 0f, 0f, 1f, 0.58f, 0f, 0.58f, 1f) },
                    ['O'] = new[] { o },
                    ['P'] = new[] { p },
                    ['Q'] = new[] { o, P(0.38f, 0.25f, 0.62f, -0.05f) },
                    ['R'] = new[] { p, P(0.3f, 0.48f, 0.58f, 0f) },
                    ['S'] = new[] { P(0.58f, 0.86f, 0.44f, 1f, 0.14f, 1f, 0f, 0.86f, 0f, 0.64f, 0.14f, 0.52f, 0.44f, 0.48f, 0.58f, 0.36f, 0.58f, 0.14f, 0.44f, 0f, 0.14f, 0f, 0f, 0.14f) },
                    ['T'] = new[] { P(0f, 1f, 0.6f, 1f), P(0.3f, 1f, 0.3f, 0f) },
                    ['U'] = new[] { P(0f, 1f, 0f, 0.2f, 0.15f, 0f, 0.45f, 0f, 0.6f, 0.2f, 0.6f, 1f) },
                    ['V'] = new[] { P(0f, 1f, 0.3f, 0f, 0.6f, 1f) },
                    ['W'] = new[] { P(0f, 1f, 0.15f, 0f, 0.32f, 0.6f, 0.49f, 0f, 0.64f, 1f) },
                    ['X'] = new[] { P(0f, 1f, 0.6f, 0f), P(0.6f, 1f, 0f, 0f) },
                    ['Y'] = new[] { P(0f, 1f, 0.3f, 0.5f, 0.6f, 1f), P(0.3f, 0.5f, 0.3f, 0f) },
                    ['Z'] = new[] { P(0f, 1f, 0.6f, 1f, 0f, 0f, 0.6f, 0f) },
                    ['0'] = new[] { P(0.15f, 0f, 0.4f, 0f, 0.55f, 0.2f, 0.55f, 0.8f, 0.4f, 1f, 0.15f, 1f, 0f, 0.8f, 0f, 0.2f, 0.15f, 0f) },
                    ['1'] = new[] { P(0.1f, 0.8f, 0.3f, 1f, 0.3f, 0f), P(0.1f, 0f, 0.5f, 0f) },
                    ['2'] = new[] { P(0f, 0.8f, 0.15f, 1f, 0.42f, 1f, 0.56f, 0.82f, 0.56f, 0.62f, 0f, 0f, 0.58f, 0f) },
                    ['3'] = new[] { P(0f, 0.88f, 0.15f, 1f, 0.43f, 1f, 0.56f, 0.86f, 0.56f, 0.64f, 0.42f, 0.52f, 0.2f, 0.52f), P(0.42f, 0.52f, 0.58f, 0.38f, 0.58f, 0.14f, 0.44f, 0f, 0.14f, 0f, 0f, 0.12f) },
                    ['4'] = new[] { P(0.45f, 0f, 0.45f, 1f, 0f, 0.3f, 0.6f, 0.3f) },
                    ['5'] = new[] { P(0.56f, 1f, 0.05f, 1f, 0f, 0.55f, 0.4f, 0.6f, 0.56f, 0.45f, 0.56f, 0.15f, 0.42f, 0f, 0.12f, 0f, 0f, 0.12f) },
                    ['6'] = new[] { P(0.52f, 0.95f, 0.35f, 1f, 0.15f, 1f, 0f, 0.8f, 0f, 0.2f, 0.15f, 0f, 0.42f, 0f, 0.56f, 0.18f, 0.56f, 0.42f, 0.42f, 0.58f, 0.15f, 0.58f, 0f, 0.42f) },
                    ['7'] = new[] { P(0f, 1f, 0.58f, 1f, 0.2f, 0f) },
                    ['8'] = new[] { P(0.3f, 0.54f, 0.08f, 0.66f, 0.08f, 0.88f, 0.2f, 1f, 0.4f, 1f, 0.52f, 0.88f, 0.52f, 0.66f, 0.3f, 0.54f, 0.04f, 0.4f, 0.04f, 0.12f, 0.16f, 0f, 0.44f, 0f, 0.56f, 0.12f, 0.56f, 0.4f, 0.3f, 0.54f) },
                    ['9'] = new[] { P(0.04f, 0.05f, 0.21f, 0f, 0.41f, 0f, 0.56f, 0.2f, 0.56f, 0.8f, 0.41f, 1f, 0.14f, 1f, 0f, 0.82f, 0f, 0.58f, 0.14f, 0.42f, 0.41f, 0.42f, 0.56f, 0.58f) },
                    ['-'] = new[] { P(0.1f, 0.5f, 0.5f, 0.5f) },
                    ['.'] = new[] { P(0.27f, 0.02f, 0.3f, 0.05f) },
                    [':'] = new[] { P(0.27f, 0.12f, 0.3f, 0.15f), P(0.27f, 0.72f, 0.3f, 0.75f) },
                    ['/'] = new[] { P(0f, 0f, 0.5f, 1f) },
                    ['#'] = new[] { P(0.18f, 0f, 0.28f, 1f), P(0.38f, 0f, 0.48f, 1f), P(0f, 0.33f, 0.6f, 0.33f), P(0f, 0.67f, 0.6f, 0.67f) },
                };
                return glyphs;
            }
        }

        /// <summary>Width of `text` in glyph units (one unit = the letter height).</summary>
        public static float Measure(string text)
        {
            var width = 0f;
            foreach (var c in text)
                width += c == ' ' ? SpaceAdvance : Advance;
            return Mathf.Max(0f, width - (Advance - 0.6f));
        }

        /// <summary>
        /// Draws `text` into an RGBA buffer (row 0 at the bottom, as Texture2D stores it).
        /// `origin` is the bottom-left of the first letter in pixels, `height` the letter
        /// height in pixels, `xScale` stretches horizontally (to undo non-square texels),
        /// `stroke` the line width in pixels. Unknown characters are skipped.
        /// </summary>
        public static void Draw(Color32[] pixels, int width, int height, string text, Vector2 origin, float letterHeight, float stroke, Color32 color, float xScale = 1f, float slant = 0f)
        {
            var cursor = 0f;
            foreach (var raw in text)
            {
                var c = char.ToUpperInvariant(raw);
                if (c == ' ')
                {
                    cursor += SpaceAdvance;
                    continue;
                }

                if (Glyphs.TryGetValue(c, out var strokes))
                {
                    foreach (var line in strokes)
                    {
                        for (var i = 0; i < line.Length - 1; i++)
                        {
                            var a = ToPixels(line[i], cursor, origin, letterHeight, xScale, slant);
                            var b = ToPixels(line[i + 1], cursor, origin, letterHeight, xScale, slant);
                            Segment(pixels, width, height, a, b, stroke * 0.5f, color);
                        }
                    }
                }

                cursor += Advance;
            }
        }

        static Vector2 ToPixels(Vector2 glyph, float cursor, Vector2 origin, float size, float xScale, float slant)
        {
            return origin + new Vector2((cursor + glyph.x + glyph.y * slant) * size * xScale, glyph.y * size);
        }

        // An anti-aliased capsule from a to b, blended over what is there.
        static void Segment(Color32[] pixels, int width, int height, Vector2 a, Vector2 b, float halfWidth, Color32 color)
        {
            var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - halfWidth - 1f));
            var maxX = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + halfWidth + 1f));
            var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - halfWidth - 1f));
            var maxY = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + halfWidth + 1f));
            var ab = b - a;
            var lengthSq = Mathf.Max(ab.sqrMagnitude, 1e-6f);
            var alphaScale = color.a / 255f;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq);
                    var distance = Vector2.Distance(p, a + ab * t);
                    var coverage = Mathf.Clamp01(halfWidth + 0.5f - distance) * alphaScale;
                    if (coverage <= 0f)
                        continue;

                    var index = y * width + x;
                    var under = pixels[index];
                    pixels[index] = new Color32(
                        (byte)Mathf.RoundToInt(Mathf.Lerp(under.r, color.r, coverage)),
                        (byte)Mathf.RoundToInt(Mathf.Lerp(under.g, color.g, coverage)),
                        (byte)Mathf.RoundToInt(Mathf.Lerp(under.b, color.b, coverage)),
                        (byte)Mathf.Max(under.a, Mathf.RoundToInt(255f * coverage)));
                }
            }
        }
    }
}
