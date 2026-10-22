using UnityEngine;

namespace Freehand.Paint
{
    /// <summary>
    /// One spray impact, described in surface-independent terms. Passed by
    /// reference so adding a parameter later does not mean touching every
    /// signature between the emitter and the surface.
    /// </summary>
    public struct SprayStamp
    {
        public Color color;

        /// <summary>Footprint radius in world meters. The surface converts to texels.</summary>
        public float radiusMeters;

        /// <summary>Opacity applied per tick, before falloff inside the stamp.</summary>
        public float alpha;

        /// <summary>1 = head-on circular. Higher values elongate the stamp for angled hits.</summary>
        public float stretch;

        /// <summary>Direction of the elongation in UV space, radians.</summary>
        public float stretchAngle;

        /// <summary>Cleaner mode: blend back toward the surface's base color instead of adding paint.</summary>
        public bool erase;

        /// <summary>Display name of the cap that produced this stamp (e.g. "Skinny") - carried through to PaintHistory's stroke metadata, not used by the paint math itself.</summary>
        public string capName;

        /// <summary>
        /// Downhill on the surface at the impact, in UV units per meter travelled along it: gravity
        /// projected onto the hit triangle's plane and converted through that triangle's own UVs (see
        /// PaintSurface.TryWorldToUv). Where paint runs when it drips.
        /// </summary>
        public Vector2 downhillUv;

        /// <summary>How much of gravity lies along the surface: 1 on a vertical wall, 0 on a floor or ceiling (paint pools there instead of running).</summary>
        public float slope;

        public static SprayStamp Basic(Color color, float radiusMeters, float alpha)
        {
            return new SprayStamp
            {
                color = color,
                radiusMeters = radiusMeters,
                alpha = alpha,
                stretch = 1f,
                stretchAngle = 0f,
                erase = false,
            };
        }
    }
}
