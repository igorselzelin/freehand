using Freehand.Paint;
using UnityEngine;

namespace Freehand.MR
{
    public enum MRSurfaceState
    {
        /// <summary>The wall is being tracked: it can be painted.</summary>
        Tracking,

        /// <summary>The wall is no longer tracked (or no longer reported): its paint is hidden and it takes none until it is found again.</summary>
        Lost,
    }

    /// <summary>
    /// One real wall as a paintable surface. It is a thin quad - exactly the size and
    /// pose the Quest reports for the wall - carrying the ordinary PaintSurface (in its
    /// transparent "overlay" mode) and nothing else: the spray hits the quad, the paint
    /// lands in the quad's texture, and what is not painted is simply the passthrough
    /// behind it.
    ///
    ///   - It follows the tracked wall every frame (when the wall's pose changes), so the
    ///     paint stays put on the wall as the player moves and as tracking is refined.
    ///   - The quad's collision mesh is double-sided: whichever way the runtime's normal
    ///     points, the spray from inside the room hits it.
    ///   - A wall that is lost hides its paint and stops taking paint; found again, it
    ///     comes back exactly as it was.
    ///   - A faint frame around the wall says "recognised, paintable": brightest when the
    ///     wall is first found, and while you look toward it.
    ///
    /// Created by MRSurfaceHub, never placed by hand.
    /// </summary>
    [RequireComponent(typeof(PaintSurface))]
    public class MRSurface : MonoBehaviour
    {
        [SerializeField] MeshRenderer paintRenderer;
        [SerializeField] MeshCollider paintCollider;

        [Tooltip("Seconds the frame stays bright after the wall is found.")]
        [SerializeField] float introSeconds = 5f;

        const float PoseEpsilon = 0.0005f;
        const float FrameInset = 0.03f;
        const float FrameWidth = 0.014f;

        static readonly Color TrackingTint = new Color(0.35f, 0.85f, 1f);
        static readonly Color LostTint = new Color(1f, 0.6f, 0.2f);

        public string Id { get; private set; }
        public Vector2 Size { get; private set; }
        public Vector2 CenterInPlane { get; private set; }
        public PaintSurface Paint { get; private set; }
        public MRSurfaceState State { get; private set; } = MRSurfaceState.Tracking;

        /// <summary>The surface's own texel count painted when it was restored from a saved session, or -1 if nothing was restored.</summary>
        public int RestoredTexels { get; internal set; } = -1;

        /// <summary>Paint changed on screen since the last save.</summary>
        public bool Unsaved { get; internal set; }

        /// <summary>Time of the last change to the paint (for the autosave pause).</summary>
        public float LastChangeTime { get; private set; }

        /// <summary>The paint revision known to be saved (or restored).</summary>
        internal int SavedRevision;

        /// <summary>Artwork was just put back: its upload (the next revision) is not a change.</summary>
        internal bool ExpectRestoreUpload;

        Transform follow;
        Camera head;
        MeshRenderer frameRenderer;
        Material frameMaterial;
        float brightness;
        float born;
        int lastRevision = -1;
        Vector3 lastPosition;
        Quaternion lastRotation = Quaternion.identity;
        bool poseKnown;

        internal void Initialize(string id, Vector2 size, Vector2 centerInPlane, Transform followTransform, Camera headCamera, Material frameBase)
        {
            Id = id;
            Size = size;
            CenterInPlane = centerInPlane;
            follow = followTransform;
            head = headCamera;
            Paint = GetComponent<PaintSurface>();
            lastRevision = Paint.Revision;
            SavedRevision = lastRevision;
            born = Time.unscaledTime;
            BuildFrame(frameBase);
            PullPose(true);
        }

        /// <summary>The wall's pose source (the AR plane's transform). Null: the wall is gone.</summary>
        internal void SetFollow(Transform followTransform) => follow = followTransform;

        internal void SetTracking(bool tracking)
        {
            var state = tracking && follow != null ? MRSurfaceState.Tracking : MRSurfaceState.Lost;
            if (state == State)
                return;

            State = state;
            var tracked = state == MRSurfaceState.Tracking;
            if (paintCollider != null)
                paintCollider.enabled = tracked;
            if (paintRenderer != null)
                paintRenderer.enabled = tracked;
            if (tracked)
                born = Time.unscaledTime;
        }

        /// <summary>Whether a newly reported size or centre is a different wall, not tracking noise.</summary>
        internal bool GeometryChanged(Vector2 size, Vector2 centerInPlane)
        {
            var relative = new Vector2(Mathf.Abs(size.x - Size.x) / Mathf.Max(0.01f, Size.x), Mathf.Abs(size.y - Size.y) / Mathf.Max(0.01f, Size.y));
            return relative.x > 0.08f || relative.y > 0.08f || (centerInPlane - CenterInPlane).magnitude > 0.1f;
        }

        void LateUpdate()
        {
            if (follow == null)
            {
                if (State == MRSurfaceState.Tracking)
                    SetTracking(false);
            }
            else
            {
                PullPose(false);
            }

            if (Paint != null && Paint.Revision != lastRevision)
            {
                lastRevision = Paint.Revision;
                if (ExpectRestoreUpload)
                {
                    // The upload of artwork that was just put back: nothing new to save.
                    ExpectRestoreUpload = false;
                    SavedRevision = lastRevision;
                    Unsaved = false;
                }
                else if (lastRevision != SavedRevision)
                {
                    Unsaved = true;
                    LastChangeTime = Time.unscaledTime;
                }
            }

            UpdateFrame();
        }

        // Only when the wall's pose actually changed: a collider that moves every frame is
        // physics work for nothing.
        void PullPose(bool force)
        {
            if (follow == null)
                return;

            var position = follow.position;
            var rotation = follow.rotation;
            if (!force && poseKnown && (position - lastPosition).sqrMagnitude < PoseEpsilon * PoseEpsilon && Quaternion.Angle(rotation, lastRotation) < 0.02f)
                return;

            transform.SetPositionAndRotation(position, rotation);
            lastPosition = position;
            lastRotation = rotation;
            poseKnown = true;
        }

        // ---------- the frame ----------

        void BuildFrame(Material frameBase)
        {
            if (frameBase == null)
                return;

            var frame = new GameObject("Frame");
            frame.transform.SetParent(transform, false);
            frame.AddComponent<MeshFilter>().sharedMesh = BuildFrameMesh(Size, CenterInPlane);
            frameRenderer = frame.AddComponent<MeshRenderer>();
            frameMaterial = new Material(frameBase) { name = "MRFrame_" + Id };
            frameRenderer.sharedMaterial = frameMaterial;
            frameRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            frameRenderer.receiveShadows = false;
            frameRenderer.enabled = false;
        }

        void UpdateFrame()
        {
            if (frameRenderer == null)
                return;

            var target = 0f;
            var lost = State == MRSurfaceState.Lost;
            if (Time.unscaledTime - born < introSeconds)
                target = 0.55f;

            if (head != null)
            {
                var toWall = (follow != null ? follow.TransformPoint(new Vector3(CenterInPlane.x, 0f, CenterInPlane.y)) : transform.position) - head.transform.position;
                var distance = toWall.magnitude;
                if (distance > 0.01f && distance < 6f && Vector3.Dot(head.transform.forward, toWall / distance) > 0.82f)
                    target = Mathf.Max(target, lost ? 0.5f : 0.3f);
            }

            if (lost)
                target = Mathf.Max(target, 0.2f);

            brightness = Mathf.MoveTowards(brightness, target, Time.unscaledDeltaTime / 0.4f);
            var visible = brightness > 0.01f;
            if (frameRenderer.enabled != visible)
                frameRenderer.enabled = visible;
            if (!visible)
                return;

            // Straight colour in, premultiplied tint out: the overlay shader adds the colour
            // scaled by its opacity.
            var tint = lost ? LostTint : TrackingTint;
            var a = brightness * 0.8f;
            frameMaterial.SetColor("_BaseColor", new Color(tint.r * a, tint.g * a, tint.b * a, a));
        }

        // Four strips just inside the wall's edge, in the wall's own plane (a hair in front, so
        // they never fight the paint quad).
        static Mesh BuildFrameMesh(Vector2 size, Vector2 center)
        {
            var hx = size.x * 0.5f - FrameInset;
            var hz = size.y * 0.5f - FrameInset;
            var w = FrameWidth;
            var vertices = new Vector3[16];
            var triangles = new int[24 * 2];
            var uv = new Vector2[16];

            // Outer rectangle corners then inner; each strip is one quad.
            Vector3 P(float x, float z) => new Vector3(center.x + x, 0.001f, center.y + z);
            var strips = new[]
            {
                (P(-hx, -hz), P(hx, -hz), P(hx - w, -hz + w), P(-hx + w, -hz + w)),
                (P(hx, -hz), P(hx, hz), P(hx - w, hz - w), P(hx - w, -hz + w)),
                (P(hx, hz), P(-hx, hz), P(-hx + w, hz - w), P(hx - w, hz - w)),
                (P(-hx, hz), P(-hx, -hz), P(-hx + w, -hz + w), P(-hx + w, hz - w)),
            };

            var v = 0;
            var t = 0;
            foreach (var (a, b, c, d) in strips)
            {
                vertices[v] = a; vertices[v + 1] = b; vertices[v + 2] = c; vertices[v + 3] = d;
                uv[v] = Vector2.zero; uv[v + 1] = Vector2.right; uv[v + 2] = Vector2.one; uv[v + 3] = Vector2.up;

                // Both windings: the wall may be seen from either side of its plane.
                triangles[t++] = v; triangles[t++] = v + 2; triangles[t++] = v + 1;
                triangles[t++] = v; triangles[t++] = v + 3; triangles[t++] = v + 2;
                triangles[t++] = v; triangles[t++] = v + 1; triangles[t++] = v + 2;
                triangles[t++] = v; triangles[t++] = v + 2; triangles[t++] = v + 3;
                v += 4;
            }

            var mesh = new Mesh { name = "MRFrame", vertices = vertices, uv = uv, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The wall's quad: its own size, in the plane's local XZ, UV 0..1 across it, both faces.</summary>
        public static Mesh BuildQuadMesh(Vector2 size, Vector2 center)
        {
            var x0 = center.x - size.x * 0.5f;
            var x1 = center.x + size.x * 0.5f;
            var z0 = center.y - size.y * 0.5f;
            var z1 = center.y + size.y * 0.5f;
            var mesh = new Mesh
            {
                name = "MRWall",
                vertices = new[] { new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) },
                // Seen from +Y (the plane's normal) and from -Y.
                triangles = new[] { 0, 3, 2, 0, 2, 1, 0, 2, 3, 0, 1, 2 },
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        void OnDestroy()
        {
            if (frameMaterial != null)
                Destroy(frameMaterial);
            var filter = GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                Destroy(filter.sharedMesh);
            if (frameRenderer != null)
            {
                var frameFilter = frameRenderer.GetComponent<MeshFilter>();
                if (frameFilter != null && frameFilter.sharedMesh != null)
                    Destroy(frameFilter.sharedMesh);
            }
        }
    }
}
