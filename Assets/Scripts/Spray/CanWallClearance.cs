using UnityEngine;
using Freehand.VR;

namespace Freehand.Spray
{
    /// <summary>
    /// Keeps the can's BODY from clipping through a paintable wall while
    /// held, without limiting how close the NOZZLE can get - matching a real
    /// spray session, where the fist still bumps the wall well before the
    /// nozzle would, but the nozzle itself needs to reach right up to the
    /// surface to paint corners and edges.
    ///
    /// Deliberately does not touch VRGrabInteractable's attach math and does
    /// not clamp the hand/controller itself (the brief is explicit: protect
    /// the can body, don't just freeze hand movement). It only nudges the
    /// rendered can position back along the wall each frame, recomputed
    /// fresh every time from the grab's attach pose - never cumulative, so
    /// it cannot drift or fight the grab.
    ///
    /// The distance is measured to the wall's face along the face's normal,
    /// found with a ray toward the wall's centre - not Collider.ClosestPoint:
    /// paint walls are non-convex MeshColliders (PaintSurface needs
    /// RaycastHit.textureCoord), which ClosestPoint does not support. It
    /// returned the query point itself, so the push never happened and
    /// Unity logged a warning every frame.
    ///
    /// Known simplification: a body point that is already inside a wall (a
    /// very fast lunge) sees no front face and is not corrected.
    /// minClearanceMeters is enough buffer for how far a hand moves in one
    /// frame at VR frame rates, but that needs confirming on Quest.
    /// </summary>
    public class CanWallClearance : MonoBehaviour
    {
        [SerializeField] Transform sprayCan;
        [SerializeField] VRGrabInteractable grab;
        [SerializeField] Collider[] walls;

        [Tooltip("Reference point on the can BODY to keep clear of walls, in the can's own local space - deliberately not the nozzle tip.")]
        [SerializeField] Vector3 bodyLocalPoint = new Vector3(0f, 0.35f, 0f);

        [SerializeField] float minClearanceMeters = 0.05f;

        void Awake()
        {
            if (sprayCan == null)
                sprayCan = transform;
        }

        void LateUpdate()
        {
            if (sprayCan == null || grab == null || !grab.IsGrabbed || walls == null)
                return;

            // Start from the pose the grab attached, every frame. Adding the
            // push to the current position instead made each frame's push
            // permanent: after leaning on a wall the can stayed shifted in
            // the hand. Assumes sprayCan is the grabbed object itself.
            sprayCan.localPosition = grab.AttachedLocalPosition;

            var bodyPoint = sprayCan.TransformPoint(bodyLocalPoint);

            foreach (var wall in walls)
            {
                if (wall == null)
                    continue;

                // The ray toward the centre crosses the wall's nearest face;
                // that face's plane gives the exact distance for a flat wall.
                var toCenter = wall.bounds.center - bodyPoint;
                var reach = toCenter.magnitude;
                if (reach < 0.0001f || !wall.Raycast(new Ray(bodyPoint, toCenter / reach), out var hit, reach))
                    continue;

                var distance = Vector3.Dot(bodyPoint - hit.point, hit.normal);
                if (distance >= minClearanceMeters || distance <= 0f)
                    continue;

                var push = hit.normal * (minClearanceMeters - distance);
                sprayCan.position += push;
                bodyPoint += push;
            }
        }
    }
}
