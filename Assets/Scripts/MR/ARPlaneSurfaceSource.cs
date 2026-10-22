using System;
using System.Collections;
using UnityEngine;
#if FREEHAND_MR
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.XR.OpenXR.Features.Meta;
#endif
#endif

namespace Freehand.MR
{
    /// <summary>Where the real-wall workflow stands, in the order the player meets it.</summary>
    public enum MRSurfaceStatus
    {
        /// <summary>Not looking for walls (virtual mode).</summary>
        Idle,

        /// <summary>Waiting for the answer to the Quest's permission dialog.</summary>
        RequestingPermission,

        /// <summary>The player (or the system) refused: no room data, no walls.</summary>
        PermissionDenied,

        /// <summary>Permission granted, the runtime has not reported a wall yet.</summary>
        WaitingForRoom,

        /// <summary>Permission granted and nothing came: the Quest has no room set up here (Space Setup), or no wall in it.</summary>
        NoWalls,

        /// <summary>At least one wall is reported and paintable.</summary>
        Ready,

        /// <summary>The device cannot report planes at all (feature off, no support).</summary>
        Unavailable,
    }

#if FREEHAND_MR
    /// <summary>
    /// The real-wall source: reads the walls the Quest knows from its Space Setup through
    /// AR Foundation's plane manager (Meta's OpenXR provider, the XR_FB_scene plane type)
    /// and reports them to the MRSurfaceHub. This is the only place that touches AR planes.
    ///
    /// What it will not do: invent a wall. A plane is passed on only if the runtime
    /// classified it as a wall face (not an "invisible" wall that only closes the room
    /// outline), it is upright, and it is big enough to paint on. Floors, ceilings, tables
    /// and doors are not offered. Without permission, or without a room set up on the
    /// device, there are no walls and nothing can be painted on the room.
    /// </summary>
    public class ARPlaneSurfaceSource : MonoBehaviour
    {
        [SerializeField] ARPlaneManager planeManager;
        [SerializeField] ARSession session;
        [SerializeField] MRSurfaceHub hub;

        [Tooltip("Seconds to wait, after permission, for the first wall before saying there is no room data.")]
        [SerializeField] float noRoomSeconds = 8f;

        [Tooltip("A plane whose normal tilts more than this from horizontal (sin of the angle) is not upright enough to be a wall.")]
        [SerializeField] float maxNormalY = 0.25f;

        public MRSurfaceStatus Status { get; private set; } = MRSurfaceStatus.Idle;
        public event Action StatusChanged;

        /// <summary>Planes reported by the runtime since the manager started, of any kind (diagnostics).</summary>
        public int PlanesSeen { get; private set; }

        Coroutine routine;
        bool listening;

        void OnDisable() => StopListening();

        /// <summary>Starts looking for the room's walls: permission first, then the plane manager.</summary>
        public void Begin()
        {
            End();
            routine = StartCoroutine(BeginRoutine());
        }

        /// <summary>Stops looking, and takes every wall surface away (their artwork is saved first).</summary>
        public void End()
        {
            if (routine != null)
                StopCoroutine(routine);
            routine = null;

            StopListening();
            if (planeManager != null && planeManager.enabled)
                planeManager.enabled = false;
            if (hub != null)
                hub.ReleaseAll();
            PlanesSeen = 0;
            SetStatus(MRSurfaceStatus.Idle);
        }

        IEnumerator BeginRoutine()
        {
            if (planeManager == null || hub == null)
            {
                SetStatus(MRSurfaceStatus.Unavailable);
                yield break;
            }

            SetStatus(MRSurfaceStatus.RequestingPermission);
            bool? granted = null;
            MRPermission.RequestScene(result =>
            {
                if (granted == null)
                    granted = result;
            });
            while (granted == null)
                yield return null;

            if (granted != true)
            {
                SetStatus(MRSurfaceStatus.PermissionDenied);
                routine = null;
                yield break;
            }

            // Only now: Meta's runtime answers nothing before the permission, and the
            // manager must not run without it.
            planeManager.requestedDetectionMode = PlaneDetectionMode.Vertical;
            StartListening();
            planeManager.enabled = true;
            SetStatus(MRSurfaceStatus.WaitingForRoom);

            // Planes already known to the manager (it may have been running before).
            foreach (var plane in planeManager.trackables)
                Consider(plane);

            var waited = 0f;
            while (waited < noRoomSeconds && hub.CandidateCount == 0)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            routine = null;
            Refresh();
        }

        void Update()
        {
            if (Status == MRSurfaceStatus.WaitingForRoom || Status == MRSurfaceStatus.NoWalls || Status == MRSurfaceStatus.Ready)
            {
                // Ready follows what is there: walls can appear (the room got set up) or all vanish.
                if (routine == null)
                    Refresh();
            }
        }

        void Refresh()
        {
            if (planeManager == null || !planeManager.enabled)
                return;
            SetStatus(hub.CandidateCount > 0 ? MRSurfaceStatus.Ready : MRSurfaceStatus.NoWalls);
        }

        void StartListening()
        {
            if (listening || planeManager == null)
                return;
            planeManager.trackablesChanged.AddListener(OnPlanesChanged);
            listening = true;
        }

        void StopListening()
        {
            if (!listening || planeManager == null)
                return;
            planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
            listening = false;
        }

        void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added)
                Consider(plane);
            foreach (var plane in args.updated)
                Consider(plane);
            foreach (var removed in args.removed)
                hub.Forget(removed.Key.ToString());
        }

        void Consider(ARPlane plane)
        {
            if (plane == null)
                return;

            PlanesSeen++;
            var id = plane.trackableId.ToString();
            if (!IsWall(plane))
            {
                // It may have been a wall a moment ago (relabelled): not any more.
                hub.Forget(id);
                return;
            }

            hub.Report(id, plane.size, plane.centerInPlaneSpace, plane.transform, plane.trackingState != TrackingState.None);
        }

        bool IsWall(ARPlane plane)
        {
            var classes = plane.classifications;
            if ((classes & PlaneClassifications.WallFace) == 0)
                return false;
            return Mathf.Abs(plane.normal.y) <= maxNormalY;
        }

        /// <summary>
        /// Asks the Quest to run its room capture (Space Setup) again, from inside the app: the app
        /// is suspended while the player scans the room, and the walls are read again on return.
        /// False if the device cannot be asked.
        /// </summary>
        public bool RequestRoomScan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var meta = session != null ? session.subsystem as MetaOpenXRSessionSubsystem : null;
            return meta != null && meta.TryRequestSceneCapture();
#else
            return false;
#endif
        }

        void SetStatus(MRSurfaceStatus status)
        {
            if (status == Status)
                return;
            Status = status;
            StatusChanged?.Invoke();
        }
    }
#else
    /// <summary>
    /// Built without the MR packages (no FREEHAND_MR): no plane can be read, so no real wall is ever reported.
    /// Same API as the real source, always "Unavailable".
    /// </summary>
    public class ARPlaneSurfaceSource : MonoBehaviour
    {
        [SerializeField] MRSurfaceHub hub;

        public MRSurfaceStatus Status { get; private set; } = MRSurfaceStatus.Idle;
        public event Action StatusChanged;
        public int PlanesSeen => 0;

        public void Begin() => SetStatus(MRSurfaceStatus.Unavailable);

        public void End()
        {
            if (hub != null)
                hub.ReleaseAll();
            SetStatus(MRSurfaceStatus.Idle);
        }

        public bool RequestRoomScan() => false;

        void SetStatus(MRSurfaceStatus status)
        {
            if (status == Status)
                return;
            Status = status;
            StatusChanged?.Invoke();
        }
    }
#endif
}
