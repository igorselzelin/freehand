using System.Collections.Generic;
using UnityEngine;

namespace Freehand.VR
{
    /// <summary>
    /// The top of a small mesh as seen from above its own +Y: a grid of
    /// surface heights, rasterized once from the mesh's triangles. What a
    /// fingertip coming down onto it meets - the actuator of a spray cap,
    /// scoop included - without modelling the part as a primitive.
    ///
    /// Gap() treats everything under that surface as solid, so a finger
    /// beside the part (not only over it) is kept out of it too. Cached per
    /// mesh: every cap that shares a model shares one field.
    /// </summary>
    public sealed class SurfaceHeightField
    {
        const int Resolution = 20;

        static readonly Dictionary<Mesh, SurfaceHeightField> Cache = new Dictionary<Mesh, SurfaceHeightField>();

        readonly float[] heights = new float[Resolution * Resolution];
        readonly Vector2 origin;
        readonly float cell;
        readonly Vector2 center;
        readonly float footprint;

        /// <summary>Highest point of the surface, in the mesh's own space.</summary>
        public float Top { get; }

        /// <summary>The field for this mesh, or null if its vertices cannot be read.</summary>
        public static SurfaceHeightField For(Mesh mesh)
        {
            if (mesh == null)
                return null;

            if (Cache.TryGetValue(mesh, out var field))
                return field;

            if (!mesh.isReadable)
            {
                Debug.LogError($"{nameof(SurfaceHeightField)}: mesh '{mesh.name}' is not readable - fingers cannot find its surface.");
                Cache[mesh] = null;
                return null;
            }

            field = new SurfaceHeightField(mesh);
            Cache[mesh] = field;
            return field;
        }

        SurfaceHeightField(Mesh mesh)
        {
            var bounds = mesh.bounds;
            var size = Mathf.Max(bounds.size.x, bounds.size.z) * 1.05f;
            center = new Vector2(bounds.center.x, bounds.center.z);
            origin = center - Vector2.one * (size * 0.5f);
            cell = size / (Resolution - 1);
            for (var i = 0; i < heights.Length; i++)
                heights[i] = float.NaN;

            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (var t = 0; t < triangles.Length; t += 3)
                Rasterize(vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]);

            // Vertical faces cover no area from above: their edges still count.
            foreach (var v in vertices)
                Raise(Mathf.RoundToInt((v.x - origin.x) / cell), Mathf.RoundToInt((v.z - origin.y) / cell), v.y);

            var top = float.NegativeInfinity;
            for (var j = 0; j < Resolution; j++)
            {
                for (var i = 0; i < Resolution; i++)
                {
                    var h = heights[j * Resolution + i];
                    if (float.IsNaN(h))
                        continue;

                    top = Mathf.Max(top, h);
                    footprint = Mathf.Max(footprint, (CellCenter(i, j) - center).magnitude + cell);
                }
            }

            Top = top;
        }

        Vector2 CellCenter(int i, int j) => origin + new Vector2(i, j) * cell;

        void Raise(int i, int j, float y)
        {
            if (i < 0 || j < 0 || i >= Resolution || j >= Resolution)
                return;

            var index = j * Resolution + i;
            if (float.IsNaN(heights[index]) || y > heights[index])
                heights[index] = y;
        }

        void Rasterize(Vector3 a, Vector3 b, Vector3 c)
        {
            var pa = new Vector2(a.x, a.z);
            var pb = new Vector2(b.x, b.z);
            var pc = new Vector2(c.x, c.z);
            var area = (pb.x - pa.x) * (pc.y - pa.y) - (pc.x - pa.x) * (pb.y - pa.y);
            if (Mathf.Abs(area) < 1e-12f)
                return;

            var i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x)) - origin.x) / cell));
            var i1 = Mathf.Min(Resolution - 1, Mathf.CeilToInt((Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x)) - origin.x) / cell));
            var j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y)) - origin.y) / cell));
            var j1 = Mathf.Min(Resolution - 1, Mathf.CeilToInt((Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y)) - origin.y) / cell));

            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var p = CellCenter(i, j);
                    var w1 = ((pb.x - p.x) * (pc.y - p.y) - (pc.x - p.x) * (pb.y - p.y)) / area;
                    var w2 = ((pc.x - p.x) * (pa.y - p.y) - (pa.x - p.x) * (pc.y - p.y)) / area;
                    var w3 = 1f - w1 - w2;
                    if (w1 < -1e-4f || w2 < -1e-4f || w3 < -1e-4f)
                        continue;

                    Raise(i, j, w1 * a.y + w2 * b.y + w3 * c.y);
                }
            }
        }

        /// <summary>Surface height at a point seen from above (the mesh's own space), NaN where there is no surface.</summary>
        public float HeightAt(float x, float z)
        {
            var i = Mathf.RoundToInt((x - origin.x) / cell);
            var j = Mathf.RoundToInt((z - origin.y) / cell);
            return i < 0 || j < 0 || i >= Resolution || j >= Resolution ? float.NaN : heights[j * Resolution + i];
        }

        /// <summary>
        /// Clearance of a sphere (centre and radius in the mesh's own space) from the solid under the
        /// surface: negative = that deep in it; positive = that far from it - exact over the part,
        /// estimated from the part's outline when the sphere is beside or away from it (so a solver
        /// still knows which way the part is).
        /// </summary>
        public float Gap(Vector3 point, float radius)
        {
            var flat = new Vector2(point.x, point.z);
            var outward = (flat - center).magnitude - footprint;
            if (outward > radius)
                return Outside(outward, point.y, radius);

            var i0 = Mathf.Max(0, Mathf.FloorToInt((flat.x - radius - origin.x) / cell));
            var i1 = Mathf.Min(Resolution - 1, Mathf.CeilToInt((flat.x + radius - origin.x) / cell));
            var j0 = Mathf.Max(0, Mathf.FloorToInt((flat.y - radius - origin.y) / cell));
            var j1 = Mathf.Min(Resolution - 1, Mathf.CeilToInt((flat.y + radius - origin.y) / cell));
            var radiusSq = radius * radius;
            var best = float.PositiveInfinity;

            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var h = heights[j * Resolution + i];
                    if (float.IsNaN(h))
                        continue;

                    var d = CellCenter(i, j) - flat;
                    var dSq = d.sqrMagnitude;
                    if (dSq >= radiusSq)
                        continue;

                    best = Mathf.Min(best, point.y - Mathf.Sqrt(radiusSq - dSq) - h);
                }
            }

            // Nothing of the part under the sphere: it is beside it, not on it.
            return float.IsPositiveInfinity(best) ? Mathf.Max(0.0005f, Outside(Mathf.Max(0f, outward), point.y, radius)) : best;
        }

        // Distance from a sphere to the part's outline (a cylinder of the
        // footprint's radius up to Top).
        float Outside(float horizontal, float y, float radius)
        {
            var vertical = Mathf.Max(0f, y - Top);
            return Mathf.Sqrt(horizontal * horizontal + vertical * vertical) - radius;
        }
    }
}
