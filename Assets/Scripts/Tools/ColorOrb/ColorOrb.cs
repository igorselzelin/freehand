using Freehand.Spray;
using Freehand.VR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.Tools
{
    /// <summary>
    /// The Freehand Color Orb: a sphere of color the player calls up over one
    /// palm and picks from with the other hand. Replaces the V0.6 HSV panel
    /// (VRColorPicker).
    ///
    ///   Summon   the primary button of a hand (X left / A right) forms the
    ///            orb over that palm; the same button again confirms and
    ///            closes it. Grabbing something with that hand closes it too.
    ///   Pick     the other hand points at it - with the can in that hand,
    ///            the can's nozzle is the pointer (aim the can at a color);
    ///            otherwise the controller - and its trigger picks what it
    ///            points at. While the can points at the orb it does not
    ///            spray: the trigger is picking.
    ///              the orb's surface  hue around, saturation centre->rim
    ///              the orbital arc    brightness
    ///              the beads          the six palette colors and the cleaner
    ///   Picked   the color goes straight to the can (ColorManager ->
    ///            SprayCanController, the path the palette rack uses; the
    ///            cleaner sets SprayEmitter.EraseMode, as the rack's cleaner
    ///            does) - one source of truth, no copy of the color here.
    ///
    /// Pointing is analytic (ray against sphere / beads / arc plane): the orb
    /// has no colliders, so it never blocks the spray's raycast, a grab or
    /// physics. The look and its animation are ColorOrbVisual's.
    /// </summary>
    public class ColorOrb : MonoBehaviour
    {
        [SerializeField] ColorOrbSettings settings;
        [SerializeField] ColorOrbVisual visual;

        [Header("Input")]
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string leftSummonAction = "Left Primary Button";
        [SerializeField] string rightSummonAction = "Right Primary Button";
        [SerializeField] string leftTriggerAction = "Left Trigger";
        [SerializeField] string rightTriggerAction = "Right Trigger";

        [Header("Hands")]
        [SerializeField] Transform leftController;
        [SerializeField] Transform rightController;
        [SerializeField] VRGrabber leftGrabber;
        [SerializeField] VRGrabber rightGrabber;
        [SerializeField] HandRig handRig;
        [SerializeField] Transform head;

        [Header("Paint")]
        [SerializeField] ColorManager colorManager;
        [SerializeField] SprayCanController canController;
        [SerializeField] SprayEmitter sprayEmitter;
        [SerializeField] VRGrabInteractable canGrab;

        [Tooltip("Tint of the cleaner bead.")]
        [SerializeField] Color cleanerTint = new Color(0.62f, 0.82f, 0.92f);

        InputAction leftSummon;
        InputAction rightSummon;
        InputAction leftTrigger;
        InputAction rightTrigger;
        bool leftSummonWas;
        bool rightSummonWas;
        bool triggerWas;

        ColorOrbState state = ColorOrbState.Dormant;
        float stateTime;
        bool hostLeft;
        bool suppressing;
        bool positioned;
        Vector3 velocity;

        float value = 1f;
        float hue;
        float saturation;
        ColorOrbTarget hover;
        int hoverBead = -1;
        Vector3 hoverDirection = Vector3.forward;
        float hoverValue;
        Color hoverColor = Color.white;
        Color confirmColor = Color.white;
        bool beamOn;
        Vector3 beamFrom;
        Vector3 beamTo;
        readonly Color[] beadColors = new Color[ColorManager.Count + 1];

        public ColorOrbState State => state;
        public bool HostIsLeft => hostLeft;
        public ColorOrbTarget Hover => hover;
        public int HoverBead => hoverBead;
        public Color HoverColor => hoverColor;
        public float DisplayValue => value;

        /// <summary>Index of the cleaner among the beads (after the palette colors).</summary>
        public static int CleanerBead => ColorManager.Count;

        void Awake()
        {
            ResolveActions();
            for (var i = 0; i < ColorManager.Count; i++)
                beadColors[i] = ColorManager.Palette[i];
            beadColors[CleanerBead] = cleanerTint;
        }

        void Start()
        {
            if (visual != null)
                visual.SetBeadColors(beadColors);
        }

        void OnDisable() => ReleaseSpray();

        void ResolveActions()
        {
            if (inputActions == null)
            {
                Debug.LogError($"{nameof(ColorOrb)}: no InputActionAsset assigned.", this);
                return;
            }

            var map = inputActions.FindActionMap(actionMapName, throwIfNotFound: false);
            if (map == null)
            {
                Debug.LogError($"{nameof(ColorOrb)}: action map '{actionMapName}' not found.", this);
                return;
            }

            leftSummon = map.FindAction(leftSummonAction, throwIfNotFound: false);
            rightSummon = map.FindAction(rightSummonAction, throwIfNotFound: false);
            leftTrigger = map.FindAction(leftTriggerAction, throwIfNotFound: false);
            rightTrigger = map.FindAction(rightTriggerAction, throwIfNotFound: false);
            if (leftSummon == null || rightSummon == null)
                Debug.LogError($"{nameof(ColorOrb)}: summon actions '{leftSummonAction}' / '{rightSummonAction}' not found - the orb cannot be called up.", this);
        }

        void Update()
        {
            if (settings == null || visual == null)
                return;

            // A hand holding something has its button for that object (item
            // inspection) - and could not host the orb anyway.
            var leftPressed = Pressed(leftSummon, ref leftSummonWas) && !Holds(leftGrabber);
            var rightPressed = Pressed(rightSummon, ref rightSummonWas) && !Holds(rightGrabber);
            stateTime += Time.deltaTime;

            switch (state)
            {
                case ColorOrbState.Dormant:
                    if (leftPressed || rightPressed)
                        Summon(leftPressed);
                    break;

                case ColorOrbState.Dismiss:
                    if (stateTime >= settings.dismissSeconds)
                        Enter(ColorOrbState.Dormant);
                    break;

                default:
                    // Either summon button, or a grab with the orb's hand,
                    // closes it.
                    var hostGrabber = hostLeft ? leftGrabber : rightGrabber;
                    if (leftPressed || rightPressed || (hostGrabber != null && hostGrabber.Held != null))
                    {
                        Dismiss();
                        break;
                    }

                    if (state == ColorOrbState.Summon && stateTime >= settings.summonSeconds)
                        Enter(ColorOrbState.Idle);
                    else if (state == ColorOrbState.Confirmed && stateTime >= settings.confirmSeconds)
                        Enter(hover != ColorOrbTarget.None ? ColorOrbState.Selecting : ColorOrbState.Idle);

                    UpdatePointer();
                    break;
            }

            if (state != ColorOrbState.Dormant)
                FollowPalm(false);

            visual.Tick(new ColorOrbFrame
            {
                State = state,
                StateTime = stateTime,
                Value = value,
                Hue = hue,
                Saturation = saturation,
                Hover = hover,
                HoverLocalDirection = hoverDirection,
                HoverBead = hoverBead,
                HoverValue = hoverValue,
                HoverColor = hoverColor,
                ConfirmColor = confirmColor,
                BeamOn = beamOn,
                BeamFrom = beamFrom,
                BeamTo = beamTo,

                // The arc goes on the side away from the orb's hand.
                ArcSide = hostLeft ? -1f : 1f,
            });
        }

        static bool Holds(VRGrabber grabber) => grabber != null && grabber.Held != null;

        static bool Pressed(InputAction action, ref bool was)
        {
            var down = action != null && action.ReadValue<float>() > 0.5f;
            var pressed = down && !was;
            was = down;
            return pressed;
        }

        // ---------- states ----------

        void Summon(bool left)
        {
            hostLeft = left;
            positioned = false;

            // The orb opens on the color in the can (the one the pointing
            // hand holds - there may be two cans).
            UseCanIn(!hostLeft);
            if (activeColors != null)
            {
                Color.RGBToHSV(activeColors.CurrentColor, out hue, out saturation, out var v);
                value = Mathf.Max(settings.minDisplayValue, v);
            }

            ClearHover();
            FollowPalm(true);
            Enter(ColorOrbState.Summon);
            HapticFeedback.Pulse(hostLeft, 0.2f, 0.04f);
        }

        void Dismiss()
        {
            ClearHover();
            ReleaseSpray();
            Enter(ColorOrbState.Dismiss);
        }

        void Enter(ColorOrbState next)
        {
            state = next;
            stateTime = 0f;
        }

        void ClearHover()
        {
            hover = ColorOrbTarget.None;
            hoverBead = -1;
            beamOn = false;
        }

        // ---------- placement ----------

        // Most the orb backs off a hand whose fingers reach toward it, meters.
        const float MaxFingerBackOff = 0.06f;

        // Floats off the palm (biased upward), catching up with the hand
        // smoothly, turned toward the player: the orb's +Z - the frame its
        // colors are laid out in - faces between the head and the pointing
        // hand (settings.pointerFacing).
        void FollowPalm(bool snap)
        {
            Vector3 target;
            if (handRig != null && handRig.TryGetPalm(hostLeft, out var palm, out var facing))
            {
                var outward = (facing * (1f - settings.upwardBias) + Vector3.up * settings.upwardBias).normalized;
                target = palm + outward * settings.hoverDistance;

                // Further out while fingers reach into the beads' orbit (a
                // curled or tilted hand), by at most MaxFingerBackOff.
                var reach = settings.beadOrbitRadius + settings.beadRadius + settings.fingerClearance;
                var backOff = 0f;
                for (var i = 0; i < 4 && backOff < MaxFingerBackOff; i++)
                {
                    var gap = reach - handRig.FingerClearance(hostLeft, target);
                    if (gap <= 0.001f)
                        break;

                    gap = Mathf.Min(gap, MaxFingerBackOff - backOff);
                    target += outward * gap;
                    backOff += gap;
                }
            }
            else
            {
                var controller = hostLeft ? leftController : rightController;
                if (controller == null)
                    return;
                target = controller.position + Vector3.up * settings.hoverDistance;
            }

            if (snap || !positioned)
            {
                transform.position = target;
                velocity = Vector3.zero;
                positioned = true;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(transform.position, target, ref velocity, settings.followSmoothTime);
            }

            var toHead = head != null ? head.position - transform.position : Vector3.back;
            if (toHead.sqrMagnitude > 1e-6f)
            {
                // Turned partway toward the pointing hand: from that hand,
                // off to the side, a head-on orb hides its far beads behind
                // the core.
                var faceDirection = toHead.normalized;
                var pointerHand = hostLeft ? rightController : leftController;
                var toPointer = pointerHand != null ? pointerHand.position - transform.position : Vector3.zero;
                if (toPointer.sqrMagnitude > 1e-6f)
                    faceDirection = Vector3.Slerp(faceDirection, toPointer.normalized, settings.pointerFacing);

                var turn = Quaternion.LookRotation(faceDirection, Vector3.up);
                transform.rotation = snap ? turn : Quaternion.Slerp(transform.rotation, turn, 1f - Mathf.Exp(-settings.turnSpeed * Time.deltaTime));
            }
        }

        // ---------- pointing and picking ----------

        // The can the colors go to: the one in the pointing hand, or the
        // player's own can when that hand is empty.
        ColorManager activeColors;
        SprayCanController activeController;
        SprayEmitter activeEmitter;
        VRGrabInteractable activeGrab;
        SprayEmitter suppressedEmitter;

        void UseCanIn(bool pointerLeft)
        {
            var grabber = pointerLeft ? leftGrabber : rightGrabber;
            var held = grabber != null ? grabber.Held : null;
            var heldController = held != null ? held.GetComponent<SprayCanController>() : null;
            if (heldController != null)
            {
                activeController = heldController;
                activeColors = held.GetComponent<ColorManager>();
                activeEmitter = held.GetComponent<SprayEmitter>();
                activeGrab = held;
                return;
            }

            activeController = canController;
            activeColors = colorManager;
            activeEmitter = sprayEmitter;
            activeGrab = canGrab;
        }

        void UpdatePointer()
        {
            var pointerLeft = !hostLeft;
            UseCanIn(pointerLeft);
            var holdsCan = activeGrab != null && activeGrab.IsGrabbed && activeGrab.HeldByLeftHand == pointerLeft;
            Transform pointer;
            if (holdsCan && activeEmitter != null && activeEmitter.SprayOrigin != null)
                pointer = activeEmitter.SprayOrigin;
            else
                pointer = pointerLeft ? leftController : rightController;

            if (pointer == null || state == ColorOrbState.Summon)
            {
                ClearHover();
                ReleaseSpray();
                return;
            }

            var previousHover = hover;
            var previousBead = hoverBead;
            var origin = pointer.position;
            var direction = pointer.forward;
            var hit = Aim(origin, direction, out var distance);

            beamOn = hit;
            beamFrom = origin;
            beamTo = origin + direction * distance;

            // The can pointing at the orb is picking, not painting.
            if (hit && holdsCan)
                HoldSpray();
            else
                ReleaseSpray();

            if (hit && state == ColorOrbState.Idle)
                Enter(ColorOrbState.Selecting);
            else if (!hit && state == ColorOrbState.Selecting)
                Enter(ColorOrbState.Idle);

            if (hit && (hover != previousHover || hoverBead != previousBead))
            {
                HapticFeedback.Pulse(pointerLeft, 0.06f, 0.012f);
                visual.PlayHover(HoverPitch());
            }

            var triggerAction = pointerLeft ? leftTrigger : rightTrigger;
            var triggerDown = triggerAction != null && triggerAction.ReadValue<float>() > 0.5f;
            if (hit && triggerDown && !triggerWas)
                Pick(pointerLeft);
            triggerWas = triggerDown;
        }

        // Half-width, meters, of the value arc as a target before aim assist
        // (its tube is 2.2 mm thick).
        const float ArcHalfWidth = 0.004f;

        // What the pointer is on, in the orb root's space (+Z toward the
        // player): the nearest target it goes straight through, else the one
        // it misses by the least within the aim assist. Ranking near misses
        // by how near they are - not by which comes first along the ray -
        // keeps a bead's assist zone from swallowing the arc beside it.
        bool Aim(Vector3 worldOrigin, Vector3 worldDirection, out float distance)
        {
            hover = ColorOrbTarget.None;
            hoverBead = -1;
            distance = settings.maxPointerDistance;

            var o = transform.InverseTransformPoint(worldOrigin);
            var d = transform.InverseTransformDirection(worldDirection).normalized;
            var assist = settings.aimAssist;
            var bestMiss = float.MaxValue;
            var bestT = settings.maxPointerDistance;

            if (RaySphere(o, d, Vector3.zero, visual.CoreRadius + assist * 0.5f, out var tCore) && tCore < bestT)
            {
                var miss = Mathf.Max(0f, MissDistance(o, d, Vector3.zero) - visual.CoreRadius);
                if (Better(miss, tCore, ref bestMiss, ref bestT))
                {
                    hover = ColorOrbTarget.Core;
                    hoverDirection = (o + d * tCore).normalized;
                    hoverColor = ColorOrbVisual.ColorAt(hoverDirection, value, out _, out _);
                }
            }

            if (visual.BeadsReady)
            {
                for (var i = 0; i < visual.BeadCount; i++)
                {
                    var center = visual.BeadLocalPosition(i);
                    var tBead = Vector3.Dot(center - o, d);
                    var miss = Mathf.Max(0f, MissDistance(o, d, center) - visual.BeadRadius);
                    if (tBead > 0f && tBead < settings.maxPointerDistance && miss < assist && Better(miss, tBead, ref bestMiss, ref bestT))
                    {
                        hover = ColorOrbTarget.Bead;
                        hoverBead = i;
                        hoverColor = beadColors[i];
                    }
                }
            }

            // The arc lies in the orb's face plane (z = 0), on the side away
            // from the orb's hand (visual.ArcSide).
            if (Mathf.Abs(d.z) > 1e-4f)
            {
                var tArc = -o.z / d.z;
                if (tArc > 0f && tArc < settings.maxPointerDistance)
                {
                    var p = o + d * tArc;
                    var radial = new Vector2(p.x, p.y).magnitude;
                    var angle = Mathf.Atan2(p.y, p.x * visual.ArcSide) * Mathf.Rad2Deg;
                    var half = visual.ArcHalfAngle;
                    var miss = Mathf.Max(0f, Mathf.Abs(radial - visual.ArcRadius) - ArcHalfWidth);
                    if (miss < assist && angle >= -half - 4f && angle <= half + 4f && Better(miss, tArc, ref bestMiss, ref bestT))
                    {
                        hover = ColorOrbTarget.ValueArc;
                        hoverValue = Mathf.Clamp01((angle + half) / (2f * half));
                        hoverColor = Color.HSVToRGB(hue, saturation, hoverValue);
                    }
                }
            }

            distance = bestT;
            return hover != ColorOrbTarget.None;
        }

        // Where on the orb the pointer is, 0-1, as pitch for the hover tick.
        float HoverPitch()
        {
            switch (hover)
            {
                case ColorOrbTarget.Core:
                    Color.RGBToHSV(hoverColor, out var hueOnCore, out _, out _);
                    return hueOnCore;
                case ColorOrbTarget.Bead:
                    return visual.BeadCount > 1 ? hoverBead / (float)(visual.BeadCount - 1) : 0.5f;
                case ColorOrbTarget.ValueArc:
                    return hoverValue;
                default:
                    return 0.5f;
            }
        }

        // Straight hits (miss 0) rank by distance along the ray, near misses
        // by how near they come.
        static bool Better(float miss, float t, ref float bestMiss, ref float bestT)
        {
            var better = miss <= 0f && bestMiss <= 0f ? t < bestT : miss < bestMiss;
            if (better)
            {
                bestMiss = miss;
                bestT = t;
            }

            return better;
        }

        // How far the ray passes from a point (the point behind it: its distance).
        static float MissDistance(Vector3 origin, Vector3 direction, Vector3 point)
        {
            var toPoint = point - origin;
            var along = Mathf.Max(0f, Vector3.Dot(toPoint, direction));
            return (toPoint - direction * along).magnitude;
        }

        static bool RaySphere(Vector3 origin, Vector3 direction, Vector3 center, float radius, out float t)
        {
            var oc = origin - center;
            var b = Vector3.Dot(oc, direction);
            var c = oc.sqrMagnitude - radius * radius;
            var discriminant = b * b - c;
            t = 0f;
            if (discriminant < 0f)
                return false;

            var root = Mathf.Sqrt(discriminant);
            t = -b - root;
            if (t < 0f)
                t = -b + root;
            return t >= 0f;
        }

        // The pick goes straight to the can, through the same calls the
        // palette rack makes.
        void Pick(bool pointerLeft)
        {
            switch (hover)
            {
                case ColorOrbTarget.Core:
                {
                    var color = ColorOrbVisual.ColorAt(hoverDirection, value, out hue, out saturation);
                    LoadColor(activeColors != null ? activeColors.SelectCustom(color) : color);
                    break;
                }

                case ColorOrbTarget.Bead when hoverBead == CleanerBead:
                    if (activeEmitter != null)
                        activeEmitter.EraseMode = true;
                    confirmColor = cleanerTint;
                    break;

                case ColorOrbTarget.Bead:
                {
                    var color = activeColors != null ? activeColors.Select(hoverBead) : beadColors[hoverBead];
                    Color.RGBToHSV(color, out hue, out saturation, out var v);
                    value = Mathf.Max(settings.minDisplayValue, v);
                    LoadColor(color);
                    break;
                }

                case ColorOrbTarget.ValueArc:
                {
                    value = Mathf.Max(0.02f, hoverValue);
                    var color = Color.HSVToRGB(hue, saturation, value);
                    LoadColor(activeColors != null ? activeColors.SelectCustom(color) : color);
                    break;
                }

                default:
                    return;
            }

            visual.PlayConfirm(confirmColor);
            HapticFeedback.Pulse(pointerLeft, 0.35f, 0.05f);
            Enter(ColorOrbState.Confirmed);
        }

        void LoadColor(Color color)
        {
            if (activeEmitter != null)
                activeEmitter.EraseMode = false;
            if (activeController != null)
                activeController.ApplyColor(color);
            confirmColor = color;
        }

        void HoldSpray()
        {
            if (activeEmitter == null || (suppressing && suppressedEmitter == activeEmitter))
                return;
            ReleaseSpray();
            activeEmitter.Suppressed = true;
            suppressedEmitter = activeEmitter;
            suppressing = true;
        }

        void ReleaseSpray()
        {
            if (!suppressing)
                return;
            if (suppressedEmitter != null)
                suppressedEmitter.Suppressed = false;
            suppressedEmitter = null;
            suppressing = false;
        }
    }
}
