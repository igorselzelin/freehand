using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.VR
{
    /// <summary>
    /// Sits on a hand/controller GameObject. Watches a grip input action and,
    /// while pressed, grabs the nearest VRGrabInteractable within range.
    /// Releasing grip drops whatever is currently held.
    ///
    /// Looks the action up by name inside the given asset (same pattern as
    /// XRInputBootstrap) instead of using an InputActionReference, because a
    /// reference's serialized fileID is a hash Unity only computes once the
    /// asset has been re-imported in the Editor — not something safe to
    /// author by hand.
    /// </summary>
    public class VRGrabber : MonoBehaviour
    {
        [SerializeField] bool isLeftHand;
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string gripActionName = "Left Grip";
        [Tooltip("Palm reach for things held in the fist (the can), meters from the controller's grip pose.")]
        [SerializeField] float grabRadius = 0.12f;

        [Tooltip("Fingertip reach for small things taken between the fingers (caps), meters from the point between the thumb and index tips.")]
        [SerializeField] float pinchRadius = 0.045f;

        [Tooltip("Palm reach for small things: only when they are right at the hand.")]
        [SerializeField] float smallObjectPalmRadius = 0.07f;

        [SerializeField] LayerMask grabbableMask = ~0;

        [Tooltip("When set, held objects are attached to this hand's grip anchor (the rendered hand), not to the raw controller transform.")]
        [SerializeField] HandRig handRig;

        InputAction gripAction;
        VRGrabInteractable held;
        bool wasGripPressed;
        readonly Collider[] reachBuffer = new Collider[8];

        /// <summary>What this hand is holding right now, or null.</summary>
        public VRGrabInteractable Held => held;

        /// <summary>The grip trigger's analog value, 0-1 (it grabs past 0.5).</summary>
        public float GripValue { get; private set; }

        // How the hand is arriving, for the sound of taking hold: its
        // smoothed speed, and the fastest recent squeeze of the grip (1/s).
        float handSpeed;
        float gripRate;
        float lastGripValue;
        Vector3 lastPosition;
        bool motionStarted;

        /// <summary>While the hand is empty: something it could grab is within palm reach - for the hand pose to open for it.</summary>
        public bool GrabbableInReach { get; private set; }

        /// <summary>V0.14: a freestyle move is about to put something into this (empty) hand - the hand opens for it.</summary>
        public bool CatchReady { get; set; }

        /// <summary>The hand the rendered grip belongs to (for the anchor a catch attaches to).</summary>
        public bool IsLeftHand => isLeftHand;

        /// <summary>
        /// V0.14: the held object leaves this hand for a freestyle flight (VRGrabInteractable.BeginFlight). The hand
        /// no longer owns it; the grip button keeps its state (letting it go during the flight drops nothing more).
        /// </summary>
        public VRGrabInteractable HandOff()
        {
            var item = held;
            if (item == null || !item.BeginFlight())
                return null;
            held = null;
            return item;
        }

        /// <summary>
        /// V0.14: this hand takes an object out of the air at the end of a freestyle flight. Only an empty hand whose
        /// grip is closed catches; `anchor` is the grip anchor the flight was aimed at (HandRig.PrepareGripAnchor),
        /// so the object's pose at the catch is the pose it is attached with - no jump.
        /// </summary>
        public bool Catch(VRGrabInteractable item, Transform anchor, float hardness)
        {
            if (item == null || held != null || item.IsGrabbed || GripValue <= 0.5f)
                return false;

            item.GrabHardness = hardness;
            item.Grab(anchor != null ? anchor : transform, isLeftHand);
            held = item;
            CatchReady = false;
            return true;
        }

        /// <summary>The grip anchor this hand would hold `item` with (placed now), or this transform without a rendered hand.</summary>
        public Transform PrepareAnchor(VRGrabInteractable item)
        {
            var anchor = handRig != null ? handRig.PrepareGripAnchor(isLeftHand, item) : null;
            return anchor != null ? anchor : transform;
        }

        void Awake()
        {
            if (inputActions == null)
            {
                Debug.LogError($"{nameof(VRGrabber)}: no InputActionAsset assigned.", this);
                return;
            }

            var map = inputActions.FindActionMap(actionMapName, throwIfNotFound: false);
            if (map == null)
            {
                Debug.LogError($"{nameof(VRGrabber)}: action map '{actionMapName}' not found.", this);
                return;
            }

            gripAction = map.FindAction(gripActionName, throwIfNotFound: false);
            if (gripAction == null)
                Debug.LogError($"{nameof(VRGrabber)}: action '{gripActionName}' not found in map '{actionMapName}'.", this);
        }

        void Update()
        {
            if (gripAction == null)
                return;

            var gripValue = gripAction.ReadValue<float>();
            GripValue = gripValue;
            TrackArrival(gripValue);
            var isGripPressed = gripValue > 0.5f;

            if (isGripPressed && !wasGripPressed)
                TryGrab();
            else if (!isGripPressed && wasGripPressed)
                TryRelease();

            wasGripPressed = isGripPressed;
            GrabbableInReach = held == null && AnyGrabbableNear();
        }

        void TrackArrival(float gripValue)
        {
            var dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            var position = transform.position;
            if (motionStarted)
            {
                var speed = (position - lastPosition).magnitude / dt;
                handSpeed = Mathf.Lerp(handSpeed, speed, 1f - Mathf.Exp(-dt / 0.05f));
                gripRate = Mathf.Max((gripValue - lastGripValue) / dt, gripRate * Mathf.Exp(-dt / 0.12f));
            }

            motionStarted = true;
            lastPosition = position;
            lastGripValue = gripValue;
        }

        // Pose cue only: one non-allocating overlap at the palm per frame,
        // and only while the hand is empty.
        bool AnyGrabbableNear()
        {
            var count = Physics.OverlapSphereNonAlloc(transform.position, grabRadius, reachBuffer, grabbableMask, QueryTriggerInteraction.Collide);
            for (var i = 0; i < count; i++)
            {
                var interactable = reachBuffer[i].GetComponentInParent<VRGrabInteractable>();
                if (interactable != null && !interactable.IsGrabbed && interactable.GrabEnabled)
                    return true;
            }

            return false;
        }

        void TryGrab()
        {
            if (held != null)
                return;

            // Two reach points. Things held in the fist (GripRadius > 0, the
            // can) are reached with the palm - the controller's grip pose.
            // Small things taken between the fingers (caps) are reached with
            // the fingertips: the point between the rendered thumb and index
            // tips, ~8cm ahead of the palm. So pulling a cap off the can in
            // the other hand means bringing the fingers to it, not the second
            // controller into the first one.
            var palm = transform.position;
            var pinch = handRig != null ? handRig.PinchPoint(isLeftHand) : null;
            VRGrabInteractable nearest = null;
            var nearestScore = float.MaxValue;

            void Consider(Collider[] colliders)
            {
                foreach (var col in colliders)
                {
                    var interactable = col.GetComponentInParent<VRGrabInteractable>();
                    if (interactable == null || interactable.IsGrabbed || !interactable.GrabEnabled)
                        continue;

                    // Reach measured to the collider's surface, relative to
                    // each reach point's radius - the smallest wins.
                    var small = interactable.GripRadius <= 0f;
                    var score = Reach(col, palm) / (small ? smallObjectPalmRadius : grabRadius);
                    if (small && pinch.HasValue)
                        score = Mathf.Min(score, Reach(col, pinch.Value) / pinchRadius);

                    if (score <= 1f && score < nearestScore)
                    {
                        nearestScore = score;
                        nearest = interactable;
                    }
                }
            }

            Consider(Physics.OverlapSphere(palm, grabRadius, grabbableMask, QueryTriggerInteraction.Collide));
            if (pinch.HasValue)
                Consider(Physics.OverlapSphere(pinch.Value, pinchRadius, grabbableMask, QueryTriggerInteraction.Collide));

            if (nearest != null)
            {
                // The rendered hand (HandRig) is placed after the controller
                // pose is read, and the controller transform is refreshed
                // again just before rendering - attaching to the hand keeps
                // hand and object in the same pose every frame instead of
                // drifting a few millimetres apart during fast motion.
                var anchor = handRig != null ? handRig.PrepareGripAnchor(isLeftHand, nearest) : null;
                nearest.GrabHardness = 0.5f * Mathf.Clamp01(handSpeed / 1.5f) + 0.5f * Mathf.Clamp01((gripRate - 1.5f) / 6f);
                nearest.Grab(anchor != null ? anchor : transform, isLeftHand);
                held = nearest;
            }
        }

        // Distance from a point to a collider's surface. ClosestPoint is
        // undefined for non-convex MeshColliders: their pivot stands in.
        static float Reach(Collider col, Vector3 point)
        {
            var closest = col is MeshCollider { convex: false } ? col.transform.position : col.ClosestPoint(point);
            return (closest - point).magnitude;
        }

        void TryRelease()
        {
            if (held == null)
                return;

            held.Release();
            held = null;
        }
    }
}
