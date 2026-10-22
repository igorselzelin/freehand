using UnityEngine;

namespace Freehand.VR
{
    /// <summary>
    /// Marks an object as grabbable by a VRGrabber and handles the actual
    /// attach/detach mechanics.
    ///
    /// On grab, `grabPoint` (not necessarily the object's own pivot) is aligned
    /// to the hand's grip frame: grabPoint's world rotation becomes the hand's
    /// rotation composed with `attachRotationEuler`, and grabPoint's world
    /// position becomes the hand's position offset by `attachPositionOffset`.
    /// The "hand" is the grip anchor of the rendered hand (HandRig), which
    /// follows the OpenXR grip pose convention: +Z runs through the fist
    /// (little finger -> thumb side), -Y points where the fingers point.
    ///
    /// Spray can convention (derived - not tuned):
    ///   can +Y (body axis, bottom -> actuator) == grip +Z (through the fist)
    ///   can +Z (orifice direction)             == grip -Y (finger direction)
    /// so the can's GrabPoint is rotated -90 about X (GrabPoint.forward = can
    /// up, GrabPoint.up = can back). The can takes the place of the
    /// controller's handle in the same fist: the hand's grip axis crosses the
    /// palm diagonally (HandAnatomy), the fingers wrap the body, the index
    /// reaches over the top onto the actuator and the spray leaves forward.
    /// Where along its axis the fist holds the can is solved by the hand at
    /// grab time (HandRig.PrepareGripAnchor) so the index reaches fingerRest.
    ///
    /// `attachRotationEuler` stays zero; it exists only for calibration.
    ///
    /// Small objects (caps) set `keepWorldPoseOnGrab`: they follow the hand
    /// from wherever they were when grabbed instead of jumping to an attach
    /// pose, which is what pulling a cap off a can should look like.
    ///
    /// While held the body is kinematic and not interpolated: it is moved by
    /// its parent (the hand), and interpolating a kinematic body moved that
    /// way writes stale poses back into its transform - the object slides
    /// away from the hand, a little more with every movement.
    ///
    /// On release it becomes a physics body again carrying the hand's motion:
    /// the linear and angular velocity of the object itself over the last
    /// releaseVelocityWindow (sampled from its own rendered poses while
    /// held), so it can be tossed, thrown and spun.
    /// </summary>
    public class VRGrabInteractable : MonoBehaviour
    {
        /// <summary>One cylinder of the held shape, along the grip axis (grabPoint.forward), meters from grabPoint.</summary>
        [System.Serializable]
        public struct GripSection
        {
            public float bottom;
            public float top;
            public float radius;
        }

        [SerializeField] Transform grabPoint;
        [SerializeField] Rigidbody body;

        [Header("Attach")]
        [Tooltip("Extra rotation applied between the hand's grip frame and grabPoint. Calibration only; the canonical pose is defined by grabPoint itself.")]
        [SerializeField] Vector3 attachRotationEuler = Vector3.zero;

        [Tooltip("Where grabPoint sits relative to the hand, in the hand's local space.")]
        [SerializeField] Vector3 attachPositionOffset = Vector3.zero;

        [Tooltip("Keep the object's current pose relative to the hand when grabbed, instead of aligning grabPoint to the hand.")]
        [SerializeField] bool keepWorldPoseOnGrab;

        [Header("Hand pose")]
        [Tooltip("Radius, meters, of the part a closed fist wraps around (the can body). 0 for things held between the fingers.")]
        [SerializeField] float gripRadius;

        [Tooltip("Where the index fingertip rests while this is held (the can's actuator top). Optional.")]
        [SerializeField] Transform fingerRest;

        [Tooltip("The held shape as stacked cylinders along the grip axis (grabPoint space, meters): what the fingers close around. Measured from the model by the scene builder.")]
        [SerializeField] GripSection[] gripProfile = new GripSection[0];

        [Tooltip("Optional: a ray (this transform's forward) the holding fingers keep clear of - the spray leaving the nozzle.")]
        [SerializeField] Transform keepClearOf;

        public bool IsGrabbed { get; private set; }

        /// <summary>
        /// V0.14: out of every hand and flown by a freestyle move (a toss, a pass to the other hand) - kinematic,
        /// placed by the move each frame, owned by nobody. Ended by a hand taking it (Grab) or by EndFlight.
        /// </summary>
        public bool InFlight { get; private set; }

        /// <summary>0-1: how hard the hand closed on this the last time it was grabbed (hand speed and how fast the grip was squeezed) - read by audio. Set by the grabber just before Grab.</summary>
        public float GrabHardness { get; set; }
        public bool HeldByLeftHand { get; private set; }
        public Transform CurrentHand { get; private set; }

        public float GripRadius => gripRadius;
        public Transform FingerRest => fingerRest;
        public Transform GrabPoint => grabPoint;
        public GripSection[] GripProfile => gripProfile;
        public Transform KeepClearOf => keepClearOf;

        /// <summary>A point on the axis the fist closes around (grabPoint), and that axis' direction.</summary>
        public Vector3 GripAxisPoint => grabPoint != null ? grabPoint.position : transform.position;
        public Vector3 GripAxisDirection => grabPoint != null ? grabPoint.forward : transform.up;

        /// <summary>
        /// Signed distance from <paramref name="point"/> to the surface of a profile placed on the axis
        /// (<paramref name="axisPoint"/>, <paramref name="axis"/>), minus <paramref name="margin"/>:
        /// negative inside. Exact for the union of the stacked cylinders, end faces included.
        /// </summary>
        public static float ProfileClearance(GripSection[] profile, Vector3 axisPoint, Vector3 axis, Vector3 point, float margin)
        {
            var offset = point - axisPoint;
            var height = Vector3.Dot(offset, axis);
            var radial = (offset - axis * height).magnitude;
            var best = float.PositiveInfinity;
            for (var i = 0; i < profile.Length; i++)
            {
                var section = profile[i];
                if (section.top <= section.bottom || section.radius <= 0f)
                    continue;

                var outRadial = radial - section.radius;
                var outAxial = Mathf.Max(section.bottom - height, height - section.top);
                var distance = outRadial <= 0f && outAxial <= 0f
                    ? Mathf.Max(outRadial, outAxial)
                    : new Vector2(Mathf.Max(outRadial, 0f), Mathf.Max(outAxial, 0f)).magnitude;
                best = Mathf.Min(best, distance);
            }

            return best - margin;
        }

        RigidbodyInterpolation interpolationWhenFree;

        /// <summary>Local pose relative to the hand right after attaching - what CanWallClearance restores before applying its push each frame.</summary>
        public Vector3 AttachedLocalPosition { get; private set; }
        public Quaternion AttachedLocalRotation { get; private set; }

        /// <summary>When false, VRGrabbers ignore this object (e.g. a cap seated on a can nobody is holding).</summary>
        public bool GrabEnabled { get; set; } = true;

        /// <summary>
        /// How far the holding hand's index finger has travelled toward FingerRest, 0 (resting on the
        /// grip) to 1 (touching it). Set every frame by the tool that owns the trigger mapping (the
        /// can's NozzleAnimator, from SprayEmitter.RawTriggerValue); read by HandBonePoseController.
        /// </summary>
        public float FingerApproach { get; set; }

        /// <summary>
        /// The part the index presses at FingerRest - the seated cap's mesh - so the hand can put the
        /// finger on its real surface and keep every finger out of it. Set by the tool that owns the
        /// part (CapSocket); null when there is nothing there but the rest point (bare valve stem).
        /// </summary>
        public MeshFilter FingerRestSurface { get; set; }

        /// <summary>True while the object is carried by the hand it was grabbed with (not, say, a cap still seated on its can while being pulled).</summary>
        public bool AttachedToHand => IsGrabbed && CurrentHand != null && transform.parent == CurrentHand;

        [Header("Throw")]
        [Tooltip("How far back, seconds, the hand's motion is averaged to get the release velocity - long enough to smooth tracking jitter, short enough to be the motion of the throw itself.")]
        [SerializeField] float releaseVelocityWindow = 0.1f;

        [Tooltip("Caps on the inherited velocity, against tracking glitches at the moment of release.")]
        [SerializeField] float maxReleaseSpeed = 12f;
        [SerializeField] float maxReleaseAngularSpeed = 30f;

        // Recent poses of the held object's centre of mass, sampled every
        // LateUpdate after HandRig has placed the hand (the object is its
        // child, so these are the rendered motion of the object itself).
        const int SampleCount = 16;
        readonly Vector3[] samplePositions = new Vector3[SampleCount];
        readonly Quaternion[] sampleRotations = new Quaternion[SampleCount];
        readonly float[] sampleTimes = new float[SampleCount];
        int sampleHead;
        int sampleFilled;

        Vector3 settleFrom;
        Vector3 settleTo;
        float settleDuration;
        float settleElapsed = -1f;

        /// <summary>Raised after the object has been attached to a hand.</summary>
        public event System.Action<VRGrabInteractable> Grabbed;

        /// <summary>Raised after the object has been detached and handed back to its previous parent with physics re-enabled - listeners may override both.</summary>
        public event System.Action<VRGrabInteractable> Released;

        Transform originalParent;

        /// <summary>Live-tunable by GripCalibrator; re-applies immediately if the object is currently held.</summary>
        public Vector3 AttachRotationEuler
        {
            get => attachRotationEuler;
            set
            {
                attachRotationEuler = value;
                if (IsGrabbed && !keepWorldPoseOnGrab)
                {
                    ApplyAttach();
                    AttachedLocalPosition = transform.localPosition;
                    AttachedLocalRotation = transform.localRotation;
                }
            }
        }

        void Awake()
        {
            if (body == null)
                body = GetComponent<Rigidbody>();

            // Unity's default (7 rad/s) would clip a wrist flick.
            if (body != null)
                body.maxAngularVelocity = maxReleaseAngularSpeed;
        }

        /// <summary>
        /// Slides the held object, over <paramref name="seconds"/>, to a new position relative to the
        /// hand: a small part (a cap) coming the last centimetres into the fingers that took it,
        /// instead of hanging where it was when the grab started. Position only; eased; restarts if
        /// called again.
        /// </summary>
        public void SettleInHand(Vector3 localPosition, float seconds)
        {
            if (!AttachedToHand)
                return;

            settleFrom = transform.localPosition;
            settleTo = localPosition;
            settleDuration = Mathf.Max(0.0001f, seconds);
            settleElapsed = 0f;
        }

        // ---------- presentation (item inspection) ----------

        Vector3 presentationPivot;
        Quaternion presentationRotation = Quaternion.identity;
        Vector3 presentationShift;
        bool presenting;

        /// <summary>True while an inspection is showing the object off in the hand (see SetPresentation).</summary>
        public bool Presenting => presenting;

        /// <summary>
        /// Inspection: turns the held object by <paramref name="rotation"/> (object space) about
        /// <paramref name="pivot"/> (object space), on top of the pose the hand attached. Only what is
        /// shown changes: the hand still carries the attached pose, and a throw still takes the hand's
        /// own motion (the release sampling leaves the presentation out). Let go mid-inspection, the
        /// object flies as it was shown - no snap back.
        /// </summary>
        public void SetPresentation(Vector3 pivot, Quaternion rotation)
        {
            presentationPivot = pivot;
            presentationRotation = rotation;
            presenting = true;
        }

        /// <summary>Back to the attached pose exactly (call once the inspection has turned it back).</summary>
        public void ClearPresentation()
        {
            if (!presenting)
                return;

            presenting = false;
            if (AttachedToHand)
            {
                transform.localPosition -= presentationShift;
                transform.localRotation = AttachedLocalRotation;
            }

            presentationShift = Vector3.zero;
            presentationRotation = Quaternion.identity;
        }

        void ApplyPresentation()
        {
            if (!presenting)
                return;

            if (!AttachedToHand)
            {
                presenting = false;
                presentationShift = Vector3.zero;
                presentationRotation = Quaternion.identity;
                return;
            }

            var attached = AttachedLocalRotation;
            var shift = attached * Vector3.Scale(transform.localScale, presentationPivot - presentationRotation * presentationPivot);
            transform.localPosition += shift - presentationShift;
            presentationShift = shift;
            transform.localRotation = attached * presentationRotation;
        }

        void LateUpdate()
        {
            ApplyPresentation();

            if (settleElapsed >= 0f)
            {
                if (!AttachedToHand)
                {
                    settleElapsed = -1f;
                }
                else
                {
                    settleElapsed += Time.deltaTime;
                    var t = Mathf.Clamp01(settleElapsed / settleDuration);
                    transform.localPosition = Vector3.Lerp(settleFrom, settleTo, t * t * (3f - 2f * t));
                    if (t >= 1f)
                    {
                        AttachedLocalPosition = settleTo;
                        settleElapsed = -1f;
                    }
                }
            }

            if (!IsGrabbed || body == null)
                return;

            // From the transform, not body.worldCenterOfMass: a kinematic body
            // carried by its parent only syncs its physics pose at the next
            // simulation step.
            // During an inspection, the pose without the presentation: what
            // the hand itself did is all a throw may inherit.
            if (presenting && AttachedToHand)
            {
                var localRotation = transform.localRotation * Quaternion.Inverse(presentationRotation);
                var localPosition = transform.localPosition - presentationShift;
                samplePositions[sampleHead] = transform.parent.TransformPoint(localPosition + localRotation * Vector3.Scale(transform.localScale, body.centerOfMass));
                sampleRotations[sampleHead] = transform.parent.rotation * localRotation;
            }
            else
            {
                samplePositions[sampleHead] = transform.TransformPoint(body.centerOfMass);
                sampleRotations[sampleHead] = transform.rotation;
            }
            sampleTimes[sampleHead] = Time.time;
            sampleHead = (sampleHead + 1) % SampleCount;
            sampleFilled = Mathf.Min(sampleFilled + 1, SampleCount);
        }

        // The held object's motion over the last releaseVelocityWindow:
        // linear velocity as the least-squares slope of the centre of mass
        // against time (robust to one noisy tracking sample), angular
        // velocity from the rotation between the window's ends.
        bool TryReleaseVelocity(out Vector3 linear, out Vector3 angular)
        {
            linear = Vector3.zero;
            angular = Vector3.zero;
            if (sampleFilled < 2)
                return false;

            var newest = (sampleHead - 1 + SampleCount) % SampleCount;
            var latest = sampleTimes[newest];
            var count = 0;
            var oldest = newest;
            float meanT = 0f;
            var meanP = Vector3.zero;
            for (var k = 0; k < sampleFilled; k++)
            {
                var i = (newest - k + SampleCount) % SampleCount;
                if (latest - sampleTimes[i] > releaseVelocityWindow && count >= 2)
                    break;

                oldest = i;
                meanT += sampleTimes[i];
                meanP += samplePositions[i];
                count++;
            }

            var span = latest - sampleTimes[oldest];
            if (count < 2 || span <= 0.0001f)
                return false;

            meanT /= count;
            meanP /= count;
            float varianceT = 0f;
            var covariance = Vector3.zero;
            for (var k = 0; k < count; k++)
            {
                var i = (newest - k + SampleCount) % SampleCount;
                var dt = sampleTimes[i] - meanT;
                varianceT += dt * dt;
                covariance += (samplePositions[i] - meanP) * dt;
            }

            linear = varianceT > 0f ? covariance / varianceT : Vector3.zero;
            linear = Vector3.ClampMagnitude(linear, maxReleaseSpeed);

            (sampleRotations[newest] * Quaternion.Inverse(sampleRotations[oldest])).ToAngleAxis(out var degrees, out var axis);
            degrees = Mathf.DeltaAngle(0f, degrees);
            if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
                angular = Vector3.ClampMagnitude(axis * (degrees * Mathf.Deg2Rad / span), maxReleaseAngularSpeed);
            return true;
        }

        public void Grab(Transform hand, bool isLeftHand)
        {
            if (IsGrabbed)
                Release();

            // From the air (a catch): the body is still kinematic and its "free" interpolation was kept when
            // the flight began - it must not be overwritten by the in-hand setting.
            var fromFlight = InFlight;
            InFlight = false;
            RestoreFlightColliders();
            IsGrabbed = true;
            presenting = false;
            presentationShift = Vector3.zero;
            presentationRotation = Quaternion.identity;
            CurrentHand = hand;
            HeldByLeftHand = isLeftHand;
            originalParent = transform.parent;

            if (body != null)
            {
                // Stopped before it turns kinematic: a kinematic body's
                // velocities cannot be set (Unity warns and ignores it).
                if (!body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                body.isKinematic = true;
                if (!fromFlight)
                    interpolationWhenFree = body.interpolation;
                body.interpolation = RigidbodyInterpolation.None;
            }

            transform.SetParent(hand, true);
            if (!keepWorldPoseOnGrab)
                ApplyAttach();

            AttachedLocalPosition = transform.localPosition;
            AttachedLocalRotation = transform.localRotation;
            sampleFilled = 0;
            FingerApproach = 0f;

            // Reuses the same haptics utility SprayCanShake already fires
            // through - a short confirmation pulse on the hand that just
            // grabbed, not a new feedback system.
            HapticFeedback.Pulse(isLeftHand, 0.25f, 0.05f);

            Grabbed?.Invoke(this);
        }

        void ApplyAttach()
        {
            var offsetRotation = Quaternion.Euler(attachRotationEuler);

            if (grabPoint == null)
            {
                transform.localPosition = attachPositionOffset;
                transform.localRotation = offsetRotation;
                return;
            }

            // Rotation composition is scale-independent, so grabPoint's world
            // rotation ends up exactly hand.rotation * offsetRotation.
            transform.localRotation = offsetRotation * Quaternion.Inverse(grabPoint.localRotation);

            // General case, so grabPoint can sit anywhere on the object (the
            // can is gripped partway up its body, not at its pivot): solve for
            // the object position that puts grabPoint at the requested offset.
            var grabPointLocal = transform.localRotation * Vector3.Scale(grabPoint.localPosition, transform.localScale);
            transform.localPosition = attachPositionOffset - grabPointLocal;
        }

        public void Release()
        {
            if (!IsGrabbed)
                return;

            IsGrabbed = false;
            CurrentHand = null;
            FingerApproach = 0f;
            settleElapsed = -1f;

            transform.SetParent(originalParent, true);

            if (body != null)
            {
                // Dynamic first, then the hand's motion: velocities can only be
                // set on a non-kinematic body. Thrown, tossed or just let go,
                // the object carries on the way the hand was moving it.
                body.isKinematic = false;
                body.interpolation = interpolationWhenFree;
                if (TryReleaseVelocity(out var linear, out var angular))
                {
                    body.linearVelocity = linear;
                    body.angularVelocity = angular;
                }
            }

            sampleFilled = 0;
            Released?.Invoke(this);
        }

        // ---------- flight (V0.14 freestyle) ----------

        readonly System.Collections.Generic.List<Collider> flightTriggers = new System.Collections.Generic.List<Collider>();

        void RestoreFlightColliders()
        {
            foreach (var c in flightTriggers)
            {
                if (c != null)
                    c.isTrigger = false;
            }

            flightTriggers.Clear();
        }

        /// <summary>
        /// Lets go for a move the game flies (a toss, a pass): no hand owns the object any more, but it is not
        /// released - it stays kinematic (no gravity, no collision impulse) and the move places it each frame
        /// until a hand takes it (Grab) or the move ends it (EndFlight). Released is not raised.
        /// </summary>
        public bool BeginFlight()
        {
            if (!IsGrabbed)
                return false;

            IsGrabbed = false;
            CurrentHand = null;
            FingerApproach = 0f;
            settleElapsed = -1f;
            presenting = false;
            presentationShift = Vector3.zero;
            presentationRotation = Quaternion.identity;
            transform.SetParent(originalParent, true);
            sampleFilled = 0;
            InFlight = true;

            // V0.15: a kinematic body placed by a move pushes whatever it passes through with infinite mass (a loose
            // cap, the other can). In the air its own solid colliders become triggers: they push nothing, and they
            // still answer the grab query (which includes triggers), so the other hand can take it out of the air.
            flightTriggers.Clear();
            foreach (var c in GetComponentsInChildren<Collider>())
            {
                if (c.enabled && !c.isTrigger && c.attachedRigidbody == body && (!(c is MeshCollider mesh) || mesh.convex))
                {
                    c.isTrigger = true;
                    flightTriggers.Add(c);
                }
            }

            return true;
        }

        /// <summary>
        /// The flight is over and no hand took it (let go of, missed, blocked): a physics body again, carrying on
        /// with the motion it was flying with. This is the object's real release (Released is raised).
        /// </summary>
        public void EndFlight(Vector3 velocity, Vector3 angularVelocity)
        {
            if (!InFlight)
                return;

            InFlight = false;
            RestoreFlightColliders();
            if (body != null)
            {
                body.isKinematic = false;
                body.interpolation = interpolationWhenFree;
                body.linearVelocity = Vector3.ClampMagnitude(velocity, maxReleaseSpeed);
                body.angularVelocity = Vector3.ClampMagnitude(angularVelocity, maxReleaseAngularSpeed);
            }

            Released?.Invoke(this);
        }

        // Editor-only (Unity strips OnDrawGizmos from player builds), for
        // visually confirming GrabPoint's position/orientation against the
        // visible can model in the Scene view while tuning attach values.
        void OnDrawGizmos()
        {
            if (grabPoint == null)
                return;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(grabPoint.position, 0.02f);
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(grabPoint.position, grabPoint.position + grabPoint.forward * 0.1f);
            Gizmos.color = Color.green;
            Gizmos.DrawLine(grabPoint.position, grabPoint.position + grabPoint.up * 0.1f);
        }
    }
}
