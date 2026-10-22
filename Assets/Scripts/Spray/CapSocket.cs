using System.Collections.Generic;
using Freehand.Audio;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// The seat on top of the can where a cap is pushed onto the valve stem.
    /// Owns "which PhysicalCap is on this can": CapManager (and through it
    /// SprayEmitter/AudioManager) only ever learns about a cap from here, the
    /// moment it physically seats or comes off.
    ///
    /// Hierarchy it expects (built by V04SceneBuilder):
    ///   CapSocket                  - fixed on the can, at the seat point, +Z = the can's front
    ///   ├── ValveStem              - static, what you see with no cap on
    ///   ├── InstallPreview         - faint ring shown while a held cap would fit
    ///   └── NozzlePivot            - installParent; NozzleAnimator presses it down
    ///       ├── [installed cap]    - seated at local identity
    ///       └── SprayOrigin        - moved onto the installed cap's orifice
    ///
    /// Seating is deliberately conservative: only on release, only within
    /// `snapRadius` of the seat and within `maxTiltDegrees` of the valve
    /// axis, then a short slide (PhysicalCap.seatSeconds) - never a pull
    /// toward the can from a distance.
    /// </summary>
    public class CapSocket : MonoBehaviour
    {
        [SerializeField] Transform installParent;
        [SerializeField] Transform sprayOrigin;
        [SerializeField] CapManager capManager;
        [SerializeField] VRGrabInteractable canGrab;
        [SerializeField] SprayCanSfx sfx;
        [SerializeField] PhysicalCap initialCap;
        [SerializeField] GameObject installPreview;

        [Tooltip("Child of installParent where the index finger presses (the can's VRGrabInteractable.fingerRest). Moved onto the seated cap's finger pad, or onto the bare valve stem.")]
        [SerializeField] Transform fingerRest;

        [Tooltip("Height of the bare valve stem above the seat, meters - where the finger rests with no cap on.")]
        [SerializeField] float stemHeight = 0.006f;

        [Tooltip("Max distance, in meters, between the cap's seat point and this socket for a release to seat the cap.")]
        [SerializeField] float snapRadius = 0.035f;

        [Tooltip("Max angle between the cap's valve axis and the can's for a release to seat the cap.")]
        [SerializeField] float maxTiltDegrees = 40f;

        static readonly List<CapSocket> ActiveSockets = new List<CapSocket>();

        public static IReadOnlyList<CapSocket> Active => ActiveSockets;

        public PhysicalCap Installed { get; private set; }
        public Transform InstallParent => installParent;
        public Transform SprayOrigin => sprayOrigin;
        public float SnapRadius => snapRadius;

        PhysicalCap reserved;

        void Awake()
        {
            SetPreview(false);

            if (initialCap != null)
            {
                initialCap.EnterSocketImmediate(this);
                FinishInstall(initialCap, silent: true, capHandIsLeft: false);
            }
            else
            {
                RestFingerOnStem();
            }
        }

        void OnEnable()
        {
            if (!ActiveSockets.Contains(this))
                ActiveSockets.Add(this);
        }

        void OnDisable()
        {
            ActiveSockets.Remove(this);
            SetPreview(false);
        }

        void Update()
        {
            // A seated cap can only be pulled off while someone is holding
            // the can: otherwise reaching for a can resting on the table
            // would pull its cap off instead of picking the can up.
            if (Installed != null)
                Installed.Grab.GrabEnabled = canGrab == null || canGrab.IsGrabbed;
        }

        public bool Accepts(PhysicalCap cap, out float distance)
        {
            distance = Vector3.Distance(cap.transform.position, installParent.position);
            return Installed == null
                && reserved == null
                && distance <= snapRadius
                && Vector3.Angle(cap.transform.up, installParent.up) <= maxTiltDegrees;
        }

        public bool TryReserve(PhysicalCap cap)
        {
            if (Installed != null || (reserved != null && reserved != cap))
                return false;

            reserved = cap;
            return true;
        }

        public void CompleteInstall(PhysicalCap cap, bool capHandIsLeft) => FinishInstall(cap, silent: false, capHandIsLeft);

        void FinishInstall(PhysicalCap cap, bool silent, bool capHandIsLeft)
        {
            reserved = null;
            Installed = cap;
            cap.Grab.GrabEnabled = canGrab == null || canGrab.IsGrabbed;
            AlignSprayOrigin(cap);

            if (capManager != null)
                capManager.SetInstalled(cap.Spray);

            if (silent)
                return;

            if (sfx != null)
                sfx.PlayCapInstall();

            // Felt in both hands, like a real cap clicking onto a stem.
            HapticFeedback.Pulse(capHandIsLeft, 0.45f, 0.04f);
            if (canGrab != null && canGrab.IsGrabbed)
                HapticFeedback.Pulse(canGrab.HeldByLeftHand, 0.3f, 0.03f);
        }

        /// <summary>Called by a cap leaving this socket (grabbed off, or pulled mid-seat).</summary>
        public void NotifyRemoved(PhysicalCap cap)
        {
            if (reserved == cap)
            {
                reserved = null;
                return;
            }

            if (Installed != cap)
                return;

            Installed = null;
            cap.Grab.GrabEnabled = true;
            RestFingerOnStem();

            if (capManager != null)
                capManager.SetInstalled(null);

            if (sfx != null)
                sfx.PlayCapRemove();

            if (canGrab != null && canGrab.IsGrabbed)
                HapticFeedback.Pulse(canGrab.HeldByLeftHand, 0.2f, 0.03f);
        }

        // SprayOrigin and the cap are both children of installParent, so
        // copying the orifice's world pose fixes their relative offset
        // exactly, whether or not the nozzle happens to be pressed right now.
        void AlignSprayOrigin(PhysicalCap cap)
        {
            if (fingerRest != null && cap.FingerPad != null)
                fingerRest.position = cap.FingerPad.position;

            // The hand puts the index on this cap's real surface (and keeps
            // every finger out of it) from its mesh.
            if (canGrab != null)
                canGrab.FingerRestSurface = cap.GetComponent<MeshFilter>();

            if (sprayOrigin == null || cap.Orifice == null)
                return;

            sprayOrigin.SetPositionAndRotation(cap.Orifice.position, cap.Orifice.rotation);
        }

        void RestFingerOnStem()
        {
            if (canGrab != null)
                canGrab.FingerRestSurface = null;

            if (fingerRest != null && installParent != null)
                fingerRest.position = installParent.position + installParent.up * stemHeight;
        }

        public void SetPreview(bool on)
        {
            if (installPreview != null && installPreview.activeSelf != on)
                installPreview.SetActive(on);
        }

        // Play Mode test hooks for the Editor, where there are no hands to do
        // the gesture: they run the exact same remove/install path.
        [ContextMenu("Debug/Remove installed cap to its rack")]
        void DebugRemoveInstalledCap()
        {
            if (!Application.isPlaying || Installed == null)
                return;

            Installed.ReturnHome();
        }

        [ContextMenu("Debug/Install next cap from the rack")]
        void DebugInstallFromRack()
        {
            if (!Application.isPlaying || Installed != null)
                return;

            foreach (var cap in FindObjectsByType<PhysicalCap>())
            {
                if (cap.State == CapState.InRack && cap.InstallOnForDebug(this))
                    return;
            }
        }
    }
}
