using System.Collections.Generic;
using UnityEngine;

namespace Freehand.Paint
{
    /// <summary>
    /// Routing layer between SprayEmitter and whatever paintable surface was
    /// hit. Kept static/stateless from the scene's point of view - no wiring.
    ///
    /// It owns one piece of per-stroke state: the previous hit UV, so paint is
    /// drawn as a continuous segment instead of isolated dots. Without this the
    /// spray leaves visible gaps whenever the hand moves faster than the paint
    /// tick, which is most of the time.
    /// </summary>
    public static class PaintManager
    {
        // Where each spray source's last hit landed (V0.12: there can be a
        // can in each hand - their lines must not join each other's).
        sealed class Segment
        {
            public PaintSurface Surface;
            public Vector2 Uv;
            public bool HasPoint;

            public void Break()
            {
                HasPoint = false;
                Surface = null;
            }
        }

        static readonly Dictionary<object, Segment> Segments = new Dictionary<object, Segment>();
        static readonly Segment Shared = new Segment();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Segments.Clear();
            Shared.Break();
        }

        static Segment For(object source)
        {
            if (source == null)
                return Shared;
            if (!Segments.TryGetValue(source, out var segment))
                Segments[source] = segment = new Segment();
            return segment;
        }

        public static void BeginStroke(object source = null)
        {
            For(source).Break();
            PaintHistory.BeginStroke();
        }

        public static void EndStroke(object source = null)
        {
            For(source).Break();
            PaintHistory.EndStroke();
        }

        /// <summary>The spray paused inside one stroke (a flick of the trigger): the next hit starts a new segment instead of drawing a line across the gap, but stays in the same undoable stroke.</summary>
        public static void BreakSegment(object source = null) => For(source).Break();

        public static void ApplyPaint(RaycastHit hit, Vector3 rayDirection, SprayStamp stamp, object source = null)
        {
            var segment = For(source);
            var surface = hit.collider.GetComponentInParent<PaintSurface>();
            if (surface == null)
            {
                // Leaving the wall breaks segment continuity - otherwise the
                // next hit would draw a line across everything in between.
                segment.HasPoint = false;
                return;
            }

            PaintHistory.RecordTargetIfNeeded(surface);
            ApplyIncidenceStretch(surface, hit, rayDirection, ref stamp);
            ApplyDownhill(surface, hit, ref stamp);

            var uv = hit.textureCoord;
            var from = segment.HasPoint && surface == segment.Surface ? segment.Uv : uv;
            surface.PaintSegment(from, uv, in stamp);
            PaintHistory.RecordPoint(hit.point, stamp.color, stamp.capName, surface.SurfaceId);

            segment.Surface = surface;
            segment.Uv = uv;
            segment.HasPoint = true;
        }

        // Spraying a wall at an angle lays paint over a longer, thinner patch
        // than spraying it head-on. 1/cos(incidence) is the exact ratio, capped
        // so a near-parallel hit cannot smear across the whole wall. The
        // elongation's direction is taken to UV space through the hit
        // triangle's own UVs (PaintSurface.TryWorldToUv) - not an assumed
        // local-axes-to-UV mapping, which is mirrored on some faces of a cube.
        static void ApplyIncidenceStretch(PaintSurface surface, RaycastHit hit, Vector3 rayDirection, ref SprayStamp stamp)
        {
            var normal = hit.normal;
            var cosAngle = Mathf.Abs(Vector3.Dot(rayDirection.normalized, normal));
            if (cosAngle < 0.001f)
                cosAngle = 0.001f;

            stamp.stretch = Mathf.Clamp(1f / cosAngle, 1f, 3f);

            var planar = rayDirection - normal * Vector3.Dot(rayDirection, normal);
            if (planar.sqrMagnitude < 0.000001f || !surface.TryWorldToUv(hit.triangleIndex, planar.normalized, out var uvDirection) || uvDirection.sqrMagnitude < 1e-12f)
            {
                stamp.stretchAngle = 0f;
                return;
            }

            stamp.stretchAngle = Mathf.Atan2(uvDirection.y, uvDirection.x);
        }

        // Where paint runs from this impact: real gravity along the surface,
        // expressed in the surface's UVs at the hit. Drips follow it; on a
        // floor or ceiling (no gravity along the surface) paint pools.
        static void ApplyDownhill(PaintSurface surface, RaycastHit hit, ref SprayStamp stamp)
        {
            var gravity = Physics.gravity.sqrMagnitude > 0f ? Physics.gravity.normalized : Vector3.down;
            var along = gravity - hit.normal * Vector3.Dot(gravity, hit.normal);
            stamp.slope = along.magnitude;
            if (stamp.slope < 0.0001f || !surface.TryWorldToUv(hit.triangleIndex, along / stamp.slope, out stamp.downhillUv))
            {
                stamp.slope = 0f;
                stamp.downhillUv = Vector2.zero;
            }
        }

        public static bool Undo() => PaintHistory.UndoLast();

        /// <summary>Clears every paintable surface as one undoable action. False if there was no paint.</summary>
        public static bool ClearAll()
        {
            foreach (var segment in Segments.Values)
                segment.Break();
            Shared.Break();
            return PaintHistory.ClearAll();
        }
    }
}
