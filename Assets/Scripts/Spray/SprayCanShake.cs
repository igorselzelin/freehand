using Freehand.VR;
using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// Detects the player shaking the can and rewards it with a short
    /// pressure boost. (The rattle's sound follows the can's motion itself:
    /// see CanMotionAudio.)
    ///
    /// A shake is not just "moving fast" - waving the can across the room
    /// should not count. What makes it a shake is rapid *reversals*, so this
    /// counts direction flips along the can's own long axis within a time
    /// window. That is cheap, has no false positives from walking, and needs no
    /// physics.
    ///
    /// Deliberately not a paint simulation: there is no penalty for never
    /// shaking, it just feels better when you do.
    /// </summary>
    public class SprayCanShake : MonoBehaviour
    {
        [SerializeField] VRGrabInteractable grabInteractable;

        [Header("Detection")]
        [SerializeField] float minSpeed = 0.55f;
        [SerializeField] int flipsRequired = 4;
        [SerializeField] float windowSeconds = 1.1f;
        [SerializeField] float cooldownSeconds = 1.2f;

        [Header("Reward")]
        [SerializeField] float boostMultiplier = 1.25f;
        [SerializeField] float boostDurationSeconds = 7f;

        Vector3 previousPosition;
        float previousAxisVelocity;
        int flipCount;
        float windowRemaining;
        float cooldownRemaining;
        float boostRemaining;

        /// <summary>1.0 normally, higher for a short time after a good shake.</summary>
        public float PressureMultiplier => boostRemaining > 0f
            ? Mathf.Lerp(1f, boostMultiplier, boostRemaining / boostDurationSeconds)
            : 1f;

        public bool RecentlyShaken => boostRemaining > 0f;

        /// <summary>0-1 signal for purely cosmetic feedback (visual jitter): 1 while flips are actively being counted, fading over the post-shake boost window, 0 otherwise. Derived from existing detection state - no new detection logic.</summary>
        public float ShakeIntensity01 => windowRemaining > 0f && flipCount > 0
            ? 1f
            : (boostRemaining > 0f ? Mathf.Clamp01(boostRemaining / boostDurationSeconds) * 0.4f : 0f);

        void Awake()
        {
            previousPosition = transform.position;
        }

        void Update()
        {
            var deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
                return;

            if (boostRemaining > 0f)
                boostRemaining -= deltaTime;

            if (cooldownRemaining > 0f)
                cooldownRemaining -= deltaTime;

            var velocity = (transform.position - previousPosition) / deltaTime;
            previousPosition = transform.position;

            if (grabInteractable == null || !grabInteractable.IsGrabbed)
            {
                flipCount = 0;
                windowRemaining = 0f;
                previousAxisVelocity = 0f;
                return;
            }

            // Along the can's own axis, so the gesture is "shake the can",
            // not "move the controller in some world direction".
            var axisVelocity = Vector3.Dot(velocity, transform.up);

            if (windowRemaining > 0f)
                windowRemaining -= deltaTime;
            else
                flipCount = 0;

            if (Mathf.Abs(axisVelocity) >= minSpeed && Mathf.Sign(axisVelocity) != Mathf.Sign(previousAxisVelocity) && previousAxisVelocity != 0f)
            {
                flipCount++;
                windowRemaining = windowSeconds;

                // One small pulse per counted flip - naturally rate-limited by
                // how fast a person can physically reverse the can, so this
                // never needs its own cooldown timer.
                HapticFeedback.Pulse(grabInteractable.HeldByLeftHand, 0.15f, 0.03f);
            }

            if (Mathf.Abs(axisVelocity) >= minSpeed * 0.5f)
                previousAxisVelocity = axisVelocity;

            if (flipCount >= flipsRequired && cooldownRemaining <= 0f)
            {
                flipCount = 0;
                windowRemaining = 0f;
                cooldownRemaining = cooldownSeconds;
                boostRemaining = boostDurationSeconds;

                HapticFeedback.Pulse(grabInteractable.HeldByLeftHand, 0.5f, 0.08f);
            }
        }
    }
}
