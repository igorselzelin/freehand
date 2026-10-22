using Freehand.Audio;
using Freehand.Spray;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Interaction
{
    /// <summary>
    /// Showing off a held item, the way a player turns a thing over in their
    /// hand to look at it - not a canned first-person animation. The hand is
    /// the player's: it stays where the controller puts it, and the item turns
    /// in it along the one freedom the grip really leaves it.
    ///
    ///   spray can : about its own axis, inside the closed fist (the body is a
    ///               cylinder - the fingers stay wrapped around it while the
    ///               index lifts off the actuator)
    ///   cap       : about the line between thumb and index, rolled in the pinch
    ///
    /// The turn is a presentation on top of the pose the hand attached
    /// (VRGrabInteractable.SetPresentation): the hand still carries the item,
    /// and a throw still inherits only the hand's motion.
    ///
    /// States: Normal -> Requested -> Presentation (the grip eases, the index
    /// lifts) -> Animation (the variant's keys) -> Return (back to the
    /// attached pose) -> Normal. Each request plays the next variant; a
    /// request during one, the trigger, letting go, or the other hand going for
    /// the cap ends it early and smoothly.
    /// </summary>
    public class ItemInspection : MonoBehaviour
    {
        public enum Kind { SprayCan, Cap }

        enum Phase { Normal, Requested, Presentation, Animation, Return }

        // One key of a variant: turn to `angle` (degrees about the variant's
        // axis) over `seconds`, then hold `hold` seconds.
        struct Key
        {
            public float angle;
            public float seconds;
            public float hold;
            public bool overshoot;
            public Cue cue;
        }

        enum Cue { None, Slip, Catch, Tick }

        [SerializeField] Kind kind = Kind.SprayCan;

        [Tooltip("Seconds the hand takes to ease before the item starts to turn (the index lifting off the actuator).")]
        [SerializeField] float presentationSeconds = 0.16f;

        [SerializeField] float returnSeconds = 0.32f;

        [Tooltip("Seconds of the hurried return when spraying or the other hand interrupts.")]
        [SerializeField] float quickReturnSeconds = 0.12f;

        [Tooltip("Meters: the other hand's fingertips this close to the can's cap ends a cap check, so the cap can be taken at the can's normal pose.")]
        [SerializeField] float otherHandReach = 0.12f;

        VRGrabInteractable grab;
        SprayEmitter emitter;
        CapSocket socket;
        PhysicalCap cap;
        HandRig handRig;
        SprayCanSfx ownSfx;

        Phase phase = Phase.Normal;
        int variant = -1;
        Key[] keys;
        int key;
        float keyTime;
        bool holding;
        float fromAngle;
        float angle;
        float returnFrom;
        float returnTo;
        float returnDuration;
        float phaseTime;
        float oscillation;          // shake test: amplitude (deg) of the flick
        float oscillationHz;
        float oscillationSeconds;
        float oscillationTime;
        int lastOscillationSign;
        Vector3 axis = Vector3.up;
        Vector3 pivot;
        bool heldLeft;
        bool watchOtherHand;

        static readonly string[] CanVariants = { "Flip", "CapCheck", "ShakeTest", "Fidget", "Roll" };

        // V0.14: which way round a move goes (+1 / -1), chosen by the freestyle flow.
        float direction = 1f;
        static readonly string[] CapVariants = { "Detail" };

        public bool Active => phase != Phase.Normal;
        public string Variant => keys == null ? "-" : (kind == Kind.SprayCan ? CanVariants[Mathf.Max(0, variant)] : CapVariants[0]);
        public string PhaseName => phase.ToString();
        public float Angle => angle;

        void Awake()
        {
            grab = GetComponent<VRGrabInteractable>();
            emitter = GetComponent<SprayEmitter>();
            socket = GetComponentInChildren<CapSocket>(true);
            cap = GetComponent<PhysicalCap>();
            ownSfx = GetComponentInChildren<SprayCanSfx>(true);
            handRig = FindAnyObjectByType<HandRig>();
        }

        SprayCanSfx Sfx => ownSfx != null ? ownSfx : SprayCanSfx.Instance;

        /// <summary>The inspect button of the hand holding this item: the next variant, or - while one plays - its end.</summary>
        public void Request()
        {
            switch (phase)
            {
                case Phase.Normal:
                    if (!CanStart())
                        return;
                    variant = (variant + 1) % (kind == Kind.SprayCan ? CanVariants.Length : 1);
                    phase = Phase.Requested;
                    break;
                case Phase.Presentation:
                case Phase.Animation:
                    BeginReturn(returnSeconds);
                    break;
            }
        }

        /// <summary>
        /// V0.14 (CanFreestyle): plays one named move now - "Flip", "CapCheck", "ShakeTest", "Fidget" or "Roll" for the
        /// can - turning `turn` (+1 / -1) way round. False if it cannot start (not held, spraying, cap in the other hand).
        /// </summary>
        public bool Play(string move, float turn)
        {
            if (phase != Phase.Normal || kind != Kind.SprayCan || !CanStart())
                return false;

            var index = System.Array.IndexOf(CanVariants, move);
            if (index < 0)
                return false;

            variant = index;
            direction = turn < 0f ? -1f : 1f;
            phase = Phase.Requested;
            return true;
        }

        /// <summary>V0.14: ends the move under way, smoothly (the short return), keeping the grip.</summary>
        public void Stop()
        {
            if (phase == Phase.Requested)
                Finish(keepShown: false);
            else if (phase == Phase.Presentation || phase == Phase.Animation)
                BeginReturn(quickReturnSeconds);
        }

        bool CanStart()
        {
            if (grab == null || !grab.AttachedToHand)
                return false;

            if (kind == Kind.SprayCan)
            {
                // Not mid-spray, not while the other hand has hold of the cap.
                if (emitter != null && (emitter.IsSpraying || emitter.RawTriggerValue > SprayEmitter.SprayStartThreshold))
                    return false;
                if (socket != null && socket.Installed != null && socket.Installed.State == CapState.Gripped)
                    return false;
            }
            else if (cap != null && cap.State != CapState.Held && cap.State != CapState.AvailableForInstall)
            {
                return false;
            }

            return true;
        }

        void LateUpdate()
        {
            if (phase == Phase.Normal)
                return;

            // Let go (or a cap seated): the item keeps the pose it was shown
            // at - VRGrabInteractable drops the presentation without a snap.
            if (grab == null || !grab.AttachedToHand)
            {
                Finish(keepShown: true);
                return;
            }

            var dt = Time.deltaTime;
            phaseTime += dt;

            switch (phase)
            {
                case Phase.Requested:
                    Begin();
                    break;

                case Phase.Presentation:
                    if (Interrupted())
                        BeginReturn(quickReturnSeconds);
                    else if (phaseTime >= presentationSeconds)
                        Enter(Phase.Animation);
                    break;

                case Phase.Animation:
                    if (Interrupted())
                        BeginReturn(quickReturnSeconds);
                    else
                        Animate(dt);
                    break;

                case Phase.Return:
                {
                    var t = Mathf.Clamp01(phaseTime / Mathf.Max(0.01f, returnDuration));
                    angle = Mathf.Lerp(returnFrom, returnTo, t * t * (3f - 2f * t));
                    if (t >= 1f)
                    {
                        Finish(keepShown: false);
                        return;
                    }

                    break;
                }
            }

            if (phase != Phase.Normal)
                grab.SetPresentation(pivot, Quaternion.AngleAxis(angle, axis));
        }

        void Enter(Phase next)
        {
            phase = next;
            phaseTime = 0f;
        }

        void Begin()
        {
            heldLeft = grab.HeldByLeftHand;
            angle = 0f;
            key = 0;
            keyTime = 0f;
            holding = false;
            fromAngle = 0f;
            oscillationTime = -1f;
            watchOtherHand = false;

            if (kind == Kind.SprayCan)
                BuildCanVariant();
            else
                BuildCapVariant();

            if (emitter != null)
                emitter.InspectionLock = true;

            HapticFeedback.Pulse(heldLeft, 0.12f, 0.02f);
            Enter(Phase.Presentation);
        }

        // ---------- variants ----------

        void BuildCanVariant()
        {
            // The can turns about its own axis (object space up, through the
            // base centre - the grab point is on it, so the fist does not move).
            axis = Vector3.up;
            pivot = Vector3.zero;

            switch (CanVariants[variant])
            {
                case "Flip":
                    // Front, side, back, other side: a quarter turn at a time,
                    // with a moment on each face.
                    keys = new[]
                    {
                        new Key { angle = 90f * direction, seconds = 0.5f, hold = 0.45f, cue = Cue.Slip },
                        new Key { angle = 180f * direction, seconds = 0.5f, hold = 0.45f, cue = Cue.Slip },
                        new Key { angle = 270f * direction, seconds = 0.5f, hold = 0.35f, cue = Cue.Slip },
                        new Key { angle = 360f * direction, seconds = 0.5f, hold = 0.1f, cue = Cue.Slip },
                    };
                    break;

                case "CapCheck":
                {
                    // The orifice turned to face the player, then the cap and
                    // stem in profile. The other hand reaching for the cap ends
                    // it, so the cap comes off (for real) at the normal pose.
                    var toFace = FacingAngle(transform.forward, CapCenter());
                    keys = new[]
                    {
                        new Key { angle = toFace, seconds = 0.55f, hold = 0.8f, cue = Cue.Slip },
                        new Key { angle = toFace + 75f, seconds = 0.45f, hold = 0.6f, cue = Cue.Slip },
                        new Key { angle = toFace, seconds = 0.4f, hold = 0.25f, cue = Cue.Tick },
                    };
                    watchOtherHand = true;
                    break;
                }

                case "ShakeTest":
                    // Quick flicks of the wrist: the ball inside answers
                    // through the can's own motion audio (CanMotionAudio).
                    keys = new[] { new Key { angle = 0f, seconds = 0.05f, hold = 0.95f } };
                    oscillation = 14f;
                    oscillationHz = 6f;
                    oscillationSeconds = 0.85f;
                    oscillationTime = 0f;
                    break;

                case "Roll":
                    // V0.14: the can rolled in the fingers - a long turn one way, a check back, round to the
                    // grip with a little overshoot (the wrist rolls with it: CanFreestyle).
                    keys = new[]
                    {
                        new Key { angle = 200f * direction, seconds = 0.42f, hold = 0.12f, cue = Cue.Slip },
                        new Key { angle = 130f * direction, seconds = 0.22f, hold = 0.06f },
                        new Key { angle = 360f * direction, seconds = 0.4f, hold = 0.12f, overshoot = true, cue = Cue.Slip },
                    };
                    break;

                default: // Fidget: a spin in the fingers, caught with a little overshoot.
                    keys = new[]
                    {
                        new Key { angle = 360f * direction, seconds = 0.5f, hold = 0.2f, overshoot = true, cue = Cue.Slip },
                    };
                    break;
            }
        }

        void BuildCapVariant()
        {
            // Rolled about the line between the pinching fingertips, through
            // their midpoint - the only way a pinched cap can turn.
            pivot = transform.InverseTransformPoint(GetComponent<Renderer>() != null ? GetComponent<Renderer>().bounds.center : transform.position);
            axis = transform.InverseTransformDirection(transform.right);
            if (handRig != null && handRig.TryGetFingertips(heldLeft, out var thumb, out var index) && (index - thumb).sqrMagnitude > 1e-6f)
            {
                axis = transform.InverseTransformDirection((index - thumb).normalized);
                pivot = transform.InverseTransformPoint((index + thumb) * 0.5f);
            }

            var orifice = cap != null && cap.Orifice != null ? cap.Orifice.forward : transform.forward;
            var center = transform.TransformPoint(pivot);
            var toOrifice = FacingAngle(orifice, center);
            var toTop = FacingAngle(transform.up, center);
            var name = cap != null && cap.Spray != null ? cap.Spray.displayName : "Standard";

            switch (name)
            {
                case "Skinny":
                    // The fine bore, then the precise little play of the part.
                    keys = new[]
                    {
                        new Key { angle = toOrifice, seconds = 0.45f, hold = 0.45f, cue = Cue.Tick },
                        new Key { angle = toOrifice + 12f, seconds = 0.14f, hold = 0f },
                        new Key { angle = toOrifice - 12f, seconds = 0.2f, hold = 0f, cue = Cue.Tick },
                        new Key { angle = toOrifice + 6f, seconds = 0.14f, hold = 0f },
                        new Key { angle = toOrifice, seconds = 0.14f, hold = 0.45f, cue = Cue.Tick },
                    };
                    break;

                case "Fat":
                    // Its width first (the top toward the player), then the wide outlet.
                    keys = new[]
                    {
                        new Key { angle = toTop, seconds = 0.55f, hold = 0.8f, cue = Cue.Tick },
                        new Key { angle = toOrifice, seconds = 0.55f, hold = 0.65f, cue = Cue.Tick },
                    };
                    break;

                default:
                    // The shape all round: outlet, the finger scoop behind it, outlet again.
                    keys = new[]
                    {
                        new Key { angle = toOrifice, seconds = 0.5f, hold = 0.55f, cue = Cue.Tick },
                        new Key { angle = toOrifice + 180f, seconds = 0.7f, hold = 0.5f, cue = Cue.Tick },
                        new Key { angle = toOrifice + 360f, seconds = 0.6f, hold = 0.3f, cue = Cue.Tick },
                    };
                    break;
            }
        }

        // The turn about the variant's axis that brings `direction` (world)
        // round to face the player's eyes, as seen from `from` (world).
        float FacingAngle(Vector3 direction, Vector3 from)
        {
            var camera = Camera.main;
            if (camera == null)
                return 0f;

            var axisWorld = transform.TransformDirection(axis).normalized;
            var toHead = camera.transform.position - from;
            var a = Vector3.ProjectOnPlane(direction, axisWorld);
            var b = Vector3.ProjectOnPlane(toHead, axisWorld);
            if (a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f)
                return 0f;
            return Vector3.SignedAngle(a, b, axisWorld);
        }

        Vector3 CapCenter()
        {
            if (socket != null && socket.Installed != null)
            {
                var renderer = socket.Installed.GetComponent<Renderer>();
                if (renderer != null)
                    return renderer.bounds.center;
            }

            return socket != null ? socket.transform.position : transform.position;
        }

        void Animate(float dt)
        {
            // Shake test: flicks that fade in and out.
            if (oscillationTime >= 0f)
            {
                oscillationTime += dt;
                var u = Mathf.Clamp01(oscillationTime / oscillationSeconds);
                var envelope = Mathf.Sin(Mathf.PI * u);
                var phaseAngle = 2f * Mathf.PI * oscillationHz * oscillationTime;
                angle = oscillation * envelope * Mathf.Sin(phaseAngle);
                var sign = Mathf.Cos(phaseAngle) >= 0f ? 1 : -1;
                if (sign != lastOscillationSign && u > 0.05f && u < 0.95f)
                    HapticFeedback.Pulse(heldLeft, 0.08f + 0.1f * envelope, 0.012f);
                lastOscillationSign = sign;
                if (u >= 1f)
                    BeginReturn(returnSeconds * 0.5f);
                return;
            }

            if (keys == null || key >= keys.Length)
            {
                BeginReturn(returnSeconds);
                return;
            }

            var k = keys[key];
            if (keyTime == 0f && !holding)
                PlayCue(k.cue, false);

            keyTime += dt;
            if (!holding)
            {
                var t = Mathf.Clamp01(keyTime / Mathf.Max(0.01f, k.seconds));
                var eased = k.overshoot ? EaseOutBack(t) : t * t * (3f - 2f * t);
                angle = Mathf.LerpUnclamped(fromAngle, k.angle, eased);
                if (t >= 1f)
                {
                    angle = k.angle;
                    holding = true;
                    keyTime = 0f;
                    PlayCue(k.cue, true);
                }
            }
            else
            {
                // Alive while it holds: the hand is never perfectly still.
                angle = k.angle + 1.2f * Mathf.Sin(keyTime * 5.3f) * Mathf.Clamp01(keyTime * 4f);
                if (keyTime >= k.hold)
                {
                    fromAngle = angle;
                    holding = false;
                    keyTime = 0f;
                    key++;
                }
            }
        }

        void PlayCue(Cue cue, bool arrived)
        {
            var sfx = Sfx;
            switch (cue)
            {
                case Cue.Slip:
                    // The body sliding in the fingers as it starts to turn.
                    if (!arrived && sfx != null)
                        sfx.PlayRelease(0.1f);
                    if (arrived)
                        HapticFeedback.Pulse(heldLeft, 0.06f, 0.012f);
                    break;
                case Cue.Catch:
                    if (arrived && sfx != null)
                        sfx.PlayGrab(0.3f);
                    break;
                case Cue.Tick:
                    if (arrived)
                    {
                        if (sfx != null)
                            sfx.PlayCapTouch(transform.TransformPoint(pivot), 0.15f);
                        HapticFeedback.Pulse(heldLeft, 0.08f, 0.012f);
                    }

                    break;
            }

            // The fidget's spin ends caught in the fingers.
            if (arrived && kind == Kind.SprayCan && variant >= 0 && (CanVariants[variant] == "Fidget" || (CanVariants[variant] == "Roll" && key == keys.Length - 1)) && sfx != null)
            {
                sfx.PlayGrab(0.35f);
                HapticFeedback.Pulse(heldLeft, 0.25f, 0.03f);
            }
        }

        bool Interrupted()
        {
            if (kind != Kind.SprayCan)
                return false;

            // The trigger: the player wants to spray - turn back now.
            if (emitter != null && emitter.RawTriggerValue > SprayEmitter.SprayStartThreshold)
                return true;

            // The other hand has the cap, or is going for it.
            if (socket != null && socket.Installed != null && socket.Installed.State == CapState.Gripped)
                return true;
            if (watchOtherHand && handRig != null && socket != null && socket.Installed != null)
            {
                var pinch = handRig.PinchPoint(!heldLeft);
                if (pinch.HasValue && Vector3.Distance(pinch.Value, CapCenter()) < otherHandReach)
                    return true;
            }

            return false;
        }

        void BeginReturn(float seconds)
        {
            // Back the shortest way to the attached pose (whole turns are the same pose).
            returnFrom = angle;
            returnTo = Mathf.Round(angle / 360f) * 360f;
            returnDuration = Mathf.Abs(returnTo - returnFrom) < 0.5f ? 0.05f : seconds;
            oscillationTime = -1f;
            Enter(Phase.Return);
        }

        void Finish(bool keepShown)
        {
            if (!keepShown && grab != null)
                grab.ClearPresentation();
            if (emitter != null)
                emitter.InspectionLock = false;
            phase = Phase.Normal;
            angle = 0f;
            keys = null;
        }

        void OnDisable()
        {
            if (phase != Phase.Normal)
                Finish(keepShown: false);
        }

        static float EaseOutBack(float t)
        {
            const float c1 = 1.2f;
            const float c3 = c1 + 1f;
            var x = t - 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }
    }
}
