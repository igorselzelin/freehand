using UnityEngine;

namespace Freehand.VR
{
    [System.Serializable]
    public struct FingerBones
    {
        public Transform segment1;
        public Transform segment2;
        public Transform segment3;
    }

    /// <summary>
    /// Procedural finger posing for the rigged fps-hands.fbx skeleton,
    /// driven only by grip state and the held object's real geometry (no
    /// finger tracking - Touch controllers do not provide it). Poses only the
    /// fingers: where the held object is, and how it moves once let go, is
    /// VRGrabInteractable's and physics' business, never this component's.
    ///
    /// Joint axes are measured, not assumed: each finger flexes about
    /// Cross(its proximal direction, the palm-facing direction), both taken
    /// from the bind pose (see HandAnatomy) and converted into each bone's
    /// own local space, so a positive angle always folds toward the palm -
    /// on either hand, whatever the FBX bone rolls are. The index MCP also
    /// abducts about the palm normal.
    ///
    /// What the fingers close around is the held object's real shape: its
    /// GripProfile (stacked cylinders along the grip axis, the can body up
    /// to the valve cup) plus, above that, the actual top of the part the
    /// index presses (VRGrabInteractable.FingerRestSurface - the seated
    /// cap's mesh, as a SurfaceHeightField). No finger is allowed into
    /// either.
    ///
    /// Poses (every joint blends toward its target, no snapping):
    ///   Free      slight curl; opening a little while something grabbable
    ///             is within reach (ready to catch it); fully open for a
    ///             moment right after letting go.
    ///   Can grip  a cylinder-like object (GripRadius > 0), solved once per
    ///             object and hand placement:
    ///               middle/ring/pinky - each phalanx lies on the body;
    ///               thumb - its metacarpal swings into opposition, around
    ///                 the far side, then wraps it;
    ///               index - two solved poses: REST, curled lightly on the
    ///                 body's upper part and clear of the cap, and ACTUATOR,
    ///                 its pad on the cap's real top (on the finger scoop
    ///                 when it can reach it). The trigger (FingerApproach,
    ///                 smoothed) moves it continuously from one to the other,
    ///                 lifting it clear of anything on the way; while the
    ///                 nozzle is pressed ACTUATOR follows the cap down.
    ///             Easing off the grip trigger loosens the fist a little
    ///             before the object is let go.
    ///   Pinch     small objects (GripRadius 0, caps): thumb and index close
    ///             on the part from opposite sides; the part settles the last
    ///             centimetres into those fingers (VRGrabInteractable
    ///             .SettleInHand). A cap still seated on the can (being
    ///             pulled) is pinched where it is.
    /// </summary>
    public class HandBonePoseController : MonoBehaviour
    {
        const int Thumb = 0;
        const int Index = 1;
        const int Middle = 2;
        const int BoneCount = 15;

        // How much of the spray, meters from the nozzle, the fingers keep out of.
        const float JetLength = 0.08f;

        // The distal pad's radius relative to fingerRadius (the phalanges'):
        // fps-hands' fingertips are slimmer than its finger bases.
        const float FingertipPadScale = 0.75f;

        [SerializeField] bool isLeftHand;
        [SerializeField] VRGrabber grabber;

        [Header("Bones (assigned by the scene builder from the real imported rig)")]
        [SerializeField] FingerBones thumb;
        [SerializeField] FingerBones index;
        [SerializeField] FingerBones middle;
        [SerializeField] FingerBones ring;
        [SerializeField] FingerBones pinky;

        [Tooltip("Fingertip ('_end') bones: thumb, index, middle, ring, pinky.")]
        [SerializeField] Transform[] tips = new Transform[5];

        [Header("Relaxed / pinch - degrees of curl per joint")]
        [SerializeField] float idleCurl = 12f;
        [SerializeField] float idleThumbCurl = 5f;
        [SerializeField] float pinchCurl = 35f;

        [Header("Joint ranges - degrees")]
        [Tooltip("Flexion limits of the finger joints: MCP, PIP, DIP.")]
        [SerializeField] Vector3 fingerFlexion = new Vector3(90f, 105f, 80f);

        [Tooltip("Flexion limits of the thumb joints: CMC, MCP, IP.")]
        [SerializeField] Vector3 thumbFlexion = new Vector3(40f, 60f, 80f);

        [Tooltip("Index MCP side-to-side range (toward thumb / toward little finger). 30: the top of the human range, and what lets the index arch over a spray cap from the side of the can.")]
        [SerializeField] float indexAbduction = 30f;

        [Tooltip("How far the thumb's metacarpal may swing from its rest pose into opposition.")]
        [SerializeField] float thumbOpposition = 80f;

        [Header("Contact")]
        [Tooltip("Finger radius at the contact, meters: a phalanx stops on the object when its surface - not its bone - reaches it.")]
        [SerializeField] float fingerRadius = 0.009f;

        [Tooltip("Gap, meters, the resting index keeps from the object - relaxed on the grip, not pressing anything.")]
        [SerializeField] float indexRestGap = 0.004f;

        [Tooltip("Clearance, meters, the resting index keeps from the cap: the cap stays free while the trigger is released.")]
        [SerializeField] float indexRestCapClearance = 0.01f;

        [Tooltip("How far, meters, the fist's upper wrapping finger (the middle) may reach above the top of the body - onto the shoulder, never up to the cap or into the spray. The hand sits as high as this allows, which is what lets the index reach over the top.")]
        [SerializeField] float fistShoulderAllowance = 0.016f;

        [Tooltip("How far, meters, the resting index stays below the cap's seat: on the body and shoulder, out of the spray.")]
        [SerializeField] float indexRestBelowSeat = 0.008f;

        [SerializeField] float thumbRadius = 0.0105f;

        [Tooltip("Extra clearance, meters, every holding finger keeps from the spray (VRGrabInteractable.KeepClearOf) - on top of its own radius.")]
        [SerializeField] float jetClearance = 0.005f;

        [Header("Motion")]
        [SerializeField] float blendSpeed = 14f;

        [Tooltip("How fast the index follows the trigger, 1/s: smooths the analog reading without lagging a deliberate press.")]
        [SerializeField] float indexFollowSpeed = 18f;

        [Tooltip("Seconds the hand stays open after letting go, before relaxing.")]
        [SerializeField] float releaseOpenSeconds = 0.2f;

        [SerializeField] float releaseBlendSpeed = 22f;

        [Tooltip("How far (0-1) an empty hand opens while something grabbable is within reach.")]
        [SerializeField] float catchOpen = 0.6f;

        [Tooltip("How far (0-1) the fist loosens toward open as the grip trigger eases off, just before it lets go.")]
        [SerializeField] float releaseLoosen = 0.35f;

        [Tooltip("Seconds a pinched part takes to settle into the fingers.")]
        [SerializeField] float pinchSettleSeconds = 0.1f;

        [Tooltip("How far (0-1) the index opens off the actuator while an inspection turns the held can in the fist.")]
        [SerializeField] float inspectionIndexLift = 0.45f;

        public bool HasAnatomy { get; private set; }
        public HandAnatomy Anatomy { get; private set; }

        /// <summary>The thumb's and the index's fingertips, as posed right now (the axis a pinched part can roll about).</summary>
        public Vector3 ThumbTip => HasAnatomy ? Tip(Thumb).position : transform.position;
        public Vector3 IndexTip => HasAnatomy ? Tip(Index).position : transform.position;

        /// <summary>Between the thumb and index fingertips, as posed right now - where this hand takes small objects (caps) from.</summary>
        public Vector3 PinchPoint => HasAnatomy ? (Tip(Thumb).position + Tip(Index).position) * 0.5f : transform.position;

        /// <summary>Index pose angles (MCP flex, MCP abduction, PIP flex): resting on the grip, on the actuator, and as posed now.</summary>
        public Vector3 IndexRestAngles => indexRest;
        public Vector3 IndexActuatorAngles => indexActuator;

        /// <summary>The trigger travel the index is posed for right now (smoothed FingerApproach).</summary>
        public float IndexApproach => smoothedApproach;

        struct Shape
        {
            public VRGrabInteractable.GripSection[] profile;
            public Vector3 axisPoint;
            public Vector3 axis;

            // The part on top (a seated cap), as seen from above in its own space.
            public SurfaceHeightField top;
            public Transform topSpace;

            // Height along the axis where the pressed part begins (the cap's seat).
            public float seat;

            // A ray the fingers keep clear of (the spray), or null.
            public Transform jet;

            /// <summary>Clearance of a sphere from the first JetLength of the jet: negative = in it.</summary>
            public float JetGap(Vector3 point, float radius)
            {
                if (jet == null)
                    return float.PositiveInfinity;

                var offset = point - jet.position;
                var along = Mathf.Clamp(Vector3.Dot(offset, jet.forward), 0f, JetLength);
                return (offset - jet.forward * along).magnitude - radius;
            }

            public float Clearance(Vector3 point, float margin)
            {
                var clearance = VRGrabInteractable.ProfileClearance(profile, axisPoint, axis, point, margin);
                return top != null ? Mathf.Min(clearance, TopGap(point, margin)) : clearance;
            }

            public float TopGap(Vector3 point, float margin)
            {
                if (top == null)
                    return float.PositiveInfinity;

                var scale = Mathf.Max(0.0001f, topSpace.lossyScale.x);
                return top.Gap(topSpace.InverseTransformPoint(point), margin / scale) * scale;
            }

            public Shape BodyOnly
            {
                get
                {
                    var copy = this;
                    copy.top = null;
                    return copy;
                }
            }

            public Shape TopOnly
            {
                get
                {
                    var copy = this;
                    copy.profile = System.Array.Empty<VRGrabInteractable.GripSection>();
                    return copy;
                }
            }
        }

        readonly Transform[] bones = new Transform[BoneCount];
        readonly Quaternion[] bindLocal = new Quaternion[BoneCount];
        readonly Vector3[] flexAxis = new Vector3[BoneCount];
        readonly Quaternion[] current = new Quaternion[BoneCount];
        readonly Quaternion[] target = new Quaternion[BoneCount];
        readonly Quaternion[] holdPose = new Quaternion[BoneCount];
        readonly Quaternion[] pinchPose = new Quaternion[BoneCount];
        readonly Quaternion[] savedIndex = new Quaternion[3];
        Vector3 indexAbductionAxis;
        Quaternion thumbOpposed;
        Quaternion thumbBindBase;

        Vector3 indexRest;
        Vector3 indexActuator;
        float smoothedApproach;
        Vector3 solvedRest;
        float solvedApproach = -1f;

        VRGrabInteractable posedFor;
        bool pinchAttached;
        Vector3 pinchedAt;
        float releaseTimer;
        float reachOpen;
        bool warnedNoProfile;

        // One remembered can-grip solution: the hand-object relation is the
        // same on every grab of the same object, so a catch costs nothing.
        VRGrabInteractable cachedFor;
        Mesh cachedSurface;
        Vector3 cachedRest;
        [System.NonSerialized] Quaternion[] cachedHold;
        Vector3 cachedIndexRest;
        Vector3 cachedIndexActuator;

        void Awake()
        {
            var chains = new[] { thumb, index, middle, ring, pinky };
            for (var f = 0; f < 5; f++)
            {
                bones[f * 3] = chains[f].segment1;
                bones[f * 3 + 1] = chains[f].segment2;
                bones[f * 3 + 2] = chains[f].segment3;
            }

            for (var i = 0; i < BoneCount; i++)
                bindLocal[i] = bones[i] != null ? bones[i].localRotation : Quaternion.identity;

            var fingers = new[] { index, middle, ring, pinky };
            var fingerTips = new[] { Tip(1), Tip(2), Tip(3), Tip(4) };
            var anatomy = default(HandAnatomy);
            HasAnatomy = System.Array.IndexOf(bones, null) < 0 && Tip(Thumb) != null
                && HandAnatomy.TryMeasure(transform, fingers, fingerTips, isLeftHand, out anatomy);
            if (!HasAnatomy)
            {
                Debug.LogError($"{nameof(HandBonePoseController)}: could not measure the hand from its bones - finger bones, tips or the pinky metacarpal not assigned?", this);
                return;
            }

            Anatomy = anatomy;
            var palmFacingWorld = transform.rotation * anatomy.PalmFacing;
            for (var f = 0; f < 5; f++)
            {
                var axisWorld = Vector3.Cross((bones[f * 3 + 1].position - bones[f * 3].position).normalized, palmFacingWorld).normalized;
                for (var s = 0; s < 3; s++)
                    flexAxis[f * 3 + s] = Quaternion.Inverse(bones[f * 3 + s].rotation) * axisWorld;
            }

            indexAbductionAxis = Quaternion.Inverse(bones[Index * 3].rotation) * palmFacingWorld;
            thumbBindBase = bindLocal[0];
            thumbOpposed = thumbBindBase;

            for (var i = 0; i < BoneCount; i++)
            {
                current[i] = RelaxedPose(i);
                bones[i].localRotation = current[i];
            }
        }

        void LateUpdate()
        {
            if (!HasAnatomy)
                return;

            var held = grabber != null ? grabber.Held : null;
            var holding = held != null && held.GripRadius > 0f;
            var pinching = held != null && !holding;

            if (held != posedFor)
            {
                // Just let go: the hand opens off the object.
                if (posedFor != null && held == null)
                    releaseTimer = releaseOpenSeconds;

                posedFor = held;
                if (holding)
                    SolveHold(held);
                else if (pinching)
                    SolvePinch(held, held.AttachedToHand);
            }
            else if (pinching)
            {
                // The seated cap came off into the fingers: it settles into the
                // pinch. Still seated (being pulled): the fingers stay on it.
                if (held.AttachedToHand != pinchAttached)
                    SolvePinch(held, held.AttachedToHand);
                else if (!pinchAttached && (InHand(PinchedPartCenter(held)) - pinchedAt).sqrMagnitude > 1e-6f)
                    SolvePinch(held, false);
            }

            var dt = Time.deltaTime;
            releaseTimer -= dt;
            // Open for something within reach - or for a can a freestyle move is sending into this hand.
            var wantsOpen = held == null && releaseTimer <= 0f && grabber != null && (grabber.GrabbableInReach || grabber.CatchReady);
            reachOpen = Mathf.MoveTowards(reachOpen, wantsOpen ? 1f : 0f, dt / 0.15f);

            for (var i = 0; i < BoneCount; i++)
            {
                if (held == null)
                    target[i] = releaseTimer > 0f ? OpenPose(i) : Quaternion.Slerp(RelaxedPose(i), OpenPose(i), reachOpen * catchOpen);
                else
                    target[i] = holding ? holdPose[i] : pinchPose[i];
            }

            if (holding && held.FingerRest != null)
                FollowFingerRest(held);

            // Easing off the grip trigger: the fist starts to open just before
            // the object is let go (it lets go at 0.5).
            if (holding && grabber != null)
            {
                var loosen = Mathf.InverseLerp(0.95f, 0.55f, grabber.GripValue) * releaseLoosen;
                if (loosen > 0f)
                    for (var i = 0; i < BoneCount; i++)
                        target[i] = Quaternion.Slerp(target[i], OpenPose(i), loosen);
            }

            var t = 1f - Mathf.Exp(-(releaseTimer > 0f ? releaseBlendSpeed : blendSpeed) * dt);
            for (var i = 0; i < BoneCount; i++)
            {
                current[i] = Quaternion.Slerp(current[i], target[i], t);
                bones[i].localRotation = current[i];
            }
        }

        Quaternion RelaxedPose(int bone) => Flexed(bone, bone < 3 ? idleThumbCurl : idleCurl);

        // Nearly flat: the bind pose of this rig is an open hand.
        Quaternion OpenPose(int bone) => Flexed(bone, bone < 3 ? 2f : 3f);

        Quaternion Flexed(int bone, float degrees) => bindLocal[bone] * Quaternion.AngleAxis(degrees, flexAxis[bone]);

        Shape ShapeOf(VRGrabInteractable held)
        {
            var profile = held.GripProfile;
            if (profile == null || profile.Length == 0)
            {
                // Degrades to an endless cylinder of GripRadius - said once,
                // loudly: the scene builder should have measured the shape.
                if (!warnedNoProfile)
                    Debug.LogError($"{nameof(HandBonePoseController)}: '{held.name}' has no grip profile - run the scene builder. Fingers close around an endless cylinder of GripRadius instead.", held);
                warnedNoProfile = true;
                profile = new[] { new VRGrabInteractable.GripSection { bottom = -10f, top = 10f, radius = held.GripRadius } };
            }

            var shape = new Shape { profile = profile, axisPoint = held.GripAxisPoint, axis = held.GripAxisDirection, jet = held.KeepClearOf };
            if (held.FingerRest == null)
                return shape;

            // The profile stops where the pressed part begins: at the seated
            // cap's seat (the cap itself is its real surface, below), or at the
            // rest point on a bare stem. Both move with the nozzle; the profile
            // does not.
            var surface = held.FingerRestSurface;
            var field = surface != null ? SurfaceHeightField.For(surface.sharedMesh) : null;
            var clipAt = field != null
                ? Vector3.Dot(surface.transform.position - shape.axisPoint, shape.axis)
                : Vector3.Dot(held.FingerRest.position - shape.axisPoint, shape.axis);

            if (clippedProfile == null || clippedProfile.Length != profile.Length)
                clippedProfile = new VRGrabInteractable.GripSection[profile.Length];
            for (var i = 0; i < profile.Length; i++)
            {
                clippedProfile[i] = profile[i];
                clippedProfile[i].top = Mathf.Min(profile[i].top, clipAt);
                if (clippedProfile[i].top < clippedProfile[i].bottom)
                    clippedProfile[i].radius = 0f;
            }

            shape.profile = clippedProfile;
            shape.seat = clipAt;
            if (field != null)
            {
                shape.top = field;
                shape.topSpace = surface.transform;
            }

            return shape;
        }

        VRGrabInteractable.GripSection[] clippedProfile;

        // ---------- can grip (bones are posed directly while solving; the
        // LateUpdate blend writes the real pose back afterwards) ----------

        void SolveHold(VRGrabInteractable held)
        {
            var surfaceMesh = held.FingerRestSurface != null ? held.FingerRestSurface.sharedMesh : null;
            var restInHand = held.FingerRest != null ? InHand(held.FingerRest.position) : Vector3.zero;
            smoothedApproach = 0f;
            solvedApproach = -1f;
            solvedRest = restInHand;

            if (cachedFor == held && cachedSurface == surfaceMesh && (cachedRest - restInHand).sqrMagnitude < 1e-7f && cachedHold != null)
            {
                System.Array.Copy(cachedHold, holdPose, BoneCount);
                indexRest = cachedIndexRest;
                indexActuator = cachedIndexActuator;
                return;
            }

            var shape = ShapeOf(held);
            thumbOpposed = thumbBindBase;
            for (var i = 0; i < BoneCount; i++)
                bones[i].localRotation = bindLocal[i];

            for (var f = Middle; f < 5; f++)
                WrapFinger(f, shape);

            SolveThumb(shape);

            if (held.FingerRest != null)
            {
                indexRest = SolveIndexRest(shape);
                indexActuator = SolveActuator(shape, held.FingerRest.position, new Vector3(35f, 0f, 45f), 16f);
                ApplyIndex(indexRest);
            }
            else
            {
                WrapFinger(Index, shape);
            }

            for (var i = 0; i < BoneCount; i++)
                holdPose[i] = bones[i].localRotation;

            cachedFor = held;
            cachedSurface = surfaceMesh;
            cachedRest = restInHand;
            if (cachedHold == null || cachedHold.Length != BoneCount)
                cachedHold = new Quaternion[BoneCount];
            System.Array.Copy(holdPose, cachedHold, BoneCount);
            cachedIndexRest = indexRest;
            cachedIndexActuator = indexActuator;
        }

        // Each joint of the finger flexes so every phalanx rests on the
        // surface: minimises the gaps between the phalanges and the object,
        // with penetration heavily penalised.
        void WrapFinger(int finger, Shape shape)
        {
            var angles = new Vector3(20f, 25f, 18f);
            var limits = fingerFlexion;
            angles = Descend(angles, new Vector3(-10f, 0f, 0f), limits, a => WrapCost(finger, a, shape, fingerRadius), 16f);
            SetFinger(finger, angles);
        }

        float WrapCost(int finger, Vector3 angles, Shape shape, float radius)
        {
            SetFinger(finger, angles);
            var cost = 0f;
            for (var s = 0; s < 3; s++)
            {
                var gap = SegmentClearance(finger, s, shape, radius);
                cost += gap > 0f ? gap * gap * 1e4f : gap * gap * 1e6f;
                var jet = SegmentJetGap(finger, s, shape, radius + jetClearance);
                if (jet < 0f)
                    cost += jet * jet * 1e6f;
            }

            // Natural synergy: the distal joint follows the middle one.
            var coupling = (angles.z - 0.7f * angles.y) / 90f;
            return cost + coupling * coupling * 0.05f;
        }

        /// <summary>Distance, meters, from `point` to the nearest finger's surface (phalanges as rendered now, fingerRadius thick); infinity before the bones are measured.</summary>
        public float FingerSurfaceDistance(Vector3 point)
        {
            if (!HasAnatomy)
                return float.PositiveInfinity;

            var best = float.PositiveInfinity;
            for (var f = 0; f < 5; f++)
            {
                for (var s = 0; s < 3; s++)
                {
                    var from = bones[f * 3 + s].position;
                    var to = s < 2 ? bones[f * 3 + s + 1].position : Tip(f).position;
                    var along = to - from;
                    var t = Mathf.Clamp01(Vector3.Dot(point - from, along) / Mathf.Max(along.sqrMagnitude, 1e-8f));
                    best = Mathf.Min(best, Vector3.Distance(point, from + along * t));
                }
            }

            return best - fingerRadius;
        }

        float SegmentClearance(int finger, int segment, Shape shape, float radius)
        {
            var from = bones[finger * 3 + segment].position;
            var to = segment < 2 ? bones[finger * 3 + segment + 1].position : Tip(finger).position;
            var best = float.PositiveInfinity;
            for (var k = 1; k <= 3; k++)
                best = Mathf.Min(best, shape.Clearance(Vector3.Lerp(from, to, k / 3f), radius));
            return best;
        }

        float SegmentJetGap(int finger, int segment, Shape shape, float radius)
        {
            if (shape.jet == null)
                return float.PositiveInfinity;

            var from = bones[finger * 3 + segment].position;
            var to = segment < 2 ? bones[finger * 3 + segment + 1].position : Tip(finger).position;
            var best = float.PositiveInfinity;
            for (var k = 0; k <= 3; k++)
                best = Mathf.Min(best, shape.JetGap(Vector3.Lerp(from, to, k / 3f), radius));
            return best;
        }

        void SetFinger(int finger, Vector3 angles)
        {
            for (var s = 0; s < 3; s++)
            {
                var i = finger * 3 + s;
                var baseRotation = finger == Thumb && s == 0 ? thumbOpposed : bindLocal[i];
                bones[i].localRotation = baseRotation * Quaternion.AngleAxis(angles[s], flexAxis[i]);
            }
        }

        // The thumb's metacarpal swings so the thumb runs tangentially around
        // the far side of the object - opposite to the way the fingers wrap
        // - with its flexion axis along the object's axis; then its joints
        // wrap the surface like the fingers do.
        void SolveThumb(Shape shape)
        {
            var baseBone = bones[0];
            var bindWorld = baseBone.rotation;
            var chord = (Tip(Thumb).position - baseBone.position).normalized;
            var flexWorld = bindWorld * flexAxis[0];

            var middleBase = bones[2 * 3].position;
            var middleRadial = Vector3.ProjectOnPlane(middleBase - shape.axisPoint, shape.axis);
            var middleDirection = bones[2 * 3 + 1].position - middleBase;
            var fingersSense = Mathf.Sign(Vector3.Dot(shape.axis, Vector3.Cross(middleRadial, middleDirection)));

            var radial = Vector3.ProjectOnPlane(baseBone.position - shape.axisPoint, shape.axis);
            var distance = radial.magnitude;
            var height = Vector3.Dot(baseBone.position - shape.axisPoint, shape.axis);
            var reach = ProfileRadiusNear(shape, height) + thumbRadius;
            var sense = -fingersSense;

            Vector3 around;
            if (distance > reach)
            {
                // Tangent from the thumb's base to the object, on the side
                // that wraps against the fingers.
                var inward = -radial / distance;
                var angle = Mathf.Asin(reach / distance) * Mathf.Rad2Deg;
                var a = Quaternion.AngleAxis(angle, shape.axis) * inward;
                var b = Quaternion.AngleAxis(-angle, shape.axis) * inward;
                around = Mathf.Sign(Vector3.Dot(shape.axis, Vector3.Cross(radial, a))) == sense ? a : b;
            }
            else
            {
                around = (Vector3.Cross(shape.axis, radial).normalized * sense).normalized;
            }

            var from = Quaternion.LookRotation(chord, Vector3.ProjectOnPlane(flexWorld, chord));
            var to = Quaternion.LookRotation(around, Vector3.ProjectOnPlane(shape.axis * sense, around));
            var opposedWorld = to * Quaternion.Inverse(from) * bindWorld;
            var swing = Quaternion.Angle(bindWorld, opposedWorld);
            if (swing > thumbOpposition)
                opposedWorld = Quaternion.Slerp(bindWorld, opposedWorld, thumbOpposition / swing);

            thumbOpposed = Quaternion.Inverse(baseBone.parent.rotation) * opposedWorld;

            var angles = Descend(new Vector3(0f, 15f, 15f), new Vector3(-15f, 0f, 0f), thumbFlexion, a => WrapCost(Thumb, a, shape, thumbRadius), 16f);
            SetFinger(Thumb, angles);
        }

        float ProfileRadiusNear(Shape shape, float height)
        {
            var radius = 0f;
            foreach (var section in shape.profile)
                if (height >= section.bottom - 0.02f && height <= section.top + 0.02f)
                    radius = Mathf.Max(radius, section.radius);
            if (radius > 0f)
                return radius;

            foreach (var section in shape.profile)
                radius = Mathf.Max(radius, section.radius);
            return radius;
        }

        // ---------- index: REST on the grip, ACTUATOR on the cap ----------

        Vector3 InHand(Vector3 world) => Quaternion.Inverse(transform.rotation) * (world - transform.position);

        Vector3 IndexMin => new Vector3(-10f, -indexAbduction, 0f);
        Vector3 IndexMax => new Vector3(fingerFlexion.x, indexAbduction, fingerFlexion.y);

        // REST: every phalanx close to the body (indexRestGap off it), none of
        // it near the cap or up in front of the nozzle (below the cap's seat
        // by indexRestBelowSeat) - the finger lies on the grip.
        Vector3 SolveIndexRest(Shape shape)
        {
            var body = shape.BodyOnly;
            var cap = shape.TopOnly;
            System.Func<Vector3, float> cost = p =>
            {
                ApplyIndex(p);
                var total = 0f;
                for (var s = 0; s < 3; s++)
                {
                    // Resting on the body - never in it (the hardest rule here).
                    var gap = SegmentClearance(Index, s, body, fingerRadius + indexRestGap);
                    total += gap > 0f ? gap * gap * 1e4f : gap * gap * 1e6f;
                    var inside = SegmentClearance(Index, s, body, fingerRadius);
                    if (inside < 0f)
                        total += inside * inside * 1e8f;

                    if (cap.top != null)
                    {
                        var capGap = SegmentClearance(Index, s, cap, fingerRadius + indexRestCapClearance);
                        if (capGap < 0f)
                            total += capGap * capGap * 1e6f;
                    }

                    var end = s < 2 ? bones[Index * 3 + s + 1].position : Tip(Index).position;
                    var above = Vector3.Dot(end - shape.axisPoint, shape.axis) - (shape.seat - indexRestBelowSeat);
                    if (above > 0f)
                        total += above * above * 1e7f;

                    var jet = SegmentJetGap(Index, s, shape, fingerRadius + jetClearance * 2f);
                    if (jet < 0f)
                        total += jet * jet * 1e7f;
                }

                var sideways = p.y / indexAbduction;
                return total + sideways * sideways * 0.05f;
            };

            // Sideways bend allowed both ways (the grip's obliquity makes a
            // finger curling in its own plane rise, and REST must stay low),
            // from several starts: the lowest resting pose is not always the
            // nearest one.
            var best = Descend(new Vector3(20f, 0f, 25f), IndexMin, IndexMax, cost, 16f);
            var bestCost = cost(best);
            foreach (var seed in RestSeeds)
            {
                var candidate = Descend(new Vector3(seed.x, seed.y * indexAbduction * TowardThumb, seed.z), IndexMin, IndexMax, cost, 16f);
                var c = cost(candidate);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = candidate;
                }
            }

            return best;
        }

        // Sign of an index abduction toward the thumb (up a held can): the
        // abduction axis is the palm normal, mirrored between the hands. The
        // seeds below are written for the right hand.
        float TowardThumb => isLeftHand ? -1f : 1f;

        static readonly Vector3[] RestSeeds =
        {
            new Vector3(0f, -1f, 20f),
            new Vector3(-5f, -1f, 50f),
        };

        // ACTUATOR: the distal pad on the pressed part's real top surface -
        // touching, not in it - as close to the rest point (the cap's finger
        // scoop) as the finger reaches, with no phalanx in the can or cap.
        // Several starting poses: the solve runs once per object, and the
        // finger's reachable set around a small cap is not convex.
        Vector3 SolveActuator(Shape shape, Vector3 rest, Vector3 start, float firstStep)
        {
            System.Func<Vector3, float> cost = p => ActuatorCost(p, shape, rest);
            var best = Descend(start, IndexMin, IndexMax, cost, firstStep);
            if (firstStep < 16f)
                return best;

            var bestCost = cost(best);
            foreach (var seed in ActuatorSeeds)
            {
                var candidate = Descend(new Vector3(seed.x, seed.y * indexAbduction * TowardThumb, seed.z), IndexMin, IndexMax, cost, firstStep);
                var c = cost(candidate);
                if (c < bestCost)
                {
                    bestCost = c;
                    best = candidate;
                }
            }

            return best;
        }

        static readonly Vector3[] ActuatorSeeds =
        {
            new Vector3(15f, 1f, 55f),
            new Vector3(25f, 1f, 35f),
            new Vector3(50f, 0.4f, 30f),
            new Vector3(10f, 0.6f, 75f),
            new Vector3(40f, -0.4f, 50f),
        };

        float ActuatorCost(Vector3 p, Shape shape, Vector3 rest)
        {
            ApplyIndex(p);
            var penetration = 0f;
            for (var s = 0; s < 3; s++)
            {
                var gap = SegmentClearance(Index, s, shape, fingerRadius);
                if (gap < 0f)
                    penetration += gap * gap;
                var jet = SegmentJetGap(Index, s, shape, fingerRadius);
                if (jet < 0f)
                    penetration += jet * jet;
            }

            // The fingertip's pad is slimmer than the phalanges that wrap the
            // can: its own radius, so it lands on the part instead of hovering.
            var pad = Vector3.Lerp(bones[Index * 3 + 2].position, Tip(Index).position, 0.6f);
            var padRadius = fingerRadius * FingertipPadScale;
            float contact;
            Vector3 toward;
            if (shape.top != null)
            {
                var gap = shape.TopGap(pad, padRadius);
                contact = gap > 0f ? gap * gap * 1e6f : gap * gap * 1e7f;

                // As far over the part's top as the finger reaches: toward its
                // axis (the scoop itself is behind the cap, out of reach from
                // the side of the can).
                var axisPoint = shape.topSpace.position;
                toward = Vector3.ProjectOnPlane(pad - axisPoint, shape.topSpace.up);
            }
            else
            {
                contact = (Tip(Index).position - (rest + shape.axis * fingerRadius)).sqrMagnitude * 1e6f;
                toward = Vector3.ProjectOnPlane(pad - rest, shape.axis);
            }

            var towardCost = toward.sqrMagnitude * 5e3f;

            // Pressing, not propped against it: the last phalanx comes down
            // onto the part, arched over its edge, rather than standing up
            // beside it.
            var distal = (Tip(Index).position - bones[Index * 3 + 2].position).normalized;
            var upward = Mathf.Max(0f, Vector3.Dot(distal, shape.axis) + 0.3f);

            var sideways = p.y / indexAbduction;
            return penetration * 1e7f + contact + towardCost + upward * upward * 2f + sideways * sideways * 0.02f;
        }

        float IndexPenetration(Shape shape)
        {
            var worst = 0f;
            for (var s = 0; s < 3; s++)
                worst = Mathf.Max(worst, -SegmentClearance(Index, s, shape, fingerRadius));
            return worst;
        }

        // Every frame while holding: the trigger moves the index, in
        // proportion, from REST to ACTUATOR - smoothed, and lifted clear of
        // whatever it would pass through on the way. The nozzle press moves
        // the cap (and FingerRest) down: ACTUATOR is refined to follow it.
        void FollowFingerRest(VRGrabInteractable held)
        {
            // An inspection is turning the can in the fist: the index comes
            // off the actuator and waits, lifted, rather than chasing (and
            // re-solving against) a cap that is going round under it.
            inspectionLift = Mathf.MoveTowards(inspectionLift, held.Presenting ? 1f : 0f, Time.deltaTime / 0.15f);
            if (held.Presenting)
            {
                LiftIndex();
                return;
            }

            FollowFingerRestNormally(held);
            if (inspectionLift > 0f)
                LiftIndex();
        }

        float inspectionLift;

        void LiftIndex()
        {
            for (var s = 0; s < 3; s++)
                target[Index * 3 + s] = Quaternion.Slerp(holdPose[Index * 3 + s], OpenPose(Index * 3 + s), inspectionLift * inspectionIndexLift);
        }

        void FollowFingerRestNormally(VRGrabInteractable held)
        {
            var raw = Mathf.Clamp01(held.FingerApproach);
            smoothedApproach = Mathf.Abs(raw - smoothedApproach) < 0.002f
                ? raw
                : Mathf.Lerp(smoothedApproach, raw, 1f - Mathf.Exp(-indexFollowSpeed * Time.deltaTime));

            var rest = held.FingerRest.position;
            var restInHand = InHand(rest);
            var restMoved = (restInHand - solvedRest).sqrMagnitude > 1e-8f;
            if (restMoved || Mathf.Abs(smoothedApproach - solvedApproach) > 0.002f)
            {
                for (var s = 0; s < 3; s++)
                    savedIndex[s] = bones[Index * 3 + s].localRotation;

                var shape = ShapeOf(held);
                if (restMoved)
                    indexActuator = SolveActuator(shape, rest, indexActuator, 2f);

                var e = smoothedApproach * smoothedApproach * (3f - 2f * smoothedApproach);
                var p = Vector3.Lerp(indexRest, indexActuator, e);
                ApplyIndex(p);

                // Straight interpolation between two clear poses can still cut
                // the cap's edge: open the finger until it passes over it.
                for (var k = 0; k < 8 && e > 0f && e < 1f && IndexPenetration(shape) > 0.0005f; k++)
                {
                    p.x = Mathf.Max(IndexMin.x, p.x - 4f);
                    p.z = Mathf.Max(0f, p.z - 3f);
                    ApplyIndex(p);
                }

                for (var s = 0; s < 3; s++)
                {
                    holdPose[Index * 3 + s] = bones[Index * 3 + s].localRotation;
                    bones[Index * 3 + s].localRotation = savedIndex[s];
                }

                solvedRest = restInHand;
                solvedApproach = smoothedApproach;
            }

            for (var s = 0; s < 3; s++)
                target[Index * 3 + s] = holdPose[Index * 3 + s];
        }

        void ApplyIndex(Vector3 p)
        {
            var i = Index * 3;
            bones[i].localRotation = bindLocal[i] * Quaternion.AngleAxis(p.y, indexAbductionAxis) * Quaternion.AngleAxis(p.x, flexAxis[i]);
            bones[i + 1].localRotation = bindLocal[i + 1] * Quaternion.AngleAxis(p.z, flexAxis[i + 1]);
            bones[i + 2].localRotation = bindLocal[i + 2] * Quaternion.AngleAxis(p.z * 0.7f, flexAxis[i + 2]);
        }

        /// <summary>
        /// Where along its grip axis the fist holds an object, relative to
        /// the grip anchor placed on the palm - solved on the object as it
        /// would be once attached to <paramref name="anchor"/>. Positive: the
        /// object moves toward the thumb side (the hand sits lower on it).
        ///
        /// The fist holds the body: the hand sits as high as it can while
        /// its upper wrapping finger (the middle) reaches at most
        /// fistShoulderAllowance above where the body ends - onto the
        /// shoulder, never the valve cup or the cap, and out of the spray. As
        /// high as that, so the index has the best reach over the top. Cached per
        /// object. Zero for objects without a FingerRest.
        /// </summary>
        public float GripSlide(VRGrabInteractable held, Transform anchor)
        {
            if (!HasAnatomy || held == null || held.FingerRest == null || held.GrabPoint == null)
                return 0f;

            if (slideCacheFor == held)
                return slideCache;

            var profile = held.GripProfile;
            if (profile == null || profile.Length == 0)
                return 0f;

            // The body: the widest section; it ends where the shoulder starts.
            var bodyTop = profile[0].top;
            var widest = profile[0].radius;
            foreach (var section in profile)
            {
                if (section.radius > widest)
                {
                    widest = section.radius;
                    bodyTop = section.top;
                }
            }

            var saved = new Quaternion[3];
            for (var s = 0; s < 3; s++)
                saved[s] = bones[Middle * 3 + s].localRotation;

            var axis = anchor.forward;
            var slide = 0f;
            for (var iteration = 0; iteration < 5; iteration++)
            {
                var shape = new Shape { profile = profile, axisPoint = anchor.position + axis * slide, axis = axis };
                WrapFinger(Middle, shape);
                var highest = float.NegativeInfinity;
                for (var s = 0; s < 3; s++)
                    highest = Mathf.Max(highest, Vector3.Dot(bones[Middle * 3 + s].position - shape.axisPoint, axis));
                highest = Mathf.Max(highest, Vector3.Dot(Tip(Middle).position - shape.axisPoint, axis));

                var excess = highest - (bodyTop + fistShoulderAllowance);
                slide = Mathf.Clamp(slide + excess, -0.04f, 0.08f);
                if (Mathf.Abs(excess) < 0.0005f)
                    break;
            }

            for (var s = 0; s < 3; s++)
                bones[Middle * 3 + s].localRotation = saved[s];

            slideCacheFor = held;
            slideCache = slide;
            return slideCache;
        }

        VRGrabInteractable slideCacheFor;
        float slideCache;

        // ---------- pinch (caps) ----------

        // Thumb and index close on the part from opposite sides, across the
        // line where the relaxed hand's thumb and index tips meet; the other
        // fingers curl out of the way. settle: the part is in the hand and
        // comes the last centimetres into those fingers. Otherwise (a cap
        // still seated, being pulled) the fingers go to where the part is.
        void SolvePinch(VRGrabInteractable held, bool settle)
        {
            pinchAttached = held.AttachedToHand;

            thumbOpposed = thumbBindBase;
            for (var i = 0; i < BoneCount; i++)
                bones[i].localRotation = RelaxedPose(i);

            var thumbTip = Tip(Thumb).position;
            var indexTip = Tip(Index).position;
            var across = (indexTip - thumbTip).normalized;
            var meet = (thumbTip + indexTip) * 0.5f;

            var collider = held.GetComponentInChildren<Collider>();
            var center = PinchedPartCenter(held);
            var half = collider != null ? HalfWidth(collider, across) : 0.008f;

            if (settle && held.transform.parent != null)
            {
                var pivot = meet + (held.transform.position - center);
                held.SettleInHand(held.transform.parent.InverseTransformPoint(pivot), pinchSettleSeconds);
                center = meet;
            }

            pinchedAt = InHand(center);
            var indexGoal = center + across * (half + fingerRadius * 0.75f);
            var thumbGoal = center - across * (half + thumbRadius * 0.75f);

            var indexAngles = Descend(new Vector3(25f, 0f, 30f), IndexMin, IndexMax, p =>
            {
                ApplyIndex(p);
                var sideways = p.y / indexAbduction;
                return (Tip(Index).position - indexGoal).sqrMagnitude * 1e4f + sideways * sideways * 0.01f;
            }, 16f);

            var thumbAngles = SolveThumbTo(thumbGoal);

            ApplyIndex(indexAngles);
            SetFinger(Thumb, thumbAngles);
            for (var i = Middle * 3; i < BoneCount; i++)
                bones[i].localRotation = Flexed(i, pinchCurl);

            for (var i = 0; i < BoneCount; i++)
                pinchPose[i] = bones[i].localRotation;
        }

        // The thumb into opposition toward a point: its metacarpal swings so
        // the thumb points at the goal (within thumbOpposition of its rest),
        // then its joints flex to reach it - a few rounds, since flexing
        // changes where the swung thumb points.
        Vector3 SolveThumbTo(Vector3 goal)
        {
            var baseBone = bones[0];
            var bindWorld = baseBone.parent.rotation * thumbBindBase;
            var flexWorld = bindWorld * flexAxis[0];
            var angles = new Vector3(10f, 15f, 15f);
            System.Func<Vector3, float> reach = a =>
            {
                SetFinger(Thumb, a);
                return (Tip(Thumb).position - goal).sqrMagnitude * 1e4f;
            };

            thumbOpposed = thumbBindBase;
            for (var round = 0; round < 3; round++)
            {
                SetFinger(Thumb, angles);
                var pointing = (Tip(Thumb).position - baseBone.position).normalized;
                var wanted = (goal - baseBone.position).normalized;
                var current = baseBone.parent.rotation * thumbOpposed;
                var swung = Quaternion.FromToRotation(pointing, wanted) * current;
                var swing = Quaternion.Angle(bindWorld, swung);
                if (swing > thumbOpposition)
                    swung = Quaternion.Slerp(bindWorld, swung, thumbOpposition / swing);

                // Keep the thumb's own flexion axis from rolling: only the
                // swing toward the goal, not a twist about the thumb.
                var rolled = swung * Quaternion.Inverse(bindWorld) * flexWorld;
                var twistFix = Quaternion.FromToRotation(rolled, Vector3.ProjectOnPlane(flexWorld, wanted).normalized);
                if (Quaternion.Angle(Quaternion.identity, twistFix) < 60f)
                    swung = twistFix * swung;

                thumbOpposed = Quaternion.Inverse(baseBone.parent.rotation) * swung;
                angles = Descend(angles, new Vector3(-15f, 0f, 0f), thumbFlexion, reach, round == 0 ? 16f : 4f);
            }

            return angles;
        }

        static Vector3 PinchedPartCenter(VRGrabInteractable held)
        {
            var collider = held.GetComponentInChildren<Collider>();
            if (collider is BoxCollider box)
                return box.transform.TransformPoint(box.center);
            return collider != null ? collider.bounds.center : held.transform.position;
        }

        // Half the part's width across `direction` (a box collider's support).
        static float HalfWidth(Collider collider, Vector3 direction)
        {
            if (collider is BoxCollider box)
            {
                var t = box.transform;
                var e = Vector3.Scale(box.size * 0.5f, t.lossyScale);
                return Mathf.Abs(Vector3.Dot(direction, t.right)) * Mathf.Abs(e.x)
                    + Mathf.Abs(Vector3.Dot(direction, t.up)) * Mathf.Abs(e.y)
                    + Mathf.Abs(Vector3.Dot(direction, t.forward)) * Mathf.Abs(e.z);
            }

            var extents = collider.bounds.extents;
            return Mathf.Abs(direction.x) * extents.x + Mathf.Abs(direction.y) * extents.y + Mathf.Abs(direction.z) * extents.z;
        }

        // ---------- small deterministic optimiser ----------

        // Coordinate descent over three joint parameters within [min, max],
        // with step halving from firstStep (16 from scratch, 2 to refine a
        // previous solution).
        static Vector3 Descend(Vector3 start, Vector3 min, Vector3 max, System.Func<Vector3, float> cost, float firstStep)
        {
            var p = Vector3.Max(min, Vector3.Min(max, start));
            var best = cost(p);
            for (var step = firstStep; step >= 0.5f; step *= 0.5f)
            {
                var improved = true;
                for (var guard = 0; improved && guard < 12; guard++)
                {
                    improved = false;
                    for (var axis = 0; axis < 3; axis++)
                    {
                        for (var sign = 1f; sign >= -1f; sign -= 2f)
                        {
                            var candidate = p;
                            candidate[axis] = Mathf.Clamp(candidate[axis] + sign * step, min[axis], max[axis]);
                            if (candidate[axis] == p[axis])
                                continue;

                            var c = cost(candidate);
                            if (c < best)
                            {
                                best = c;
                                p = candidate;
                                improved = true;
                            }
                        }
                    }
                }
            }

            return p;
        }

        Transform Tip(int finger) => tips != null && finger < tips.Length ? tips[finger] : null;
    }
}
