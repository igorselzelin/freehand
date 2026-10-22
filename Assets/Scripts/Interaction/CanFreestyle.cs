using System.Collections.Generic;
using Freehand.Audio;
using Freehand.Spray;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Interaction
{
    /// <summary>
    /// V0.14: playing with the can - short, varied freestyle runs started by the inspect button (A / X of the hand
    /// holding it), the way a writer fidgets with a can between pieces. A run is two or three moves:
    ///
    ///   in-hand  the can turned in the fist (Roll, Fidget, Flip, ShakeTest, CapCheck - ItemInspection does the
    ///            turning, the only thing that ever turns a held can) with the wrist rolling or flicking along
    ///   toss     a flick of the wrist, the can leaves the hand, flips end over end (or spins) over a short
    ///            ballistic arc above it, and comes back down into the same hand, which opens for it
    ///   pass     the can goes across to the other hand, which must be empty, close by and closed (grip held:
    ///            "ready to catch")
    ///
    /// Object ownership is explicit and single: VRGrabber.HandOff takes the can out of the hand (no hand owns it -
    /// it is kinematic, placed by the move, no gravity, no collision impulse), VRGrabber.Catch puts it into one hand.
    /// The flight is aimed at the catching hand's own grip anchor, so the can arrives exactly in the pose that hand
    /// holds it with - the catch is the end of the flight, not a jump to the hand. What the hand really does decides:
    ///   - letting go of the grip during a flight drops the can (it carries on with the flight's motion, as a
    ///     physics body),
    ///   - a hand that moved too far from where the can comes down misses it the same way,
    ///   - the other hand grabbing it out of the air takes it, and the run stops.
    /// Spraying is impossible while the can is in the air (no hand holds it) and locked while a move gets ready or
    /// lands. A second press of the button stops the run: an in-hand move turns back, a can in the air is caught
    /// first. The trigger, letting go, or the other hand going for the cap stop it too.
    ///
    /// States: Idle -> (Preparing -> InHand | Flight -> Recovery)* -> Idle.
    /// </summary>
    [RequireComponent(typeof(VRGrabInteractable))]
    public class CanFreestyle : MonoBehaviour
    {
        public enum State { Idle, Preparing, InHand, Flight, Recovery }

        enum Kind { InHand, Toss, Pass }

        struct Move
        {
            public Kind kind;
            public string name;     // in-hand move
            public float turn;      // +1 / -1
            public bool flip;       // toss: end over end (else a spin about the can's own axis)
            public float spins;     // toss: whole turns in the air
            public float height;    // toss: apex above the hand, meters
        }

        [SerializeField] VRGrabber leftGrabber;
        [SerializeField] VRGrabber rightGrabber;

        [Header("Toss")]
        [SerializeField] float minTossHeight = 0.2f;
        [SerializeField] float maxTossHeight = 0.3f;

        [Tooltip("The hand must be steadier than this (m/s) for a toss: a can thrown from a swinging hand would not come back to it.")]
        [SerializeField] float maxHandSpeedForToss = 0.8f;

        [Tooltip("How far, meters, the catching hand may be from where the can comes down and still catch it.")]
        [SerializeField] float catchTolerance = 0.3f;

        [Header("Pass to the other hand")]
        [SerializeField] float minPassDistance = 0.1f;
        [SerializeField] float maxPassDistance = 0.6f;

        [Header("Timing")]
        [SerializeField] float prepareSeconds = 0.18f;
        [SerializeField] float recoverySeconds = 0.3f;
        [SerializeField] float gapSeconds = 0.12f;

        [Tooltip("Layers that block a toss (something right above the hand).")]
        [SerializeField] LayerMask clearanceMask = ~(1 << 2);

        VRGrabInteractable grab;
        ItemInspection inspection;
        SprayEmitter emitter;
        CapSocket socket;
        HandRig handRig;
        SprayCanSfx sfx;

        State state = State.Idle;
        readonly List<Move> run = new List<Move>();
        int step;
        float stateTime;
        bool stopRequested;
        string lastRun = "";
        Move current;

        // flight
        VRGrabber thrower;
        VRGrabber catcher;
        Transform targetAnchor;
        Vector3 attachLocalPosition;
        Quaternion attachLocalRotation;
        Vector3 launchCenter;
        Vector3 launchTargetCenter;
        Quaternion launchRotation;
        Vector3 centerLocal;
        Vector3 spinAxis;
        float spinDegrees;
        float flightSeconds;
        float arcHeight;
        Vector3 lastPosition;
        Quaternion lastRotation;
        Vector3 flightVelocity;
        Vector3 flightAngular;

        // hand speed, for the steadiness check
        Vector3 lastHandPosition;
        float handSpeed;
        bool handSampled;

        // wrist flourish per hand (grip-frame rotation, eased)
        Quaternion flourishLeft = Quaternion.identity;
        Quaternion flourishRight = Quaternion.identity;
        Quaternion flourishTargetLeft = Quaternion.identity;
        Quaternion flourishTargetRight = Quaternion.identity;

        readonly RaycastHit[] clearanceHits = new RaycastHit[8];

        public State Current => state;
        public bool Active => state != State.Idle;
        public string CurrentMove => state == State.Idle ? "-" : (current.kind == Kind.InHand ? current.name : current.kind.ToString());

        /// <summary>Catches and drops since start (diagnostics, tests).</summary>
        public int Catches { get; private set; }
        public int Drops { get; private set; }

        /// <summary>The moves of the last run, in order (diagnostics, tests).</summary>
        public string LastRun => lastRun;

        void Awake()
        {
            grab = GetComponent<VRGrabInteractable>();
            inspection = GetComponent<ItemInspection>();
            emitter = GetComponent<SprayEmitter>();
            socket = GetComponentInChildren<CapSocket>(true);
            sfx = GetComponentInChildren<SprayCanSfx>(true);
            handRig = FindAnyObjectByType<HandRig>();
        }

        SprayCanSfx Sfx => sfx != null ? sfx : SprayCanSfx.Instance;

        VRGrabber Holder => !grab.IsGrabbed ? null : (grab.HeldByLeftHand ? leftGrabber : rightGrabber);

        VRGrabber Other(VRGrabber hand) => hand == leftGrabber ? rightGrabber : leftGrabber;

        // ---------- the button ----------

        /// <summary>The inspect button of the hand holding the can: a new run, or - during one - its end.</summary>
        public void Request()
        {
            if (Active)
            {
                Stop();
                return;
            }

            if (!grab.AttachedToHand || Holder == null || !SafeToMove())
                return;

            BuildRun();
            step = -1;
            stopRequested = false;
            NextMove();
        }

        /// <summary>
        /// Diagnostics and tests: plays exactly these moves, comma separated - "Roll", "Fidget", "Flip", "ShakeTest",
        /// "CapCheck", "TossFlip", "TossSpin", "Pass" - under the same rules as a run (a move that is not possible
        /// right now becomes an in-hand one). False if a run cannot start.
        /// </summary>
        public bool Play(string moves)
        {
            if (Active || !grab.AttachedToHand || Holder == null || !SafeToMove())
                return false;

            run.Clear();
            foreach (var raw in moves.Split(','))
            {
                var name = raw.Trim();
                var turn = Random.value < 0.5f ? -1f : 1f;
                switch (name)
                {
                    case "TossFlip":
                    case "TossSpin":
                    {
                        var flip = name == "TossFlip";
                        var height = Random.Range(minTossHeight, maxTossHeight);
                        run.Add(new Move { kind = Kind.Toss, name = name, turn = turn, flip = flip, height = height, spins = !flip && height > 0.26f ? 2f : 1f });
                        break;
                    }
                    case "Pass":
                        run.Add(new Move { kind = Kind.Pass, name = "Pass", turn = turn, spins = 1f });
                        break;
                    default:
                        run.Add(new Move { kind = Kind.InHand, name = name, turn = turn });
                        break;
                }
            }

            lastRun = Signature();
            step = -1;
            stopRequested = false;
            NextMove();
            return true;
        }

        /// <summary>Ends the run as soon as it safely can: an in-hand move turns back, a can in the air is caught first.</summary>
        public void Stop()
        {
            stopRequested = true;
            switch (state)
            {
                case State.Preparing:
                    End();
                    break;
                case State.InHand:
                    if (inspection != null)
                        inspection.Stop();
                    break;
            }
        }

        bool SafeToMove()
        {
            if (emitter != null && (emitter.IsSpraying || emitter.RawTriggerValue > SprayEmitter.SprayStartThreshold))
                return false;
            if (socket != null && socket.Installed != null && socket.Installed.State == CapState.Gripped)
                return false;
            return true;
        }

        // ---------- the run ----------

        void BuildRun()
        {
            run.Clear();
            var templates = new List<Kind[]>
            {
                new[] { Kind.InHand, Kind.Toss },
                new[] { Kind.Toss, Kind.InHand },
                new[] { Kind.InHand, Kind.InHand },
                new[] { Kind.Toss },
                new[] { Kind.Pass, Kind.InHand },
                new[] { Kind.InHand, Kind.Pass, Kind.InHand },
            };

            // Never the same run twice in a row; a pass only when the other hand is ready for it now.
            var passPossible = PassReady(Holder, out _);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var template = templates[Random.Range(0, templates.Count)];
                if (!passPossible && System.Array.IndexOf(template, Kind.Pass) >= 0)
                    continue;

                run.Clear();
                var previous = "";
                foreach (var kind in template)
                {
                    var move = Pick(kind, previous);
                    previous = move.name;
                    run.Add(move);
                }

                var signature = Signature();
                if (signature != lastRun || attempt == 11)
                    break;
            }

            lastRun = Signature();
        }

        string Signature()
        {
            var parts = new string[run.Count];
            for (var i = 0; i < run.Count; i++)
                parts[i] = run[i].kind == Kind.InHand ? run[i].name : (run[i].kind == Kind.Toss ? (run[i].flip ? "TossFlip" : "TossSpin") : "Pass");
            return string.Join(",", parts);
        }

        Move Pick(Kind kind, string avoid)
        {
            var turn = Random.value < 0.5f ? -1f : 1f;
            switch (kind)
            {
                case Kind.Toss:
                {
                    var flip = Random.value < 0.55f;
                    var height = Random.Range(minTossHeight, maxTossHeight);
                    return new Move { kind = Kind.Toss, name = flip ? "TossFlip" : "TossSpin", turn = turn, flip = flip, height = height, spins = !flip && height > 0.26f ? 2f : 1f };
                }
                case Kind.Pass:
                    return new Move { kind = Kind.Pass, name = "Pass", turn = turn, spins = Random.value < 0.5f ? 1f : 0f };
                default:
                {
                    // Weighted: the roll is the signature move.
                    var hasCap = socket != null && socket.Installed != null;
                    string name;
                    var guard = 0;
                    do
                    {
                        var r = Random.value;
                        name = r < 0.34f ? "Roll" : r < 0.56f ? "Fidget" : r < 0.72f ? "Flip" : r < 0.86f ? "ShakeTest" : (hasCap ? "CapCheck" : "Roll");
                    }
                    while (name == avoid && ++guard < 8);

                    return new Move { kind = Kind.InHand, name = name, turn = turn };
                }
            }
        }

        void NextMove()
        {
            step++;
            if (stopRequested || step >= run.Count || !grab.AttachedToHand)
            {
                End();
                return;
            }

            current = run[step];

            // What was possible when the run was planned may not be now: a toss over a shelf, a pass to a hand
            // that moved away - those become a move in the hand instead.
            if (current.kind == Kind.Toss && !TossPossible())
                current = Pick(Kind.InHand, step > 0 ? run[step - 1].name : "");
            if (current.kind == Kind.Pass && !PassReady(Holder, out _))
                current = Pick(Kind.InHand, step > 0 ? run[step - 1].name : "");

            Enter(State.Preparing);
            if (current.kind == Kind.Pass)
                Other(Holder).CatchReady = true;
        }

        void Enter(State next)
        {
            state = next;
            stateTime = 0f;
        }

        void End()
        {
            if (state == State.Flight)
                return; // a can in the air is always brought down first

            if (inspection != null && inspection.Active)
                inspection.Stop();
            if (leftGrabber != null) leftGrabber.CatchReady = false;
            if (rightGrabber != null) rightGrabber.CatchReady = false;
            if (emitter != null) emitter.FreestyleLock = false;
            flourishTargetLeft = flourishTargetRight = Quaternion.identity;
            state = State.Idle;
            stateTime = 0f;
        }

        // ---------- every frame (after HandRig has placed the hands) ----------

        void LateUpdate()
        {
            TrackHandSpeed();

            if (state != State.Idle)
            {
                stateTime += Time.deltaTime;
                switch (state)
                {
                    case State.Preparing: UpdatePreparing(); break;
                    case State.InHand: UpdateInHand(); break;
                    case State.Flight: UpdateFlight(); break;
                    case State.Recovery: UpdateRecovery(); break;
                }

                // A run being stopped (the trigger, the button) never holds the spray back: only a move getting
                // ready, a can in the air, and the first moment of a landing do.
                if (emitter != null)
                    emitter.FreestyleLock = state == State.Preparing || state == State.Flight || (state == State.Recovery && !stopRequested && stateTime < recoverySeconds * 0.6f);
            }

            ApplyFlourish();
        }

        void TrackHandSpeed()
        {
            var holder = Holder;
            if (holder == null)
            {
                handSampled = false;
                return;
            }

            var position = holder.transform.position;
            var dt = Time.deltaTime;
            if (handSampled && dt > 0f)
                handSpeed = Mathf.Lerp(handSpeed, (position - lastHandPosition).magnitude / dt, 1f - Mathf.Exp(-dt / 0.08f));
            lastHandPosition = position;
            handSampled = true;
        }

        void UpdatePreparing()
        {
            // Let go, or the trigger: the run is over before it starts.
            if (!grab.AttachedToHand || !SafeToMove())
            {
                End();
                return;
            }

            var holder = Holder;
            var t = Mathf.Clamp01(stateTime / prepareSeconds);
            var ease = t * t * (3f - 2f * t);
            switch (current.kind)
            {
                case Kind.InHand:
                    // A small settle of the wrist into the move.
                    SetFlourish(holder, Quaternion.AngleAxis(-6f * ease, Vector3.forward));
                    if (t >= 1f)
                    {
                        if (inspection != null && inspection.Play(current.name, current.turn))
                            Enter(State.InHand);
                        else
                            NextMove();
                    }

                    break;

                case Kind.Toss:
                    // The wrist cocks back before the flick.
                    SetFlourish(holder, Quaternion.AngleAxis(-18f * ease, Vector3.forward));
                    if (t >= 1f)
                        Launch(holder, holder, current.height);
                    break;

                case Kind.Pass:
                    SetFlourish(holder, Quaternion.AngleAxis(-10f * ease, Vector3.forward));
                    if (t >= 1f)
                    {
                        if (PassReady(holder, out var receiver))
                            Launch(holder, receiver, 0f);
                        else
                            NextMove();
                    }

                    break;
            }
        }

        void UpdateInHand()
        {
            var holder = Holder;
            if (!grab.AttachedToHand || holder == null)
            {
                End();
                return;
            }

            // The wrist goes with the can: rolls with a roll, flicks with a fidget, eases otherwise.
            var phase = Mathf.Sin(Mathf.Min(1f, stateTime / 1.1f) * Mathf.PI);
            switch (current.name)
            {
                case "Roll":
                    SetFlourish(holder, Quaternion.AngleAxis(22f * current.turn * phase, Vector3.up) * Quaternion.AngleAxis(-6f * phase, Vector3.forward));
                    break;
                case "Fidget":
                    SetFlourish(holder, Quaternion.AngleAxis(10f * current.turn * Mathf.Sin(Mathf.Min(1f, stateTime / 0.6f) * Mathf.PI), Vector3.up));
                    break;
                case "Flip":
                    SetFlourish(holder, Quaternion.AngleAxis(-8f * phase, Vector3.right));
                    break;
                default:
                    SetFlourish(holder, Quaternion.identity);
                    break;
            }

            // The trigger stopped the move (ItemInspection turned back): spraying wins, the run ends.
            if (emitter != null && emitter.RawTriggerValue > SprayEmitter.SprayStartThreshold)
                stopRequested = true;

            if (inspection == null || !inspection.Active)
            {
                Enter(State.Recovery);
            }
        }

        // ---------- flight ----------

        void Launch(VRGrabber from, VRGrabber to, float height)
        {
            if (!grab.AttachedToHand || from == null || to == null)
            {
                NextMove();
                return;
            }

            attachLocalPosition = grab.AttachedLocalPosition;
            attachLocalRotation = grab.AttachedLocalRotation;
            targetAnchor = to == from ? grab.CurrentHand : to.PrepareAnchor(grab);
            if (targetAnchor == null)
            {
                NextMove();
                return;
            }

            // Spin about the can's middle, not its base.
            var bounds = grab.GetComponentInChildren<Renderer>() != null ? grab.GetComponentInChildren<Renderer>().bounds.center : grab.transform.position;
            centerLocal = grab.transform.InverseTransformPoint(bounds);
            launchRotation = grab.transform.rotation;
            launchCenter = grab.transform.TransformPoint(centerLocal);

            if (from.HandOff() == null)
            {
                NextMove();
                return;
            }

            thrower = from;
            catcher = to;
            catcher.CatchReady = true;

            if (to == from)
            {
                arcHeight = height;
                flightSeconds = 2f * Mathf.Sqrt(2f * height / 9.81f);
                spinAxis = current.flip ? Vector3.right : Vector3.up;
                spinDegrees = 360f * current.spins * current.turn;
            }
            else
            {
                var distance = Vector3.Distance(launchCenter, TargetCenter());
                arcHeight = 0.05f + 0.12f * distance;
                flightSeconds = Mathf.Clamp(0.26f + 0.32f * distance, 0.28f, 0.48f);
                spinAxis = Vector3.up;
                spinDegrees = 360f * current.spins * current.turn;
            }

            lastPosition = grab.transform.position;
            lastRotation = grab.transform.rotation;
            flightVelocity = Vector3.zero;
            flightAngular = Vector3.zero;

            // Where the catching hand was when the can left: the flight may follow it a little, never far
            // (V0.15 - a pass used to chase a receiving hand anywhere).
            launchTargetCenter = TargetCenter();

            // The release sound comes from SprayCanController when the hand lets go (until V0.15 a toss also
            // played its own here: two release sounds per toss).
            HapticFeedback.Pulse(from.IsLeftHand, 0.12f, 0.02f);
            // The flick that sent it.
            SetFlourish(from, Quaternion.AngleAxis(to == from ? 14f : 8f, Vector3.forward));
            Enter(State.Flight);
        }

        Vector3 TargetCenter() => targetAnchor.TransformPoint(attachLocalPosition + attachLocalRotation * Vector3.Scale(grab.transform.localScale, centerLocal));

        Quaternion TargetRotation() => targetAnchor.rotation * attachLocalRotation;

        void UpdateFlight()
        {
            // A hand took it out of the air on its own (the other hand grabbing it): it is that hand's now, and the
            // run is over - the moves were the first hand's.
            if (grab.IsGrabbed)
            {
                stopRequested = true;
                FinishFlight(caught: false);
                return;
            }

            if (!grab.InFlight)
            {
                FinishFlight(caught: false);
                return;
            }

            var dt = Time.deltaTime;
            var s = Mathf.Clamp01(stateTime / Mathf.Max(0.05f, flightSeconds));

            // Let go of the grip mid-air: the player dropped it on purpose. No catch.
            if (catcher.GripValue < 0.45f || catcher.Held != null || targetAnchor == null)
            {
                Drop();
                return;
            }

            var targetCenter = TargetCenter();
            var targetRotation = TargetRotation();
            var self = thrower == catcher;

            // A toss flies its own arc from where it left and only bends toward the hand on the way down (the hand
            // may have drifted a little); a pass follows the other hand all the way (it is a short throw at it).
            Vector3 center;
            Quaternion baseRotation;
            if (self)
            {
                var home = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.95f, s));
                center = Vector3.Lerp(launchCenter, targetCenter, home) + Vector3.up * (4f * arcHeight * s * (1f - s));
                baseRotation = Quaternion.Slerp(launchRotation, targetRotation, home);
            }
            else
            {
                var e = s * s * (3f - 2f * s);
                center = Vector3.Lerp(launchCenter, targetCenter, e) + Vector3.up * (4f * arcHeight * s * (1f - s));
                baseRotation = Quaternion.Slerp(launchRotation, targetRotation, e);
            }

            var rotation = baseRotation * Quaternion.AngleAxis(spinDegrees * s, spinAxis);
            var position = center - rotation * Vector3.Scale(grab.transform.lossyScale, centerLocal);

            // The catching hand went somewhere else: a miss, and the can falls - for a toss (on the way down) and
            // for a pass (any time: it is a throw at where that hand was, not a can that chases it).
            if ((self ? s > 0.6f : s > 0.05f) && (targetCenter - launchTargetCenter).magnitude > catchTolerance)
            {
                Drop();
                return;
            }

            if (s >= 1f)
            {
                grab.transform.SetPositionAndRotation(targetAnchor.TransformPoint(attachLocalPosition), targetRotation);
                if (catcher.Catch(grab, targetAnchor, self ? 0.45f : 0.35f))
                {
                    Catches++;
                    HapticFeedback.Pulse(catcher.IsLeftHand, 0.3f, 0.035f);
                    // The catch is absorbed by the wrist.
                    SetFlourish(catcher, Quaternion.AngleAxis(12f, Vector3.forward));
                    FinishFlight(caught: true);
                }
                else
                {
                    Drop();
                }

                return;
            }

            grab.transform.SetPositionAndRotation(position, rotation);
            if (dt > 0f)
            {
                flightVelocity = (position - lastPosition) / dt;
                (rotation * Quaternion.Inverse(lastRotation)).ToAngleAxis(out var degrees, out var axis);
                degrees = Mathf.DeltaAngle(0f, degrees);
                flightAngular = float.IsNaN(axis.x) ? Vector3.zero : axis * (degrees * Mathf.Deg2Rad / dt);
            }

            lastPosition = position;
            lastRotation = rotation;

            // The thrower's wrist comes back from the flick while the can is up.
            if (!self)
                SetFlourish(thrower, Quaternion.Slerp(Quaternion.AngleAxis(8f, Vector3.forward), Quaternion.identity, s));
            else
                SetFlourish(thrower, Quaternion.Slerp(Quaternion.AngleAxis(14f, Vector3.forward), Quaternion.AngleAxis(4f, Vector3.forward), s));
        }

        void Drop()
        {
            Drops++;
            grab.EndFlight(flightVelocity, flightAngular);
            stopRequested = true;
            FinishFlight(caught: false);
        }

        void FinishFlight(bool caught)
        {
            if (thrower != null) thrower.CatchReady = false;
            if (catcher != null) catcher.CatchReady = false;
            if (thrower != null && thrower != catcher)
                SetFlourish(thrower, Quaternion.identity);
            thrower = null;
            catcher = null;
            targetAnchor = null;

            if (!caught)
            {
                state = State.Recovery; // so End() runs (it refuses to end a flight)
                End();
                return;
            }

            Enter(State.Recovery);
        }

        void UpdateRecovery()
        {
            // Stopped: straight back to the plain grip (the wrist eases out on its own).
            if (stopRequested)
            {
                End();
                return;
            }

            var holder = Holder;
            if (holder != null)
            {
                // Out of the absorb (or the move's last wrist turn) back to the plain grip.
                var t = Mathf.Clamp01(stateTime / recoverySeconds);
                var from = holder == leftGrabber ? flourishTargetLeft : flourishTargetRight;
                SetFlourish(holder, Quaternion.Slerp(from, Quaternion.identity, t * t * (3f - 2f * t)));
            }

            if (stateTime >= recoverySeconds + gapSeconds)
            {
                if (!grab.AttachedToHand)
                    End();
                else
                    NextMove();
            }
        }

        // ---------- conditions ----------

        bool TossPossible()
        {
            if (!grab.AttachedToHand || handSpeed > maxHandSpeedForToss || !SafeToMove())
                return false;

            // Room above the can for the arc (a shelf, the workshop sign, a low ceiling).
            var renderer = grab.GetComponentInChildren<Renderer>();
            var origin = renderer != null ? renderer.bounds.center : grab.transform.position;
            var count = Physics.SphereCastNonAlloc(origin, 0.05f, Vector3.up, clearanceHits, maxTossHeight + 0.15f, clearanceMask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var hit = clearanceHits[i].collider;
                if (hit == null || hit.GetComponentInParent<VRGrabInteractable>() == grab)
                    continue;
                return false;
            }

            return true;
        }

        bool PassReady(VRGrabber holder, out VRGrabber receiver)
        {
            receiver = holder != null ? Other(holder) : null;
            if (holder == null || receiver == null || receiver.Held != null || receiver.GripValue <= 0.5f || !SafeToMove())
                return false;
            if (handRig != null && !handRig.IsReady(receiver.IsLeftHand))
                return false;

            var distance = Vector3.Distance(holder.transform.position, receiver.transform.position);
            return distance >= minPassDistance && distance <= maxPassDistance;
        }

        // ---------- the wrist ----------

        void SetFlourish(VRGrabber hand, Quaternion rotation)
        {
            if (hand == null)
                return;
            if (hand == leftGrabber)
                flourishTargetLeft = rotation;
            else
                flourishTargetRight = rotation;
        }

        void ApplyFlourish()
        {
            if (handRig == null)
                return;

            // Nothing to do with the hands while idle and settled: there are two cans (the workshop's too), and
            // only the one playing may touch the wrists.
            var settled = Quaternion.Angle(flourishLeft, Quaternion.identity) < 0.05f && Quaternion.Angle(flourishRight, Quaternion.identity) < 0.05f
                          && flourishTargetLeft == Quaternion.identity && flourishTargetRight == Quaternion.identity;
            if (!Active && settled)
            {
                if (flourishLeft != Quaternion.identity || flourishRight != Quaternion.identity)
                {
                    flourishLeft = flourishRight = Quaternion.identity;
                    handRig.SetFlourish(true, Quaternion.identity);
                    handRig.SetFlourish(false, Quaternion.identity);
                }

                return;
            }

            // Eased, so nothing in the hand ever jumps - in or out of a move, or when a run is cut short.
            var k = 1f - Mathf.Exp(-Time.deltaTime / 0.045f);
            flourishLeft = Quaternion.Slerp(flourishLeft, flourishTargetLeft, k);
            flourishRight = Quaternion.Slerp(flourishRight, flourishTargetRight, k);
            handRig.SetFlourish(true, flourishLeft);
            handRig.SetFlourish(false, flourishRight);
        }

        void OnDisable()
        {
            if (state == State.Flight && grab != null && grab.InFlight)
                grab.EndFlight(Vector3.zero, Vector3.zero);
            state = State.Recovery;
            End();
            if (handRig != null)
            {
                handRig.SetFlourish(true, Quaternion.identity);
                handRig.SetFlourish(false, Quaternion.identity);
            }
        }
    }
}
