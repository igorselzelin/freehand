using UnityEngine;

namespace Freehand.VR
{
    /// <summary>
    /// Drives the shared fps-hands.fbx rig (both arms in one
    /// SkinnedMeshRenderer) from the head and the two controllers.
    ///
    /// Why arms used to look like stretched tentacles: the FBX has two bone
    /// chains per arm, and the one this used to drive (forearm.X.001 ->
    /// hand) carries NO vertex weights - the arm mesh is skinned to
    /// upper_arm.X / forearm.X (260 / 320 vertices). Those were never moved,
    /// so they stayed in bind pose at the rig's origin (the XR Origin, on the
    /// floor) while the hand was pulled to the controller, stretching the
    /// skin between them. In the bind pose the two chains meet exactly at the
    /// wrist (forearm.X_end == hand.X), so they are one arm:
    /// shoulder (upper_arm) -> elbow (forearm) -> wrist (hand).
    ///
    /// Every frame:
    ///  1. Torso: the rig root is placed so its bind-pose shoulders sit a
    ///     fixed distance below/behind the eyes, turned with the head's yaw
    ///     only once the head turns past a dead zone (looking at your hand
    ///     should not swing both shoulders).
    ///  2. Hand: the hand bone is placed so its anatomical grip frame
    ///     (HandAnatomy, measured from the bones) coincides with the
    ///     controller's grip pose - no guessed rotation offset.
    ///  3. Arm: analytic two-bone IK on upper_arm/forearm with their real
    ///     bind lengths. The one free parameter - how far the elbow swings
    ///     around the shoulder-wrist line - is chosen every frame to keep
    ///     the wrist inside its natural range for the hand's current
    ///     orientation (flexion/extension, radial/ulnar deviation, twist),
    ///     with a mild preference for a down/out/back elbow and for staying
    ///     where it was. A fixed elbow pole ignored the hand: the wrist ended
    ///     up bent 45-75 degrees sideways, and the skin, which blends
    ///     forearm to hand over only ~3cm, folded into a visible crease.
    ///     If the wrist is beyond reach the shoulder slides toward it rather
    ///     than any bone stretching. Half the wrist's remaining roll about the
    ///     forearm is given to the forearm so the wrist skin does not twist flat.
    ///
    /// Runs before other LateUpdates (finger poses read the placed hand;
    /// held objects are children of the hand's grip anchor and follow it).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class HandRig : MonoBehaviour
    {
        [System.Serializable]
        public class Arm
        {
            public bool isLeft;
            public Transform controller;
            public VRGrabber grabber;
            public HandBonePoseController pose;
            public Transform upperArm;
            public Transform forearm;
            public Transform forearmEnd;
            public Transform hand;

            [Tooltip("Calibration only: extra rotation between the controller's grip pose and the hand's anatomical grip frame. Zero = OpenXR's grip definition taken literally.")]
            public Vector3 gripRotationOffsetEuler;

            [Tooltip("Calibration only: extra offset, meters, in the controller's grip frame.")]
            public Vector3 gripPositionOffset;

            [System.NonSerialized] public bool ready;
            [System.NonSerialized] public Vector3 shoulderRel;
            [System.NonSerialized] public Quaternion upperBindRel;
            [System.NonSerialized] public Quaternion foreBindRel;
            [System.NonSerialized] public Vector3 upperDirRel;
            [System.NonSerialized] public Vector3 foreDirRel;
            [System.NonSerialized] public Vector3 elbowOffsetRel;
            [System.NonSerialized] public float upperLength;
            [System.NonSerialized] public float foreLength;
            [System.NonSerialized] public Quaternion handInForearm;
            [System.NonSerialized] public Vector3 gripCenterInHand;
            [System.NonSerialized] public Quaternion gripRotationInHand;
            [System.NonSerialized] public Transform gripAnchor;
            [System.NonSerialized] public float swivel = float.NaN;
            [System.NonSerialized] public Quaternion flourish = Quaternion.identity;
        }

        [SerializeField] Transform head;
        [SerializeField] Arm left = new Arm { isLeft = true };
        [SerializeField] Arm right = new Arm();

        [Header("Body estimate, from the eyes (meters)")]
        [SerializeField] float shoulderDropBelowEyes = 0.24f;
        [SerializeField] float shoulderBehindEyes = 0.06f;

        [Tooltip("The head can turn this far before the shoulders turn with it.")]
        [SerializeField] float torsoDeadZoneDegrees = 35f;

        [Tooltip("How fast the shoulders drift back under the head inside the dead zone.")]
        [SerializeField] float torsoFollowSpeed = 1.5f;

        [Header("Controller")]
        [Tooltip("Radius of the controller handle the real hand closes around. The controller's grip position is the centre of that tube.")]
        [SerializeField] float controllerHandleRadius = 0.017f;

        [Range(0f, 1f)]
        [SerializeField] float forearmTwistShare = 0.5f;

        [Header("Wrist range used to place the elbow (degrees)")]
        [SerializeField] float wristFlexionRange = 65f;
        [SerializeField] float wristDeviationRange = 25f;
        [SerializeField] float wristTwistRange = 80f;

        [Tooltip("How fast the elbow swings to a newly chosen position (1/s).")]
        [SerializeField] float swivelFollowSpeed = 12f;

        Vector3 shoulderMidRel;
        float torsoYaw;
        bool torsoInitialized;

        /// <summary>
        /// V0.14: a small extra turn of the rendered hand about its own grip (grip-frame space: X across the palm,
        /// Y along the fingers, Z through the fist) - a flick of the wrist during a freestyle move. The grip itself
        /// stays on the controller, so what the hand holds turns with it; limited to `maxFlourishDegrees`.
        /// </summary>
        public void SetFlourish(bool isLeft, Quaternion rotation)
        {
            var arm = isLeft ? left : right;
            rotation.ToAngleAxis(out var degrees, out var axis);
            degrees = Mathf.DeltaAngle(0f, degrees);
            arm.flourish = float.IsNaN(axis.x) || Mathf.Abs(degrees) < 0.01f
                ? Quaternion.identity
                : Quaternion.AngleAxis(Mathf.Clamp(degrees, -maxFlourishDegrees, maxFlourishDegrees), axis);
        }

        [Tooltip("V0.14: the largest wrist flourish a freestyle move may add to the rendered hand.")]
        [SerializeField] float maxFlourishDegrees = 30f;

        /// <summary>True once this arm's bones and hand anatomy were measured and it is being driven.</summary>
        public bool IsReady(bool isLeft) => (isLeft ? left : right).ready;

        /// <summary>Between this hand's thumb and index fingertips, as rendered now; null when the arm is not driven.</summary>
        public Vector3? PinchPoint(bool isLeft)
        {
            var arm = isLeft ? left : right;
            return arm.ready ? arm.pose.PinchPoint : (Vector3?)null;
        }

        /// <summary>This hand's thumb and index fingertips as rendered now; false while the arm is not driven.</summary>
        public bool TryGetFingertips(bool isLeft, out Vector3 thumb, out Vector3 index)
        {
            var arm = isLeft ? left : right;
            if (!arm.ready || arm.pose == null || !arm.pose.HasAnatomy)
            {
                thumb = index = Vector3.zero;
                return false;
            }

            thumb = arm.pose.ThumbTip;
            index = arm.pose.IndexTip;
            return true;
        }

        /// <summary>The middle of this hand's palm surface and the direction the palm faces, as rendered now (things floating over the palm - the color orb). False while the arm is not driven.</summary>
        public bool TryGetPalm(bool isLeft, out Vector3 center, out Vector3 facing)
        {
            var arm = isLeft ? left : right;
            if (!arm.ready || arm.pose == null || !arm.pose.HasAnatomy)
            {
                center = facing = Vector3.zero;
                return false;
            }

            var anatomy = arm.pose.Anatomy;
            facing = arm.hand.rotation * anatomy.PalmFacing;
            center = arm.hand.position + arm.hand.rotation * anatomy.PalmReference + facing * anatomy.PalmHalfThickness;
            return true;
        }

        /// <summary>Distance, meters, from `point` to the surface of this hand's nearest finger, as rendered now; infinity while the arm is not driven.</summary>
        public float FingerClearance(bool isLeft, Vector3 point)
        {
            var arm = isLeft ? left : right;
            return arm.ready && arm.pose != null ? arm.pose.FingerSurfaceDistance(point) : float.PositiveInfinity;
        }

        void Start()
        {
            // Without the head the shoulders stay at the rig's origin (the
            // floor) and every arm stretches up to its controller.
            if (head == null)
            {
                Debug.LogError($"{nameof(HandRig)}: no head (XR camera) assigned - run the scene builder. Arm IK disabled.", this);
                return;
            }

            Measure(left);
            Measure(right);
            shoulderMidRel = (left.shoulderRel + right.shoulderRel) * 0.5f;
        }

        // Bind pose, expressed relative to this root (rotation frame, world
        // meters) so it stays valid while the root follows the head.
        void Measure(Arm arm)
        {
            if (arm.upperArm == null || arm.forearm == null || arm.forearmEnd == null || arm.hand == null || arm.pose == null || !arm.pose.HasAnatomy)
            {
                Debug.LogError($"{nameof(HandRig)}: {(arm.isLeft ? "left" : "right")} arm is missing bones or hand anatomy - arm IK disabled for it.", this);
                return;
            }

            var inverse = Quaternion.Inverse(transform.rotation);
            var shoulder = arm.upperArm.position;
            var elbow = arm.forearm.position;
            var wrist = arm.forearmEnd.position;

            arm.shoulderRel = inverse * (shoulder - transform.position);
            arm.upperBindRel = inverse * arm.upperArm.rotation;
            arm.foreBindRel = inverse * arm.forearm.rotation;
            arm.upperLength = Vector3.Distance(shoulder, elbow);
            arm.foreLength = Vector3.Distance(elbow, wrist);
            arm.upperDirRel = inverse * (elbow - shoulder).normalized;
            arm.foreDirRel = inverse * (wrist - elbow).normalized;
            arm.elbowOffsetRel = inverse * Vector3.ProjectOnPlane(elbow - shoulder, (wrist - shoulder).normalized).normalized;
            arm.handInForearm = Quaternion.Inverse(arm.forearm.rotation) * arm.hand.rotation;

            var anatomy = arm.pose.Anatomy;
            arm.gripCenterInHand = anatomy.GripCenter(controllerHandleRadius);
            arm.gripRotationInHand = anatomy.GripRotation;

            var anchor = new GameObject("GripAnchor").transform;
            anchor.SetParent(arm.hand, false);
            anchor.localScale = Vector3.one / Mathf.Max(0.0001f, arm.hand.lossyScale.x);
            arm.gripAnchor = anchor;
            PlaceAnchor(arm, 0f);

            arm.ready = true;
        }

        /// <summary>
        /// The transform a VRGrabber attaches <paramref name="held"/> to: the
        /// hand's grip frame, on the axis of the object's cylinder resting on
        /// the palm (GripRadius; 0 = the controller handle's own axis), slid
        /// along that axis to where this hand's index finger reaches the
        /// object's FingerRest. Held objects follow the rendered hand exactly,
        /// never a different pose.
        /// </summary>
        public Transform PrepareGripAnchor(bool isLeft, VRGrabInteractable held)
        {
            var arm = isLeft ? left : right;
            if (!arm.ready || held == null)
                return null;

            PlaceAnchor(arm, held.GripRadius);
            if (held.GripRadius > 0f)
                arm.gripAnchor.position += arm.gripAnchor.forward * arm.pose.GripSlide(held, arm.gripAnchor);
            return arm.gripAnchor;
        }

        void PlaceAnchor(Arm arm, float objectRadius)
        {
            var anatomy = arm.pose.Anatomy;
            var radius = objectRadius > 0f ? objectRadius : controllerHandleRadius;
            arm.gripAnchor.SetPositionAndRotation(
                arm.hand.position + arm.hand.rotation * anatomy.GripCenter(radius),
                arm.hand.rotation * anatomy.GripRotation);
        }

        void LateUpdate()
        {
            PlaceTorso();
            Solve(left);
            Solve(right);
        }

        void PlaceTorso()
        {
            if (head == null)
                return;

            var headForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (headForward.sqrMagnitude > 0.0001f)
            {
                var headYaw = Mathf.Atan2(headForward.x, headForward.z) * Mathf.Rad2Deg;
                if (!torsoInitialized)
                {
                    torsoYaw = headYaw;
                    torsoInitialized = true;
                }

                var delta = Mathf.DeltaAngle(torsoYaw, headYaw);
                var outside = Mathf.Abs(delta) - torsoDeadZoneDegrees;
                if (outside > 0f)
                    torsoYaw += Mathf.Sign(delta) * outside;

                torsoYaw = Mathf.LerpAngle(torsoYaw, headYaw, 1f - Mathf.Exp(-torsoFollowSpeed * Time.deltaTime));
            }

            var torso = Quaternion.Euler(0f, torsoYaw, 0f);
            var shoulderCenter = head.position + Vector3.down * shoulderDropBelowEyes - torso * Vector3.forward * shoulderBehindEyes;
            transform.SetPositionAndRotation(shoulderCenter - torso * shoulderMidRel, torso);
        }

        void Solve(Arm arm)
        {
            if (!arm.ready || arm.controller == null)
                return;

            // Hand: its grip frame onto the controller's grip pose.
            var gripRotation = arm.controller.rotation * Quaternion.Euler(arm.gripRotationOffsetEuler) * arm.flourish;
            var gripPosition = arm.controller.position + gripRotation * arm.gripPositionOffset;
            var handRotation = gripRotation * Quaternion.Inverse(arm.gripRotationInHand);
            var wrist = gripPosition - handRotation * arm.gripCenterInHand;

            SolveArm(arm, wrist, handRotation);
            arm.hand.SetPositionAndRotation(wrist, handRotation);
        }

        void SolveArm(Arm arm, Vector3 wrist, Quaternion handRotation)
        {
            var root = transform.rotation;
            var a = arm.upperLength;
            var b = arm.foreLength;

            var shoulder = transform.position + root * arm.shoulderRel;
            var toWrist = wrist - shoulder;
            var distance = toWrist.magnitude;
            if (distance < 0.0001f)
                return;

            var direction = toWrist / distance;

            // Out of reach: the shoulder follows the hand instead of the arm
            // stretching. Too close: the elbow folds as far as it can.
            var reach = a + b - 0.001f;
            if (distance > reach)
            {
                shoulder = wrist - direction * reach;
                distance = reach;
            }

            distance = Mathf.Max(distance, Mathf.Abs(a - b) + 0.001f);
            arm.upperArm.position = shoulder;

            // The elbow circle around the shoulder-wrist line, measured from
            // a pole that is down, a little out and a little back in torso space.
            var side = arm.isLeft ? -1f : 1f;
            var pole = root * new Vector3(0.35f * side, -1f, -0.35f);
            var u = Vector3.ProjectOnPlane(pole, direction);
            if (u.sqrMagnitude < 0.04f)
                u = Vector3.ProjectOnPlane(root * new Vector3(side, 0f, -1f), direction);
            u.Normalize();
            var v = Vector3.Cross(direction, u);

            var cosShoulder = Mathf.Clamp((a * a + distance * distance - b * b) / (2f * a * distance), -1f, 1f);
            var sinShoulder = Mathf.Sqrt(1f - cosShoulder * cosShoulder);
            var geometry = new ElbowGeometry
            {
                arm = arm, root = root, shoulder = shoulder, direction = direction, distance = distance,
                a = a, cosShoulder = cosShoulder, sinShoulder = sinShoulder, u = u, v = v, handRotation = handRotation,
            };

            // Coarse sweep, then refine around the best - deterministic, and
            // continuous frame to frame through the "stay where it was" term.
            var best = 0f;
            var bestCost = float.MaxValue;
            for (var angle = -180f; angle < 180f; angle += 15f)
            {
                var cost = SwivelCost(geometry, angle);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = angle;
                }
            }

            for (var step = 7.5f; step >= 0.5f; step *= 0.5f)
            {
                foreach (var candidate in new[] { best - step, best + step })
                {
                    var cost = SwivelCost(geometry, candidate);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = candidate;
                    }
                }
            }

            // Eased toward the new best, so a switch between two elbow
            // solutions is a short swing, never a one-frame snap.
            if (!float.IsNaN(arm.swivel))
                best = Mathf.LerpAngle(arm.swivel, best, 1f - Mathf.Exp(-swivelFollowSpeed * Time.deltaTime));
            arm.swivel = Mathf.DeltaAngle(0f, best);
            var bendDirection = geometry.Bend(best);
            var elbow = geometry.Elbow(bendDirection);
            var upperDir = (elbow - shoulder).normalized;
            var foreDir = (shoulder + direction * distance - elbow).normalized;

            var bindOffset = root * arm.elbowOffsetRel;
            var upperRotation = FrameRotation(root * arm.upperDirRel, bindOffset, upperDir, bendDirection) * (root * arm.upperBindRel);
            var foreRotation = ForearmRotation(geometry, bendDirection, foreDir);

            // Share the wrist's remaining roll about the forearm with the forearm.
            var expectedHand = foreRotation * arm.handInForearm;
            var twist = TwistDegrees(handRotation * Quaternion.Inverse(expectedHand), foreDir);
            foreRotation = Quaternion.AngleAxis(twist * forearmTwistShare, foreDir) * foreRotation;

            arm.upperArm.rotation = upperRotation;
            arm.forearm.rotation = foreRotation;
        }

        struct ElbowGeometry
        {
            public Arm arm;
            public Quaternion root;
            public Vector3 shoulder;
            public Vector3 direction;
            public float distance;
            public float a;
            public float cosShoulder;
            public float sinShoulder;
            public Vector3 u;
            public Vector3 v;
            public Quaternion handRotation;

            public Vector3 Bend(float degrees)
            {
                var r = degrees * Mathf.Deg2Rad;
                return u * Mathf.Cos(r) + v * Mathf.Sin(r);
            }

            public Vector3 Elbow(Vector3 bend) => shoulder + direction * (a * cosShoulder) + bend * (a * sinShoulder);
        }

        // Map the forearm's bind (direction, elbow side) frame onto the solved
        // one - keeps the bind twist instead of an arbitrary roll.
        static Quaternion ForearmRotation(ElbowGeometry g, Vector3 bend, Vector3 foreDir) =>
            FrameRotation(g.root * g.arm.foreDirRel, g.root * g.arm.elbowOffsetRel, foreDir, bend) * (g.root * g.arm.foreBindRel);

        // How unnatural the wrist would be with the elbow swung this far,
        // measured on the hand's own anatomical axes: flexion/extension about
        // the knuckle axis, deviation about the palm normal, and the part of
        // the twist the forearm does not take. Plus a mild pull toward the
        // down/out/back pole and toward last frame's swivel.
        float SwivelCost(ElbowGeometry g, float degrees)
        {
            var bend = g.Bend(degrees);
            var elbow = g.Elbow(bend);
            var foreDir = (g.shoulder + g.direction * g.distance - elbow).normalized;
            var foreRotation = ForearmRotation(g, bend, foreDir);
            var delta = g.handRotation * Quaternion.Inverse(foreRotation * g.arm.handInForearm);

            var twist = TwistDegrees(delta, foreDir);
            var swing = Quaternion.AngleAxis(-twist, foreDir) * delta;
            swing.ToAngleAxis(out var swingAngle, out var swingAxis);
            swingAngle = Mathf.DeltaAngle(0f, swingAngle);

            var anatomy = g.arm.pose.Anatomy;
            var flexion = swingAngle * Vector3.Dot(swingAxis, g.handRotation * anatomy.KnuckleAxis) / wristFlexionRange;
            var deviation = swingAngle * Vector3.Dot(swingAxis, g.handRotation * anatomy.PalmFacing) / wristDeviationRange;
            var remainingTwist = twist * (1f - forearmTwistShare) / wristTwistRange;
            var fromPole = Mathf.DeltaAngle(0f, degrees) / 90f;

            // Elbows above the shoulder-wrist line are only natural when
            // nothing else works.
            var elbowUp = Mathf.Max(0f, Vector3.Dot(bend, Vector3.up));

            // A small, capped preference for last frame's swivel: breaks ties
            // between near-equal solutions without holding the elbow in a
            // worse one after a big move.
            var fromLast = float.IsNaN(g.arm.swivel) ? 0f : Mathf.Min(1f, Mathf.Abs(Mathf.DeltaAngle(g.arm.swivel, degrees)) / 45f);

            return flexion * flexion + deviation * deviation + remainingTwist * remainingTwist
                + 0.15f * fromPole * fromPole + elbowUp * elbowUp + 0.08f * fromLast * fromLast;
        }

        static Quaternion FrameRotation(Vector3 fromDirection, Vector3 fromSide, Vector3 toDirection, Vector3 toSide)
        {
            var from = Quaternion.LookRotation(fromDirection, Vector3.ProjectOnPlane(fromSide, fromDirection));
            var to = Quaternion.LookRotation(toDirection, Vector3.ProjectOnPlane(toSide, toDirection));
            return to * Quaternion.Inverse(from);
        }

        // Swing-twist decomposition: the part of `rotation` about `axis`.
        static float TwistDegrees(Quaternion rotation, Vector3 axis)
        {
            var vector = new Vector3(rotation.x, rotation.y, rotation.z);
            var projected = Vector3.Dot(vector, axis);
            var angle = 2f * Mathf.Atan2(projected, rotation.w) * Mathf.Rad2Deg;
            return Mathf.DeltaAngle(0f, angle);
        }
    }
}
