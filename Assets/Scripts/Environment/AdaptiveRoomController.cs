using System.Collections.Generic;
using Freehand.VR;
using UnityEngine;
using UnityEngine.XR;

namespace Freehand.Environment
{
    /// <summary>
    /// Fits the practice room to the player's real physical space by
    /// repositioning/rescaling the room's existing props (floor, walls, paint
    /// wall, palette, tool table with the can and cap rack). It never creates a second room -
    /// V04SceneBuilder builds the geometry once; this only moves it.
    ///
    /// Tries the Guardian/boundary polygon through XRInputSubsystem
    /// (TryGetBoundaryPoints) to classify the space into Compact/Standard/
    /// Large. If no subsystem reports a boundary - common outside a real
    /// headset, and possibly on some OpenXR runtimes even on-device, since
    /// boundary exposure is provider-dependent - it falls back to
    /// `fallbackMode`, which defaults to Compact: this project's primary use
    /// case is a small room, so "no data" should mean "assume small," not
    /// "assume the old, much larger default."
    ///
    /// Runs once at Start and again only through an explicit Recalibrate()
    /// call (e.g. a future in-game button) - boundary geometry does not
    /// change mid-session, so there is no per-frame work here.
    /// </summary>
    public class AdaptiveRoomController : MonoBehaviour
    {
        [SerializeField] RoomSizeMode fallbackMode = RoomSizeMode.Compact;
        [SerializeField] bool autoDetectFromBoundary = true;
        [SerializeField] bool showDebugGizmos;

        [Header("Room shell")]
        [SerializeField] Transform floor;
        [SerializeField] Transform ceiling;
        [SerializeField] Transform wallLeft;
        [SerializeField] Transform wallRight;
        [SerializeField] Transform wallBack;
        [SerializeField] Transform paintWall;

        [Header("Props")]
        [UnityEngine.Serialization.FormerlySerializedAs("canPedestal")]
        [SerializeField] Transform toolTable;
        [SerializeField] Transform sprayCan;
        [SerializeField] VRGrabInteractable sprayCanGrab;
        [SerializeField] Transform colorPaletteRoot;
        [SerializeField] Transform capRackRoot;

        [Tooltip("The can workshop (V0.12): moved with the room, behind the player.")]
        [SerializeField] Transform workshopRoot;

        [Tooltip("The mode panel (V0.13): beside the tool table.")]
        [SerializeField] Transform mrPanelRoot;

        [Tooltip("Reference for 'player center' - the XR Origin. Left null, world origin is used (correct for this project: Tracking Origin Mode is Floor, no room offset).")]
        [SerializeField] Transform playerOrigin;

        const float FloorThickness = 0.1f;
        const float CeilingThickness = 0.1f;
        const float CeilingGap = 0.05f;
        const float WallThickness = 0.2f;

        public RoomSizeMode CurrentMode { get; private set; }
        public bool LastDetectionSucceeded { get; private set; }
        public Vector2 LastDetectedSize { get; private set; }

        readonly List<Vector3> boundaryPointsBuffer = new List<Vector3>();
        readonly List<XRInputSubsystem> inputSubsystemsBuffer = new List<XRInputSubsystem>();
        Vector3[] lastBoundaryWorldPoints = System.Array.Empty<Vector3>();

        void Start()
        {
            Recalibrate();
        }

        /// <summary>Re-detects the play space (if enabled) and re-applies the resulting layout. Cheap enough to call on demand, but deliberately not called every frame.</summary>
        public void Recalibrate()
        {
            var mode = fallbackMode;
            LastDetectionSucceeded = false;

            if (autoDetectFromBoundary && TryDetectBoundarySize(out var size))
            {
                LastDetectedSize = size;
                LastDetectionSucceeded = true;
                mode = Classify(size);
            }

            Apply(RoomLayoutProfile.ForMode(mode));
            CurrentMode = mode;

            Debug.Log($"[AdaptiveRoomController] mode={mode} (boundary detected={LastDetectionSucceeded}" +
                (LastDetectionSucceeded ? $", size={LastDetectedSize.x:F2}x{LastDetectedSize.y:F2}m" : "") + ")");
        }

        /// <summary>
        /// V0.13 (mixed reality): puts the tools - table, cap rack, colour palette, the mode panel and, if it is
        /// free, the can - where the current layout puts them for a player standing at `floorPosition` and
        /// facing `yawDegrees`, instead of around the tracking origin. The shell and the workshop are not touched
        /// (they are not shown in mixed reality). Recalibrate() puts everything back for the virtual room.
        /// </summary>
        public void PlaceToolsFor(Vector3 floorPosition, float yawDegrees)
        {
            var profile = RoomLayoutProfile.ForMode(CurrentMode);
            var turn = Quaternion.Euler(0f, yawDegrees, 0f);
            Vector3 World(Vector3 local) => floorPosition + turn * local;

            if (toolTable != null)
            {
                toolTable.SetPositionAndRotation(World(profile.ToolTableCenter(Vector3.zero)), turn);
                toolTable.localScale = profile.toolTableSize;
            }

            if (capRackRoot != null)
                capRackRoot.SetPositionAndRotation(World(profile.CapRackPosition(Vector3.zero)), turn * profile.CapRackRotation(Vector3.zero));

            if (colorPaletteRoot != null)
            {
                var angleRad = profile.paletteAngleDegrees * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Sin(angleRad) * profile.paletteDistance, 0f, Mathf.Cos(angleRad) * profile.paletteDistance);
                colorPaletteRoot.SetPositionAndRotation(World(offset), turn * Quaternion.Euler(0f, profile.paletteAngleDegrees, 0f));
            }

            if (mrPanelRoot != null)
                mrPanelRoot.SetPositionAndRotation(World(profile.MRPanelPosition(Vector3.zero)), turn * profile.MRPanelRotation(Vector3.zero));

            var canIsFree = (sprayCanGrab == null || !sprayCanGrab.IsGrabbed) && (sprayCan == null || sprayCan.parent == null);
            if (sprayCan != null && canIsFree)
                sprayCan.SetPositionAndRotation(World(profile.CanRestPosition(Vector3.zero)), turn);
        }

        static RoomSizeMode Classify(Vector2 size)
        {
            var smaller = Mathf.Min(size.x, size.y);
            if (smaller < 2.0f) return RoomSizeMode.Compact;
            if (smaller < 3.5f) return RoomSizeMode.Standard;
            return RoomSizeMode.Large;
        }

        // XRInputSubsystem.TryGetBoundaryPoints reports the Guardian/boundary
        // polygon in tracking space when the runtime exposes one. Whether
        // Meta's OpenXR runtime actually surfaces this through Unity's XR
        // subsystem (rather than only through Meta's own SDK) is exactly the
        // part that cannot be confirmed without a physical headset - hence the
        // fallback path existing at all.
        bool TryDetectBoundarySize(out Vector2 size)
        {
            size = Vector2.zero;

            // GetInstances<T> looked like the modern name at a glance, but
            // current Unity docs mark it obsolete in favor of this generic
            // GetSubsystems<T>(List<T>) - confirmed against the live
            // ScriptReference page rather than assumed from memory.
            SubsystemManager.GetSubsystems(inputSubsystemsBuffer);
            foreach (var subsystem in inputSubsystemsBuffer)
            {
                if (subsystem == null || !subsystem.running)
                    continue;

                boundaryPointsBuffer.Clear();
                if (!subsystem.TryGetBoundaryPoints(boundaryPointsBuffer) || boundaryPointsBuffer.Count < 3)
                    continue;

                var min = boundaryPointsBuffer[0];
                var max = boundaryPointsBuffer[0];
                foreach (var p in boundaryPointsBuffer)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                size = new Vector2(max.x - min.x, max.z - min.z);
                lastBoundaryWorldPoints = boundaryPointsBuffer.ToArray();
                return size.x > 0.01f && size.y > 0.01f;
            }

            lastBoundaryWorldPoints = System.Array.Empty<Vector3>();
            return false;
        }

        void Apply(RoomLayoutProfile profile)
        {
            var center = playerOrigin != null ? playerOrigin.position : Vector3.zero;

            SetShellBox(floor, center, new Vector3(0f, -FloorThickness * 0.5f, 0f),
                new Vector3(profile.roomHalfWidth * 2f, FloorThickness, profile.roomHalfDepth + profile.wallDistance));

            SetShellBox(ceiling, center, new Vector3(0f, profile.wallHeight + CeilingGap, 0f),
                new Vector3(profile.roomHalfWidth * 2f, CeilingThickness, profile.roomHalfDepth + profile.wallDistance));

            SetShellBox(wallLeft, center, new Vector3(-profile.roomHalfWidth, profile.wallHeight * 0.5f, 0f),
                new Vector3(WallThickness, profile.wallHeight, profile.roomHalfDepth + profile.wallDistance));

            SetShellBox(wallRight, center, new Vector3(profile.roomHalfWidth, profile.wallHeight * 0.5f, 0f),
                new Vector3(WallThickness, profile.wallHeight, profile.roomHalfDepth + profile.wallDistance));

            SetShellBox(wallBack, center, new Vector3(0f, profile.wallHeight * 0.5f, -profile.roomHalfDepth),
                new Vector3(profile.roomHalfWidth * 2f, profile.wallHeight, WallThickness));

            SetShellBox(paintWall, center, new Vector3(0f, profile.wallHeight * 0.5f, profile.wallDistance),
                new Vector3(profile.wallWidth, profile.wallHeight, WallThickness));

            PlaceOnRing(colorPaletteRoot, center, profile.paletteDistance, profile.paletteAngleDegrees);

            // The table is a cube: position is its centre (half its height
            // above the floor), scale its full size. The previous pedestal
            // code put the centre at floor level, sinking half of it.
            if (toolTable != null)
            {
                toolTable.position = profile.ToolTableCenter(center);
                toolTable.localScale = profile.toolTableSize;
            }

            if (capRackRoot != null)
                capRackRoot.SetPositionAndRotation(profile.CapRackPosition(center), profile.CapRackRotation(center));

            if (workshopRoot != null)
                workshopRoot.SetPositionAndRotation(profile.WorkshopPosition(center), RoomLayoutProfile.WorkshopRotation);

            if (mrPanelRoot != null)
                mrPanelRoot.SetPositionAndRotation(profile.MRPanelPosition(center), profile.MRPanelRotation(center));

            // Never yank the can out from under the player's own hand - only
            // reposition it while it is resting on the table (not in a hand,
            // not set on the workshop's turntable).
            var canIsFree = (sprayCanGrab == null || !sprayCanGrab.IsGrabbed) && (sprayCan == null || sprayCan.parent == null);
            if (sprayCan != null && canIsFree)
                sprayCan.SetPositionAndRotation(profile.CanRestPosition(center), Quaternion.identity);
        }

        // Walls/floor/ceiling are cubes: position is their center, scale is
        // their full size along each axis - matches how V04SceneBuilder's
        // MakeBox already builds them, so this only ever repositions/rescales
        // the same objects, never creates new ones.
        static void SetShellBox(Transform box, Vector3 center, Vector3 localOffset, Vector3 size)
        {
            if (box == null)
                return;

            box.position = center + localOffset;
            box.localScale = size;
        }

        // The palette sits on a ring around the player at a given distance and
        // angle from forward (+Z), its front (-Z) turned to face the player -
        // the same placement V04SceneBuilder computes, so Compact/Standard/
        // Large only ever differ by the profile's numbers. (Until V0.9 this
        // was Euler(0, -angle, 0), which turned the rack's long side toward
        // the player, its nearest swatch ~25cm from them.)
        static void PlaceOnRing(Transform target, Vector3 center, float distance, float angleDegrees)
        {
            if (target == null)
                return;

            var angleRad = angleDegrees * Mathf.Deg2Rad;
            var offset = new Vector3(Mathf.Sin(angleRad) * distance, 0f, Mathf.Cos(angleRad) * distance);

            target.position = center + offset;
            target.rotation = Quaternion.Euler(0f, angleDegrees, 0f);
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (!showDebugGizmos)
                return;

            var center = playerOrigin != null ? playerOrigin.position : Vector3.zero;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(center, 0.05f);

            // Before Recalibrate() has ever run (edit-time preview, not
            // playing), show what the fallback would draw rather than an
            // uninitialized CurrentMode.
            var previewMode = Application.isPlaying ? CurrentMode : fallbackMode;
            var profile = RoomLayoutProfile.ForMode(previewMode);
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.6f);
            Gizmos.DrawWireSphere(center, profile.minimumComfortRadius);

            if (lastBoundaryWorldPoints.Length >= 2)
            {
                Gizmos.color = Color.green;
                for (var i = 0; i < lastBoundaryWorldPoints.Length; i++)
                {
                    var a = lastBoundaryWorldPoints[i];
                    var b = lastBoundaryWorldPoints[(i + 1) % lastBoundaryWorldPoints.Length];
                    Gizmos.DrawLine(a, b);
                }
            }

            UnityEditor.Handles.color = Color.white;
            UnityEditor.Handles.Label(center + Vector3.up * 1.9f,
                $"AdaptiveRoom: {CurrentMode}\nboundary={(LastDetectionSucceeded ? $"{LastDetectedSize.x:F2}x{LastDetectedSize.y:F2}m" : "unavailable (fallback)")}");
        }
#endif
    }
}
