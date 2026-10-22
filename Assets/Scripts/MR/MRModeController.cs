using System;
using System.Collections;
using Freehand.Environment;
using Freehand.Paint;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if FREEHAND_MR
using Unity.XR.CompositionLayers.Services;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR.Features.Meta;
#endif

// V0.15.1: everything that needs the Meta OpenXR / AR Foundation packages is behind FREEHAND_MR. The V0.15.0 APK
// hung at startup on the Quest before the engine logged anything, and the only native component added since the
// last build that started (V0.12) is those packages' plugin (libUnityARFoundationMeta.so): the packages are out of
// the build until that is understood. Without FREEHAND_MR the game is the virtual room; MR says "not available".

namespace Freehand.MR
{
    public enum MRMode
    {
        /// <summary>The virtual practice room (the game as it was).</summary>
        Virtual,

        /// <summary>Passthrough is being switched on; the room is still there.</summary>
        Entering,

        /// <summary>The real room is the world: passthrough on, the virtual room's shell gone.</summary>
        Mixed,
    }

    public enum MRProblem
    {
        None,

        /// <summary>This device or build cannot do passthrough (the Editor, a headset without it, the feature off).</summary>
        Unsupported,

        /// <summary>Passthrough was asked for and did not start.</summary>
        PassthroughFailed,

        /// <summary>It was working, and stopped (the system took it back).</summary>
        PassthroughLost,
    }

    /// <summary>
    /// Switches between the virtual practice room and mixed reality (the real room, seen
    /// through passthrough, as the world to paint). Everything that is the same in both modes -
    /// the XR rig, the camera, the hands, the can, the caps, the paint system - is the
    /// same object in both: there is one rig, one camera, one audio listener.
    ///
    /// Entering mixed reality is a sequence that stops at the first thing that does not hold:
    ///   1. the device must be able to do passthrough (the Meta camera subsystem exists);
    ///   2. the AR session starts, the camera background becomes transparent black, the
    ///      passthrough camera manager is enabled;
    ///   3. passthrough is then CHECKED: the camera subsystem is running and the passthrough
    ///      composition layer exists. Only if so does the virtual room go away. If not, the
    ///      camera is put back as it was and the player stays in the virtual room, with the
    ///      reason on the panel.
    /// While in mixed reality passthrough is checked again twice a second, and on return to
    /// the app; if it is gone the player is returned to the virtual room instead of being left
    /// in an empty black space.
    ///
    /// Leaving puts everything back: camera, room, tools. The virtual room's paint and the
    /// real walls' paint are separate: B and Clear All reach only the mode you are in (the
    /// undo list is dropped at each switch, so B never reaches into paint that is not on
    /// screen). The Quest Guardian / boundary is not touched.
    /// </summary>
    public class MRModeController : MonoBehaviour
    {
        [Header("Rig (the existing one)")]
        [SerializeField] Camera head;
#if FREEHAND_MR
        [SerializeField] ARSession session;
        [SerializeField] ARCameraManager cameraManager;
#endif

        [Header("Real room")]
        [SerializeField] ARPlaneSurfaceSource walls;
        [SerializeField] MRSurfaceHub hub;

        [Header("Virtual room")]
        [Tooltip("The virtual room's shell and scenery: gone in mixed reality.")]
        [SerializeField] GameObject[] virtualOnly;

        [Tooltip("Renderers hidden in mixed reality while their colliders stay (the floor: a thrown can still lands on it).")]
        [SerializeField] Renderer[] hideRenderersInMixed;

        [Tooltip("The tools that go with the player: table, can, cap rack, colour palette, the panel.")]
        [SerializeField] AdaptiveRoomController layout;

        [Header("Timing")]
        [SerializeField] float startTimeoutSeconds = 4f;
        [SerializeField] float checkInterval = 0.5f;
        [SerializeField] float lostGraceSeconds = 2f;

        public MRMode Mode { get; private set; } = MRMode.Virtual;
        public MRProblem Problem { get; private set; }
        public bool IsMixed => Mode == MRMode.Mixed;
        public ARPlaneSurfaceSource Walls => walls;
        public MRSurfaceHub Hub => hub;

        /// <summary>The mode, a problem, or the passthrough state changed.</summary>
        public event Action Changed;

        CameraClearFlags savedClearFlags;
        Color savedBackground;
        bool savedAllowHdr;
        bool savedPostProcessing;
        Coroutine routine;
        float nextCheck;
        float lostSince = -1f;
        bool cameraSaved;
        bool rescanPending;

        void Awake()
        {
            // Mixed reality starts off, always: the game opens in the virtual room.
            SetPassthroughCamera(false);
            SetSession(false);
            if (walls != null && walls.Status != MRSurfaceStatus.Idle)
                walls.End();
        }

        void Start()
        {
            // For the logcat: what the device actually renders with (passthrough needs a colour buffer with alpha).
            var pipeline = GraphicsSettings.currentRenderPipeline;
            Debug.Log($"[MR] render pipeline '{(pipeline != null ? pipeline.name : "none")}', quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}', passthrough-capable device: {DeviceSupportsPassthrough()}");
        }

        // ---------- the commands ----------

        public void Toggle()
        {
            if (Mode == MRMode.Entering)
                return;
            if (Mode == MRMode.Mixed)
                LeaveMixed(MRProblem.None);
            else
                EnterMixed();
        }

        public void EnterMixed()
        {
            if (Mode != MRMode.Virtual)
                return;
            if (routine != null)
                StopCoroutine(routine);
            routine = StartCoroutine(EnterRoutine());
        }

        public void LeaveMixed() => LeaveMixed(MRProblem.None);

        /// <summary>Asks the Quest to run its room scan (Space Setup) from inside the app. False if it cannot.</summary>
        public bool RequestRoomScan()
        {
            if (!IsMixed || walls == null || !walls.RequestRoomScan())
                return false;
            // The app is suspended while the player scans; the walls are read again on return.
            rescanPending = true;
            return true;
        }

        // ---------- entering ----------

        IEnumerator EnterRoutine()
        {
            Problem = MRProblem.None;
            SetMode(MRMode.Entering);

            if (!DeviceSupportsPassthrough() || !HasPassthroughParts() || head == null)
            {
                Fail(MRProblem.Unsupported, "no passthrough camera subsystem on this device/build");
                yield break;
            }

            SetSession(true);
            var waited = 0f;
            while (SessionStarting() && waited < startTimeoutSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (SessionUnsupported(out var sessionState))
            {
                Fail(MRProblem.Unsupported, "AR session " + sessionState);
                yield break;
            }

            // Passthrough shows through wherever the eye buffer is transparent: transparent BLACK
            // (the compositor adds colour on top of what the passthrough layer shows).
            SaveCamera();
            head.clearFlags = CameraClearFlags.SolidColor;
            head.backgroundColor = new Color(0f, 0f, 0f, 0f);

            // The eye buffer must carry alpha for the passthrough to show through: an HDR colour buffer has
            // none, and post-processing (bloom, tonemapping...) writes opaque alpha. Both are the camera's
            // to switch off while mixed reality is on; the virtual room gets them back on the way out.
            head.allowHDR = false;
            var cameraData = head.GetUniversalAdditionalCameraData();
            if (cameraData != null)
                cameraData.renderPostProcessing = false;
            SetPassthroughCamera(true);

            waited = 0f;
            while (!PassthroughActive() && waited < startTimeoutSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!PassthroughActive())
            {
                SetPassthroughCamera(false);
                RestoreCamera();
                Fail(MRProblem.PassthroughFailed, $"passthrough did not start within {startTimeoutSeconds:F0}s (camera subsystem running: {SubsystemRunning()}, layer: {PassthroughLayerPresent()})");
                yield break;
            }

            // Only now does the virtual room go.
            ApplyMixedWorld();
            lostSince = -1f;
            SetMode(MRMode.Mixed);
            Debug.Log($"[MR] mixed reality on (passthrough verified: subsystem running + layer present)");
            if (walls != null)
                walls.Begin();
            routine = null;
        }

        void Fail(MRProblem problem, string detail)
        {
            Problem = problem;
            Debug.LogWarning($"[MR] mixed reality not entered: {problem} - {detail}");
            SetMode(MRMode.Virtual);
            routine = null;
        }

        // ---------- leaving ----------

        void LeaveMixed(MRProblem reason)
        {
            if (Mode == MRMode.Virtual && routine == null)
                return;
            if (routine != null)
            {
                StopCoroutine(routine);
                routine = null;
            }

            if (walls != null)
                walls.End();
            SetPassthroughCamera(false);
            // V0.15: the virtual room runs as it did before mixed reality was ever entered - no AR session.
            SetSession(false);
            RestoreCamera();
            ApplyVirtualWorld();
            Problem = reason;
            SetMode(MRMode.Virtual);
            if (reason != MRProblem.None)
                Debug.LogWarning($"[MR] back in the virtual room: {reason}");
            else
                Debug.Log("[MR] back in the virtual room");
        }

        // ---------- the worlds ----------

        void ApplyMixedWorld()
        {
            // The undo list belongs to the world it was made in.
            PaintHistory.ClearHistory();

            foreach (var go in virtualOnly)
            {
                if (go != null)
                    go.SetActive(false);
            }

            foreach (var renderer in hideRenderersInMixed)
            {
                if (renderer != null)
                    renderer.enabled = false;
            }

            // The tools stand where the player stands, facing where they face.
            if (layout != null && head != null)
            {
                var forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up);
                var yaw = forward.sqrMagnitude > 0.01f ? Quaternion.LookRotation(forward).eulerAngles.y : 0f;
                var floor = new Vector3(head.transform.position.x, 0f, head.transform.position.z);
                layout.PlaceToolsFor(floor, yaw);
            }
        }

        void ApplyVirtualWorld()
        {
            PaintHistory.ClearHistory();

            foreach (var go in virtualOnly)
            {
                if (go != null)
                    go.SetActive(true);
            }

            foreach (var renderer in hideRenderersInMixed)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }

            if (layout != null)
                layout.Recalibrate();
        }

        void SaveCamera()
        {
            if (cameraSaved || head == null)
                return;
            savedClearFlags = head.clearFlags;
            savedBackground = head.backgroundColor;
            savedAllowHdr = head.allowHDR;
            var data = head.GetUniversalAdditionalCameraData();
            savedPostProcessing = data != null && data.renderPostProcessing;
            cameraSaved = true;
        }

        void RestoreCamera()
        {
            if (!cameraSaved || head == null)
                return;
            head.clearFlags = savedClearFlags;
            head.backgroundColor = savedBackground;
            head.allowHDR = savedAllowHdr;
            var data = head.GetUniversalAdditionalCameraData();
            if (data != null)
                data.renderPostProcessing = savedPostProcessing;
            cameraSaved = false;
        }

        // ---------- is passthrough really on? ----------

#if FREEHAND_MR
        /// <summary>Whether this build, on this device, has a passthrough camera subsystem to start.</summary>
        public static bool DeviceSupportsPassthrough()
        {
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            var loader = manager != null ? manager.activeLoader : null;
            return loader != null && loader.GetLoadedSubsystem<XRCameraSubsystem>() != null;
        }

        bool HasPassthroughParts() => cameraManager != null && session != null;

        void SetPassthroughCamera(bool on)
        {
            if (cameraManager != null && cameraManager.enabled != on)
                cameraManager.enabled = on;
        }

        void SetSession(bool on)
        {
            if (session != null && session.enabled != on)
                session.enabled = on;
        }

        static bool SessionStarting() => ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.CheckingAvailability;

        static bool SessionUnsupported(out string state)
        {
            state = ARSession.state.ToString();
            return ARSession.state == ARSessionState.Unsupported || ARSession.state == ARSessionState.NeedsInstall;
        }

        bool SubsystemRunning() => cameraManager != null && cameraManager.enabled && cameraManager.subsystem != null && cameraManager.subsystem.running;

        static bool PassthroughLayerPresent()
        {
            var manager = CompositionLayerManager.Instance;
            var layers = manager != null ? manager.CompositionLayers : null;
            if (layers == null)
                return false;

            foreach (var layer in layers)
            {
                if (layer != null && layer.isActiveAndEnabled && layer.LayerData is PassthroughLayerData)
                    return true;
            }

            return false;
        }
#else
        // Built without the MR packages: there is no passthrough to start. Mixed reality reports "not available".
        public static bool DeviceSupportsPassthrough() => false;
        bool HasPassthroughParts() => false;
        void SetPassthroughCamera(bool on) { }
        void SetSession(bool on) { }
        static bool SessionStarting() => false;
        static bool SessionUnsupported(out string state) { state = "no MR packages"; return true; }
        bool SubsystemRunning() => false;
        static bool PassthroughLayerPresent() => false;
#endif

        /// <summary>Passthrough is on: the camera subsystem is running AND the passthrough layer is in the composition. A mode flag alone proves nothing.</summary>
        public bool PassthroughActive() => SubsystemRunning() && PassthroughLayerPresent();

        void Update()
        {
            if (Mode != MRMode.Mixed || Time.unscaledTime < nextCheck)
                return;
            nextCheck = Time.unscaledTime + checkInterval;

            if (PassthroughActive())
            {
                lostSince = -1f;
                return;
            }

            if (lostSince < 0f)
                lostSince = Time.unscaledTime;
            else if (Time.unscaledTime - lostSince > lostGraceSeconds)
                LeaveMixed(MRProblem.PassthroughLost);
        }

        // Back from the Quest menu, the dock or a room scan: nothing is assumed.
        void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                if (hub != null)
                    hub.SaveAll();
                return;
            }

            if (Mode == MRMode.Mixed)
            {
                nextCheck = 0f;
                lostSince = Time.unscaledTime;
                if (!PassthroughActive())
                {
                    // One restart of the passthrough camera; if it still is not there, Update returns to the virtual room.
                    SetPassthroughCamera(false);
                    SetPassthroughCamera(true);
                }

                if (rescanPending && walls != null)
                {
                    rescanPending = false;
                    walls.Begin();
                }
            }
        }

        void SetMode(MRMode mode)
        {
            Mode = mode;
            Changed?.Invoke();
        }

        void OnDestroy()
        {
            if (hub != null)
                hub.SaveAll();
        }
    }
}
