using Freehand.Audio;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// Maps the analog trigger onto the index finger and the actuator, from
    /// the one trigger reading there is (SprayEmitter.RawTriggerValue):
    ///
    ///   0 .. contact    the index finger travels, in proportion, from its
    ///                   rest on the grip to the actuator (published as
    ///                   VRGrabInteractable.FingerApproach for the hand pose);
    ///   contact .. 1    the finger presses: the actuator moves down,
    ///                   linearly, to maxTravel at full trigger.
    ///
    /// Contact is SprayEmitter.SprayStartThreshold itself: the finger lands
    /// on the actuator exactly where paint starts, so paint never comes out
    /// with the finger still in the air, and the press that follows is the
    /// whole analog range of the spray.
    ///
    /// Moves `nozzlePivot` (CapSocket/NozzlePivot) along the can's axis. The
    /// seated cap, SprayOrigin and the finger's rest point are all children
    /// of that pivot, so the visible cap, the particle emitter, the paint
    /// raycast and the finger all move together by construction. Travel is a
    /// real distance in meters (a few millimetres, like a real actuator),
    /// converted into the pivot's parent space, and clamped to
    /// [0, maxTravel], so it can never overshoot into the can.
    ///
    /// With no cap seated there is nothing to press: the pivot stays at rest.
    /// </summary>
    public class NozzleAnimator : MonoBehaviour
    {
        [SerializeField] Transform nozzlePivot;
        [SerializeField] SprayEmitter sprayEmitter;
        [SerializeField] CapManager capManager;
        [SerializeField] SprayCanSfx sfx;

        [Tooltip("The can's grab component - receives FingerApproach for the hand holding it.")]
        [SerializeField] VRGrabInteractable canGrab;

        [Tooltip("Actuator travel at full trigger, in meters. Real aerosol actuators move roughly 2-4mm.")]
        [SerializeField] float maxTravelMeters = 0.003f;

        [SerializeField] float smoothingSpeed = 20f;

        Vector3 restLocalPosition;
        float smoothedPress;
        bool wasPressing;

        void Awake()
        {
            if (nozzlePivot != null)
                restLocalPosition = nozzlePivot.localPosition;
        }

        void Update()
        {
            if (nozzlePivot == null || sprayEmitter == null)
                return;

            const float contact = SprayEmitter.SprayStartThreshold;
            var trigger01 = Mathf.Clamp01(sprayEmitter.RawTriggerValue);
            if (canGrab != null)
                canGrab.FingerApproach = Mathf.Clamp01(trigger01 / contact);

            var hasCap = capManager == null || capManager.HasCap;
            var press = hasCap ? Mathf.Clamp01((trigger01 - contact) / (1f - contact)) : 0f;

            var t = 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);
            smoothedPress = Mathf.Lerp(smoothedPress, press, t);

            var parent = nozzlePivot.parent;
            var parentScale = parent != null ? Mathf.Abs(parent.lossyScale.y) : 1f;
            var travelLocal = maxTravelMeters / Mathf.Max(0.00001f, parentScale);
            nozzlePivot.localPosition = restLocalPosition + Vector3.down * (travelLocal * smoothedPress);

            // The click is the actuator starting to move under the finger.
            var pressing = press > 0f;
            if (pressing != wasPressing && sfx != null)
                sfx.PlayNozzleClick(pressing);

            wasPressing = pressing;
        }
    }
}
