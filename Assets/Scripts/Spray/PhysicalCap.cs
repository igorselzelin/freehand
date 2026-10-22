using Freehand.Tools;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Spray
{
    public enum CapState
    {
        /// <summary>Loose in the world, under physics.</summary>
        Detached,
        Held,
        /// <summary>A hand has hold of it while it is still seated: it comes off once pulled far enough.</summary>
        Gripped,
        /// <summary>Held, and close/aligned enough to a free socket that releasing it would seat it.</summary>
        AvailableForInstall,
        /// <summary>Released onto a socket, sliding the last few millimetres into place.</summary>
        Installing,
        Installed,
        InRack,
    }

    /// <summary>
    /// A spray cap as a real object: it can sit on the can, be pulled off,
    /// carried, dropped, parked on a rack peg and pushed onto the can again.
    /// Its SprayCap data is what the spray uses while it is seated - the cap's
    /// behaviour follows from which object is physically on the can, never
    /// from a menu selection.
    ///
    /// Model convention (every cap model must follow it, so any model fits
    /// the same socket): pivot at the seat (bottom centre, where it sits on
    /// the valve stem), +Y along the valve axis, +Z out of the orifice. The
    /// `orifice` child marks the exact exit point; its forward is the spray
    /// direction. The `fingerPad` child marks where the index finger presses
    /// (on the actuator's finger scoop, which faces away from the orifice,
    /// toward the hand). CapSocket copies both onto the can (SprayOrigin, the
    /// index finger's rest point) when the cap seats, so a different cap
    /// model with its hole or pad somewhere else still works from its own.
    ///
    /// While not loose, the cap is kinematic with a trigger collider: it
    /// must never shove the can it sits on, the hand holding it, or the rack.
    /// VRGrabber's overlap test includes triggers, so it stays grabbable.
    ///
    /// Taking a seated cap is a pull, not a touch: grabbing it leaves it on
    /// the stem (Gripped), riding up a couple of millimetres with the hand,
    /// until the hand has pulled pullOffDistance away - then it comes off
    /// into the fingers, with the remove sound and a click in both hands.
    /// Letting go before that leaves it seated.
    /// </summary>
    [RequireComponent(typeof(VRGrabInteractable))]
    [RequireComponent(typeof(Rigidbody))]
    public class PhysicalCap : MonoBehaviour
    {
        [SerializeField] SprayCap spray = new SprayCap();

        [Tooltip("Exit point of the spray on this cap's model. Forward (+Z) is the spray direction.")]
        [SerializeField] Transform orifice;

        [Tooltip("Where the index finger presses this cap: on its finger scoop, facing the hand.")]
        [SerializeField] Transform fingerPad;

        [SerializeField] Collider capCollider;

        [Tooltip("Where this cap goes back to if it is lost (falls out of the room).")]
        [SerializeField] CapRack homeRack;
        [SerializeField] int homeSlot = -1;

        [Tooltip("True while this cap's mesh is a stand-in (derived from another cap's model). Replace the MeshFilter mesh with the real model and re-place 'orifice' on its real hole.")]
        [SerializeField] bool usesPlaceholderModel;

        [Tooltip("Seconds the cap takes to slide from where it was released to its seated pose. Short on purpose: long enough to read as a part pushing home, too short to feel like a magnet.")]
        [SerializeField] float seatSeconds = 0.08f;

        [Tooltip("A loose cap below this world height has fallen out of the room and is returned to its rack.")]
        [SerializeField] float lostBelowWorldY = -1f;

        [Tooltip("How far, meters, the hand holding a seated cap has to pull it (away along the stem; sideways counts half) before it comes off.")]
        [SerializeField] float pullOffDistance = 0.015f;

        [Tooltip("How far, meters, a seated cap rides up the stem with the pull before it lets go.")]
        [SerializeField] float pullGive = 0.0025f;

        public SprayCap Spray => spray;
        public Transform Orifice => orifice;

        /// <summary>Where the index finger presses this cap (on its finger scoop).</summary>
        public Transform FingerPad => fingerPad;

        public CapState State { get; private set; } = CapState.Detached;
        public bool UsesPlaceholderModel => usesPlaceholderModel;

        public VRGrabInteractable Grab
        {
            get
            {
                Cache();
                return grab;
            }
        }

        VRGrabInteractable grab;
        Rigidbody body;
        float worldScale = 1f;

        CapSocket socket;
        CapSocket hoverSocket;
        CapRack rack;
        int rackSlot = -1;

        Vector3 gripStart;
        int tensionStep;
        Transform seatTarget;
        Vector3 seatFromPosition;
        Quaternion seatFromRotation;
        float seatElapsed;
        System.Action onSeated;

        // Owners (CapSocket/CapRack) may claim this cap from their own Awake,
        // which can run before this one's - so every entry point caches.
        void Cache()
        {
            if (grab != null)
                return;

            grab = GetComponent<VRGrabInteractable>();
            body = GetComponent<Rigidbody>();
            if (capCollider == null)
                capCollider = GetComponent<Collider>();

            worldScale = transform.lossyScale.x;
            grab.Grabbed += OnGrabbed;
            grab.Released += OnReleased;
        }

        void Awake() => Cache();

        void Start()
        {
            // Not claimed by a socket or rack: a cap lying loose in the world.
            if (State == CapState.Detached)
                SetLoosePhysics();
        }

        void OnDisable()
        {
            if (hoverSocket != null)
            {
                hoverSocket.SetPreview(false);
                hoverSocket = null;
            }
        }

        void OnDestroy()
        {
            if (grab == null)
                return;

            grab.Grabbed -= OnGrabbed;
            grab.Released -= OnReleased;
        }

        internal void EnterSocketImmediate(CapSocket target)
        {
            Cache();
            ClearOwners();
            socket = target;
            State = CapState.Installed;
            SetOwnedPhysics();
            SnapInto(target.InstallParent);
        }

        internal void EnterRackImmediate(CapRack target, int slot)
        {
            Cache();
            ClearOwners();
            PlaceInRack(target, slot, animate: false);
        }

        void OnGrabbed(VRGrabInteractable _)
        {
            onSeated = null;
            seatTarget = null;
            tensionStep = 0;

            // Fingertips meeting the plastic.
            if (Freehand.Audio.SprayCanSfx.Instance != null)
                Freehand.Audio.SprayCanSfx.Instance.PlayCapTouch(transform.position, grab.GrabHardness);

            // Seated: the fingers have it, the stem still holds it.
            if (State == CapState.Installed && socket != null && grab.CurrentHand != null)
            {
                State = CapState.Gripped;
                SnapInto(socket.InstallParent);
                gripStart = socket.InstallParent.InverseTransformPoint(grab.CurrentHand.position);
                return;
            }

            ClearOwners();
            State = CapState.Held;
            SetOwnedPhysics();
        }

        void ClearOwners()
        {
            if (socket != null)
            {
                var previous = socket;
                socket = null;
                previous.NotifyRemoved(this);
            }

            if (rack != null)
            {
                rack.Vacate(rackSlot);
                rack = null;
                rackSlot = -1;
            }
        }

        void OnReleased(VRGrabInteractable _)
        {
            var releasedByLeftHand = grab.HeldByLeftHand;

            // Let go before it came off: it never left the stem.
            if (State == CapState.Gripped && socket != null)
            {
                State = CapState.Installed;
                SetOwnedPhysics();
                SnapInto(socket.InstallParent);
                return;
            }

            if (hoverSocket != null)
            {
                var target = hoverSocket;
                hoverSocket = null;
                target.SetPreview(false);

                if (TryInstallOn(target, releasedByLeftHand))
                    return;
            }

            var nearRack = CapRack.FindFreeSlotNear(transform.position, out var slot);
            if (nearRack != null)
            {
                PlaceInRack(nearRack, slot, animate: true);
                return;
            }

            // Dropped away from everything: a real object, it falls.
            State = CapState.Detached;
            transform.SetParent(null, true);
            SetLoosePhysics();
        }

        bool TryInstallOn(CapSocket target, bool capHandIsLeft)
        {
            if (!target.TryReserve(this))
                return false;

            socket = target;
            State = CapState.Installing;
            SetOwnedPhysics();
            SeatOnto(target.InstallParent, () =>
            {
                State = CapState.Installed;
                target.CompleteInstall(this, capHandIsLeft);
            });
            return true;
        }

        void PlaceInRack(CapRack target, int slot, bool animate)
        {
            target.Occupy(slot, this);
            rack = target;
            rackSlot = slot;
            State = CapState.InRack;
            SetOwnedPhysics();

            if (animate)
                SeatOnto(target.Slot(slot), () => target.OnCapSeated(this));
            else
                SnapInto(target.Slot(slot));
        }

        void Update()
        {
            if (seatTarget != null)
            {
                UpdateSeat();
                return;
            }

            switch (State)
            {
                case CapState.Held:
                case CapState.AvailableForInstall:
                    UpdateHover();
                    break;

                case CapState.Gripped:
                    UpdatePull();
                    break;

                case CapState.Detached:
                    if (transform.position.y < lostBelowWorldY)
                        ReturnHome();
                    break;
            }
        }

        // How far the gripping hand has moved from where it took hold, in
        // the stem's frame (so moving both hands together does not count).
        void UpdatePull()
        {
            if (socket == null || grab.CurrentHand == null)
                return;

            var stem = socket.InstallParent;
            var scale = Mathf.Max(0.0001f, stem.lossyScale.x);
            var delta = (stem.InverseTransformPoint(grab.CurrentHand.position) - gripStart) * scale;
            var along = Mathf.Max(0f, delta.y);
            var pull = along + 0.5f * new Vector2(delta.x, delta.z).magnitude;

            // The stem holds on: a squeak at each third of the pull, the
            // moment it lets go is the pop (CapSocket.NotifyRemoved).
            var step = Mathf.FloorToInt(Mathf.Clamp01(pull / pullOffDistance) * 3f);
            if (step < tensionStep)
                tensionStep = step;
            else if (step > tensionStep && step < 3)
            {
                tensionStep = step;
                if (Freehand.Audio.SprayCanSfx.Instance != null)
                    Freehand.Audio.SprayCanSfx.Instance.PlayCapTension(transform.position, pull / pullOffDistance);
            }

            if (pull >= pullOffDistance)
            {
                PullOff();
                return;
            }

            transform.localPosition = Vector3.up * (Mathf.Min(along * (pullGive / pullOffDistance), pullGive) / scale);
        }

        void PullOff()
        {
            var hand = grab.CurrentHand;
            ClearOwners();
            HapticFeedback.Pulse(grab.HeldByLeftHand, 0.5f, 0.04f);
            transform.SetParent(hand, true);
            State = CapState.Held;
            SetOwnedPhysics();
        }

        void UpdateHover()
        {
            CapSocket best = null;
            var bestDistance = float.MaxValue;

            var sockets = CapSocket.Active;
            for (var i = 0; i < sockets.Count; i++)
            {
                if (sockets[i].Accepts(this, out var distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = sockets[i];
                }
            }

            if (best != hoverSocket)
            {
                if (hoverSocket != null)
                    hoverSocket.SetPreview(false);

                hoverSocket = best;

                // Subtle "it will fit here" cue: a faint tick in the hand
                // holding the cap and the socket's ring lighting up - no
                // pull toward the socket while still held.
                if (hoverSocket != null)
                {
                    hoverSocket.SetPreview(true);
                    HapticFeedback.Pulse(grab.HeldByLeftHand, 0.1f, 0.015f);
                }
            }

            State = hoverSocket != null ? CapState.AvailableForInstall : CapState.Held;
        }

        void SeatOnto(Transform target, System.Action done)
        {
            transform.SetParent(target, true);
            seatFromPosition = transform.localPosition;
            seatFromRotation = transform.localRotation;
            seatElapsed = 0f;
            seatTarget = target;
            onSeated = done;

            if (seatSeconds <= 0f)
                FinishSeat();
        }

        void UpdateSeat()
        {
            seatElapsed += Time.deltaTime;
            var t = Mathf.Clamp01(seatElapsed / seatSeconds);
            var eased = t * t * (3f - 2f * t);

            transform.localPosition = Vector3.Lerp(seatFromPosition, Vector3.zero, eased);
            transform.localRotation = Quaternion.Slerp(seatFromRotation, Quaternion.identity, eased);

            if (t >= 1f)
                FinishSeat();
        }

        void FinishSeat()
        {
            SnapInto(seatTarget);
            seatTarget = null;

            var done = onSeated;
            onSeated = null;
            done?.Invoke();
        }

        // Exact seated pose. Scale is re-derived from the cap's own world
        // size so repeated reparenting (hand -> world -> socket -> rack) can
        // never accumulate drift in how big the cap looks.
        void SnapInto(Transform parent)
        {
            transform.SetParent(parent, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            var parentScale = Mathf.Max(0.0001f, parent.lossyScale.x);
            transform.localScale = Vector3.one * (worldScale / parentScale);
        }

        // Owned = carried by a parent transform (socket pivot, rack peg, hand).
        // No interpolation then: interpolating a kinematic body that its
        // parent moves writes stale poses back into its transform, and the
        // seated cap drifted off the nozzle a little more with every move of
        // the can. Only a loose cap, moved by physics, is interpolated.
        void SetOwnedPhysics()
        {
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
            if (capCollider != null)
                capCollider.isTrigger = true;
        }

        void SetLoosePhysics()
        {
            body.isKinematic = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            if (capCollider != null)
                capCollider.isTrigger = false;
        }

        /// <summary>Puts the cap back on its home rack slot (or any free slot). Used when it is lost, and by the Editor debug commands.</summary>
        public void ReturnHome()
        {
            Cache();
            onSeated = null;
            seatTarget = null;
            ClearOwners();

            var target = homeRack;
            var slot = homeSlot;
            if (target == null || !target.IsFree(slot))
                target = CapRack.FindAnyFreeSlot(out slot);

            if (target == null)
                return;

            if (!body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            PlaceInRack(target, slot, animate: false);
        }

        /// <summary>Editor/debug path for testing without a headset: the same install the release gesture performs, from wherever the cap is.</summary>
        public bool InstallOnForDebug(CapSocket target)
        {
            Cache();
            if (grab.IsGrabbed)
                return false;

            ClearOwners();
            return TryInstallOn(target, capHandIsLeft: false);
        }
    }
}
