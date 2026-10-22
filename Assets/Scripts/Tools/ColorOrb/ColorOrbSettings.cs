using UnityEngine;

namespace Freehand.Tools
{
    /// <summary>
    /// Everything that shapes the Color Orb - where it floats, how big it is,
    /// how forgiving pointing at it is, how long each transition takes - in
    /// one asset, so tuning it in the headset never means editing code.
    /// </summary>
    [CreateAssetMenu(menuName = "Freehand/Color Orb Settings", fileName = "ColorOrbSettings")]
    public class ColorOrbSettings : ScriptableObject
    {
        [Header("Placement over the palm")]
        [Tooltip("How far the orb floats off the palm, meters (clear of the fingers).")]
        public float hoverDistance = 0.14f;

        [Tooltip("0 = straight out of the palm, 1 = straight up: keeps the orb above the hand when the palm tilts.")]
        [Range(0f, 1f)] public float upwardBias = 0.45f;

        [Tooltip("Gap, meters, the beads' orbit keeps from the fingers of the hand under the orb: it floats further off a curled or tilted hand rather than through its fingers.")]
        public float fingerClearance = 0.01f;

        [Tooltip("Seconds the orb takes to catch up with the hand - it floats after the palm, it is not bolted to the wrist.")]
        public float followSmoothTime = 0.07f;

        [Tooltip("How quickly the orb turns to face the player, 1/s.")]
        public float turnSpeed = 10f;

        [Tooltip("0 = the orb faces the head, 1 = it faces the pointing hand. Between the two, the hand pointing in from the side still sees the beads beside the core, not behind it.")]
        [Range(0f, 0.8f)] public float pointerFacing = 0.45f;

        [Header("Size (meters)")]
        public float coreRadius = 0.036f;
        public float haloScale = 1.25f;
        public float valueArcRadius = 0.052f;

        [Tooltip("The value arc spans this many degrees above and below the orb's side.")]
        public float valueArcHalfAngle = 70f;

        public float beadOrbitRadius = 0.066f;
        public float beadRadius = 0.0075f;

        [Tooltip("Tilt of the beads' orbit off the orb's face, degrees.")]
        public float beadOrbitTilt = 24f;

        [Header("Pointing")]
        [Tooltip("Extra radius, meters, every target accepts a pointer by - small targets in a moving hand need it.")]
        public float aimAssist = 0.012f;
        public float maxPointerDistance = 1.6f;

        [Header("Timing (seconds)")]
        public float summonSeconds = 0.42f;
        public float dismissSeconds = 0.3f;
        public float confirmSeconds = 0.38f;

        [Header("Idle motion")]
        public float breatheAmplitude = 0.025f;
        public float breatheHz = 0.7f;
        public float beadOrbitDegreesPerSecond = 16f;

        [Tooltip("The orb never shows its colors darker than this, whatever the loaded color - so a black can still opens a readable orb.")]
        [Range(0.1f, 1f)] public float minDisplayValue = 0.35f;
    }
}
