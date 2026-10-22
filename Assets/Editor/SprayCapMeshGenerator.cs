using System.Collections.Generic;
using UnityEngine;

// SM_Spraybottle.fbx models the top of the can as ONE mesh, 'sm_Nozzle1',
// that actually depicts two real parts (read from the FBX's own vertex
// profile, not guessed - see DEVELOPMENT_LOG.md, V0.8.x):
//
//   0% - 33% of its height : a collar ring (r = 1.92) - the valve mounting
//                            cup, part of the can, never removed.
//  33% - 37%               : a ledge stepping in to r = 1.60.
//  37% - 100%              : the actuator (r 1.60 -> 1.34). On a real
//                            aerosol this actuator IS the swappable "cap".
//
// This splits the mesh at the ledge so the collar stays on the can and the
// actuator becomes the Standard cap object, closing each part's newly open
// side with a flat disc so neither looks hollow once separated.
//
// Actuator features (V0.9, checked against the model's geometry AND its
// textures - the V0.8.x reading had them backwards):
//
//   Finger scoop - the top is cut down on one side (rim 1.14 there vs 1.67
//     opposite): the sloped pad the index finger presses. It faces the
//     finger, i.e. away from the spray.
//   Orifice - the bore is drawn in the model's normal map, not modelled: a
//     ring with a centre dot on the actuator's side wall, on the side
//     OPPOSITE the scoop, at ~48% of the actuator's height. Its centre is at
//     OrificeUv in the texture atlas; it is mapped onto the mesh through the
//     mesh's own UVs (barycentric on the triangle that contains it), so the
//     point lies exactly on the side face and the spray leaves where the
//     texture shows the hole. The earlier version took the scoop for the
//     spout, which put the orifice and the finger pad facing the player.
public static class SprayCapMeshGenerator
{
    public const float SplitFraction = 0.35f;

    // Centre of the nozzle bore disc in LR_SprayCan_phong2_Normal.1001.png
    // (UV atlas of the actuator's side strip), measured from the map's own
    // pixels - see DEVELOPMENT_LOG.md, V0.9.
    public static readonly Vector2 OrificeUv = new Vector2(0.8155f, 0.8893f);

    // Where on the scoop the finger pad rests: this fraction of the way from
    // the actuator's axis to its rim, toward the scoop.
    public const float FingerPadRadiusFraction = 0.45f;

    public class NozzleParts
    {
        public Mesh Collar;

        // Actuator, still in the source mesh's space: the caller transforms
        // it into the cap's own frame (it knows the instance's scale/yaw).
        public List<Vector3> ActuatorPositions = new List<Vector3>();
        public List<Vector3> ActuatorNormals = new List<Vector3>();
        public List<Vector4> ActuatorTangents = new List<Vector4>();
        public List<Vector2> ActuatorUvs = new List<Vector2>();
        public List<int> ActuatorTriangles = new List<int>();

        public Vector3 Seat;

        /// <summary>Horizontal, from the actuator's axis toward the finger scoop.</summary>
        public Vector3 ScoopDirection;
        public float ScoopDepthFraction;

        /// <summary>Point on the actuator's side wall where the bore is drawn, and the spray direction from it (horizontal, outward).</summary>
        public Vector3 Orifice;
        public Vector3 OrificeDirection;

        /// <summary>Point on the scoop's surface where the finger pad presses.</summary>
        public Vector3 FingerPad;
    }

    public static NozzleParts Split(Mesh source, out string error)
    {
        error = null;
        if (source == null)
        {
            error = "mesh ausente";
            return null;
        }

        if (!source.isReadable)
        {
            error = $"'{source.name}' nao esta Read/Write Enabled - o ModelImporter precisa de isReadable=true.";
            return null;
        }

        var positions = source.vertices;
        var normals = source.normals;
        var tangents = source.tangents;
        var uvs = source.uv;
        var triangles = source.triangles;
        var hasTangents = tangents != null && tangents.Length == positions.Length;
        var hasUvs = uvs != null && uvs.Length == positions.Length;

        var bounds = source.bounds;
        var splitY = bounds.min.y + SplitFraction * bounds.size.y;
        var epsilon = bounds.size.y * 0.004f;

        var collar = new MeshPart();
        var actuator = new MeshPart();

        for (var t = 0; t < triangles.Length; t += 3)
        {
            var a = triangles[t];
            var b = triangles[t + 1];
            var c = triangles[t + 2];
            var allAbove = positions[a].y >= splitY && positions[b].y >= splitY && positions[c].y >= splitY;
            var target = allAbove ? actuator : collar;
            target.AddTriangle(a, b, c, positions, normals, hasTangents ? tangents : null, hasUvs ? uvs : null);
        }

        if (actuator.Positions.Count == 0 || collar.Positions.Count == 0)
        {
            error = $"split em {SplitFraction:P0} da altura nao separou colar e atuador (colar={collar.Positions.Count} verts, atuador={actuator.Positions.Count}).";
            return null;
        }

        // Actuator frame.
        var actuatorMinY = float.MaxValue;
        var actuatorMaxY = float.MinValue;
        foreach (var p in actuator.Positions)
        {
            actuatorMinY = Mathf.Min(actuatorMinY, p.y);
            actuatorMaxY = Mathf.Max(actuatorMaxY, p.y);
        }

        var bottomRing = UniqueRing(actuator.Positions, actuatorMinY, epsilon);
        var center = Centroid(bottomRing);
        center.y = actuatorMinY;

        var parts = new NozzleParts { Seat = center };

        // Finger scoop: the lowest vertex of the top rim.
        var rimThreshold = actuatorMinY + 0.6f * (actuatorMaxY - actuatorMinY);
        var scoop = Vector3.zero;
        var scoopY = float.MaxValue;
        var rimTopY = float.MinValue;
        var rimRadius = 0f;
        foreach (var p in actuator.Positions)
        {
            if (p.y < rimThreshold)
                continue;

            rimTopY = Mathf.Max(rimTopY, p.y);
            rimRadius = Mathf.Max(rimRadius, new Vector2(p.x - center.x, p.z - center.z).magnitude);
            if (p.y < scoopY)
            {
                scoopY = p.y;
                scoop = p;
            }
        }

        var actuatorHeight = Mathf.Max(0.0001f, actuatorMaxY - actuatorMinY);
        parts.ScoopDepthFraction = (rimTopY - scoopY) / actuatorHeight;
        var scoopFlat = new Vector3(scoop.x - center.x, 0f, scoop.z - center.z);
        if (parts.ScoopDepthFraction < 0.1f || scoopFlat.sqrMagnitude < 0.0000001f)
        {
            error = $"rampa do dedo nao encontrada no topo do atuador (profundidade {parts.ScoopDepthFraction:P0}).";
            return null;
        }

        parts.ScoopDirection = scoopFlat.normalized;

        // Orifice: the bore drawn in the normal map, mapped onto the mesh.
        if (!hasUvs || !TryPointAtUv(actuator, OrificeUv, out var orifice))
        {
            error = $"o disco do orificio (UV {OrificeUv}) nao cai em nenhum triangulo do atuador - UVs do modelo mudaram?";
            return null;
        }

        var orificeFlat = new Vector3(orifice.x - center.x, 0f, orifice.z - center.z);
        parts.Orifice = orifice;
        parts.OrificeDirection = orificeFlat.normalized;
        if (Vector3.Dot(parts.OrificeDirection, parts.ScoopDirection) > -0.5f)
        {
            error = "orificio e rampa do dedo nao estao em lados opostos do atuador - modelo diferente do esperado.";
            return null;
        }

        // Finger pad: on the scoop's surface, between the axis and its rim.
        var padXZ = center + parts.ScoopDirection * (rimRadius * FingerPadRadiusFraction);
        if (!TryTopHeight(actuator, padXZ, out var padY))
        {
            error = "superficie da rampa do dedo nao encontrada.";
            return null;
        }

        parts.FingerPad = new Vector3(padXZ.x, padY, padXZ.z);

        // Close the actuator's bottom (faces -Y) and the collar's top (+Y).
        actuator.AddDisc(bottomRing, center, Vector3.down);
        var collarTopY = float.MinValue;
        foreach (var p in collar.Positions)
            collarTopY = Mathf.Max(collarTopY, p.y);
        var collarRing = UniqueRing(collar.Positions, collarTopY, epsilon);
        var collarCenter = Centroid(collarRing);
        collarCenter.y = collarTopY;
        collar.AddDisc(collarRing, collarCenter, Vector3.up);

        parts.Collar = collar.ToMesh("SprayCan_ValveCup");
        parts.ActuatorPositions = actuator.Positions;
        parts.ActuatorNormals = actuator.Normals;
        parts.ActuatorTangents = actuator.Tangents;
        parts.ActuatorUvs = actuator.Uvs;
        parts.ActuatorTriangles = actuator.Triangles;
        return parts;
    }

    // The point of the mesh whose texture coordinate is `uv` (barycentric on
    // the containing triangle).
    static bool TryPointAtUv(MeshPart part, Vector2 uv, out Vector3 point)
    {
        point = Vector3.zero;
        var tris = part.Triangles;
        for (var t = 0; t < tris.Count; t += 3)
        {
            var a = part.Uvs[tris[t]];
            var b = part.Uvs[tris[t + 1]];
            var c = part.Uvs[tris[t + 2]];
            var v0 = b - a;
            var v1 = c - a;
            var v2 = uv - a;
            var det = v0.x * v1.y - v1.x * v0.y;
            if (Mathf.Abs(det) < 1e-12f)
                continue;

            var s = (v2.x * v1.y - v1.x * v2.y) / det;
            var r = (v0.x * v2.y - v2.x * v0.y) / det;
            if (s < -1e-5f || r < -1e-5f || s + r > 1f + 1e-5f)
                continue;

            var pa = part.Positions[tris[t]];
            point = pa + (part.Positions[tris[t + 1]] - pa) * s + (part.Positions[tris[t + 2]] - pa) * r;
            return true;
        }

        return false;
    }

    // Height of the actuator's top surface above (x, z): the highest
    // upward-facing triangle containing that point in plan view.
    static bool TryTopHeight(MeshPart part, Vector3 xz, out float height)
    {
        height = float.MinValue;
        var tris = part.Triangles;
        for (var t = 0; t < tris.Count; t += 3)
        {
            var a = part.Positions[tris[t]];
            var b = part.Positions[tris[t + 1]];
            var c = part.Positions[tris[t + 2]];
            if (Mathf.Abs(Vector3.Cross(b - a, c - a).normalized.y) < 0.3f)
                continue;

            var v0 = new Vector2(b.x - a.x, b.z - a.z);
            var v1 = new Vector2(c.x - a.x, c.z - a.z);
            var v2 = new Vector2(xz.x - a.x, xz.z - a.z);
            var det = v0.x * v1.y - v1.x * v0.y;
            if (Mathf.Abs(det) < 1e-12f)
                continue;

            var s = (v2.x * v1.y - v1.x * v2.y) / det;
            var r = (v0.x * v2.y - v2.x * v0.y) / det;
            if (s < -1e-5f || r < -1e-5f || s + r > 1f + 1e-5f)
                continue;

            height = Mathf.Max(height, a.y + (b.y - a.y) * s + (c.y - a.y) * r);
        }

        return height > float.MinValue;
    }

    static List<Vector3> UniqueRing(List<Vector3> positions, float y, float epsilon)
    {
        var ring = new List<Vector3>();
        foreach (var p in positions)
        {
            if (Mathf.Abs(p.y - y) > epsilon)
                continue;

            var duplicate = false;
            foreach (var r in ring)
            {
                if ((r - p).sqrMagnitude < epsilon * epsilon)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                ring.Add(p);
        }

        return ring;
    }

    static Vector3 Centroid(List<Vector3> points)
    {
        var sum = Vector3.zero;
        foreach (var p in points)
            sum += p;
        return points.Count > 0 ? sum / points.Count : Vector3.zero;
    }

    class MeshPart
    {
        public readonly List<Vector3> Positions = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector4> Tangents = new List<Vector4>();
        public readonly List<Vector2> Uvs = new List<Vector2>();
        public readonly List<int> Triangles = new List<int>();

        readonly Dictionary<int, int> remap = new Dictionary<int, int>();

        public void AddTriangle(int a, int b, int c, Vector3[] positions, Vector3[] normals, Vector4[] tangents, Vector2[] uvs)
        {
            Triangles.Add(Map(a, positions, normals, tangents, uvs));
            Triangles.Add(Map(b, positions, normals, tangents, uvs));
            Triangles.Add(Map(c, positions, normals, tangents, uvs));
        }

        int Map(int index, Vector3[] positions, Vector3[] normals, Vector4[] tangents, Vector2[] uvs)
        {
            if (remap.TryGetValue(index, out var mapped))
                return mapped;

            mapped = Positions.Count;
            Positions.Add(positions[index]);
            Normals.Add(normals != null && normals.Length > index ? normals[index] : Vector3.up);
            Tangents.Add(tangents != null ? tangents[index] : new Vector4(1f, 0f, 0f, 1f));
            Uvs.Add(uvs != null ? uvs[index] : Vector2.zero);
            remap[index] = mapped;
            return mapped;
        }

        // Flat fan over a closed ring. Winding is chosen per triangle from
        // the requested normal (Unity front faces satisfy
        // Cross(b - a, c - a) . normal > 0), not assumed from ring order.
        public void AddDisc(List<Vector3> ring, Vector3 center, Vector3 normal)
        {
            if (ring.Count < 3)
                return;

            var reference = ring[0] - center;
            reference.y = 0f;
            ring.Sort((p, q) => SignedAngle(reference, p - center).CompareTo(SignedAngle(reference, q - center)));

            var uv = Uvs.Count > 0 ? Uvs[0] : Vector2.zero;
            var tangent = new Vector4(1f, 0f, 0f, 1f);

            var centerIndex = Positions.Count;
            Positions.Add(center);
            Normals.Add(normal);
            Tangents.Add(tangent);
            Uvs.Add(uv);

            var first = Positions.Count;
            foreach (var p in ring)
            {
                Positions.Add(p);
                Normals.Add(normal);
                Tangents.Add(tangent);
                Uvs.Add(uv);
            }

            for (var i = 0; i < ring.Count; i++)
            {
                var a = centerIndex;
                var b = first + i;
                var c = first + (i + 1) % ring.Count;
                if (Vector3.Dot(Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]), normal) < 0f)
                    (b, c) = (c, b);

                Triangles.Add(a);
                Triangles.Add(b);
                Triangles.Add(c);
            }
        }

        static float SignedAngle(Vector3 from, Vector3 to)
        {
            to.y = 0f;
            return Vector3.SignedAngle(from, to, Vector3.up);
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(Positions);
            mesh.SetNormals(Normals);
            mesh.SetTangents(Tangents);
            mesh.SetUVs(0, Uvs);
            mesh.SetTriangles(Triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
