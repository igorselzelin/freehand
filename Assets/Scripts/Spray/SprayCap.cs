using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// One cap's behaviour, as data rather than as a subclass. Adding Soft,
    /// Needle or Calligraphy later means adding an entry to CapManager's
    /// array - no new types and no changes to SprayEmitter.
    ///
    /// Radii are in world meters so they mean the same thing on every surface,
    /// and both radius and density vary with distance to the wall, which is
    /// what makes close-up work look tight and far-away work look dusty.
    /// </summary>
    [System.Serializable]
    public class SprayCap
    {
        public string displayName = "Standard";

        [Header("Paint footprint")]
        [Tooltip("Paint radius in meters when the nozzle is right against the wall.")]
        public float baseRadiusMeters = 0.035f;

        [Tooltip("How much the radius grows per meter of distance to the wall.")]
        public float radiusGrowthPerMeter = 0.05f;

        [Header("Density")]
        [Tooltip("Paint opacity per tick at contact range (0-1).")]
        public float alphaAtContact = 0.55f;

        [Tooltip("Fraction of density lost per meter of distance.")]
        public float alphaFalloffPerMeter = 0.35f;

        [Header("Particles")]
        [Tooltip("Cone half-angle of the mist. Skinny caps are a tight jet, fat caps a wide fan.")]
        public float coneAngleDegrees = 12f;

        public float particleStartSize = 0.03f;
        public float emissionRate = 250f;

        [Tooltip("Particle speed range leaving the nozzle. A tight jet (Skinny) travels faster than a loose fan (Fat).")]
        public float particleSpeedMin = 2f;
        public float particleSpeedMax = 3.2f;

        [Tooltip("0-1: how much the initial particle direction wanders from a perfect cone, and how strong the drifting turbulence is. Low reads as a tight jet, high as a loose, aerosol-like mist.")]
        public float spreadRandomness = 0.15f;

        [Header("Feel")]
        [Tooltip("Pitch offset for the spray hiss, so caps sound different.")]
        public float audioPitchOffset;

        public static SprayCap[] CreateDefaults()
        {
            return new[]
            {
                new SprayCap
                {
                    displayName = "Skinny",
                    baseRadiusMeters = 0.016f,
                    radiusGrowthPerMeter = 0.022f,
                    alphaAtContact = 0.7f,
                    alphaFalloffPerMeter = 0.3f,
                    coneAngleDegrees = 5f,
                    particleStartSize = 0.014f,
                    emissionRate = 200f,
                    particleSpeedMin = 2.6f,
                    particleSpeedMax = 3.6f,
                    spreadRandomness = 0.05f,
                    audioPitchOffset = 0.18f,
                },
                new SprayCap
                {
                    displayName = "Standard",
                    baseRadiusMeters = 0.035f,
                    radiusGrowthPerMeter = 0.05f,
                    alphaAtContact = 0.55f,
                    alphaFalloffPerMeter = 0.35f,
                    coneAngleDegrees = 12f,
                    particleStartSize = 0.03f,
                    emissionRate = 250f,
                    particleSpeedMin = 2f,
                    particleSpeedMax = 3.2f,
                    spreadRandomness = 0.15f,
                    audioPitchOffset = 0f,
                },
                new SprayCap
                {
                    displayName = "Fat",
                    baseRadiusMeters = 0.075f,
                    radiusGrowthPerMeter = 0.1f,
                    alphaAtContact = 0.4f,
                    alphaFalloffPerMeter = 0.4f,
                    coneAngleDegrees = 24f,
                    particleStartSize = 0.055f,
                    emissionRate = 320f,
                    particleSpeedMin = 1.5f,
                    particleSpeedMax = 2.5f,
                    spreadRandomness = 0.32f,
                    audioPitchOffset = -0.15f,
                },
            };
        }

        public float RadiusAtDistance(float distanceMeters)
        {
            return baseRadiusMeters + radiusGrowthPerMeter * Mathf.Max(0f, distanceMeters);
        }

        public float AlphaAtDistance(float distanceMeters)
        {
            var falloff = 1f - alphaFalloffPerMeter * Mathf.Max(0f, distanceMeters);
            return Mathf.Clamp(alphaAtContact * falloff, 0.04f, 1f);
        }
    }
}
