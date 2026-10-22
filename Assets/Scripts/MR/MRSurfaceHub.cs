using System;
using System.Collections.Generic;
using Freehand.Paint;
using UnityEngine;

namespace Freehand.MR
{
    /// <summary>
    /// Every real wall the game paints on. It does not know where walls come from (the AR
    /// plane source reports them; so does a test): it keeps one MRSurface per wall it was
    /// told about, at most MaxSurfaces of them (the largest), and looks after what belongs
    /// to a wall's whole life:
    ///
    ///   - building the surface (inactive template -> configured -> activated, one per frame,
    ///     so a room with many walls does not stall a frame),
    ///   - putting back the artwork saved for that wall in an earlier session - only when
    ///     the wall is the same size - without making it an undo step,
    ///   - saving the artwork again a few seconds after the painting stops (never in the
    ///     middle of a stroke), when the mode is left, and when the app is paused or closed,
    ///   - removing it all when mixed reality is left.
    ///
    /// Nothing here is created from a hard-coded position: a wall exists here only because
    /// something reported it.
    /// </summary>
    public class MRSurfaceHub : MonoBehaviour
    {
        [Tooltip("Inactive object with MeshFilter, MeshRenderer (the overlay material), MeshCollider, PaintSurface (overlay) and MRSurface.")]
        [SerializeField] GameObject template;

        [Tooltip("The faint 'recognised wall' frame's material.")]
        [SerializeField] Material frameMaterial;

        [SerializeField] Camera head;
        [SerializeField] Transform surfaceRoot;

        [Tooltip("Walls painted on at once (the largest ones).")]
        [SerializeField] int maxSurfaces = 8;

        [Tooltip("A wall shorter than this on either side is not offered for painting.")]
        [SerializeField] float minSide = 0.6f;

        [Tooltip("Seconds after the last change before a wall's artwork is saved.")]
        [SerializeField] float autosaveDelay = 3f;

        struct Candidate
        {
            public string Id;
            public Vector2 Size;
            public Vector2 Center;
            public Transform Follow;
            public bool Tracking;
        }

        readonly Dictionary<string, Candidate> candidates = new Dictionary<string, Candidate>();
        readonly Dictionary<string, MRSurface> surfaces = new Dictionary<string, MRSurface>();
        readonly List<string> toCreate = new List<string>();
        readonly List<MRSurface> scratch = new List<MRSurface>();

        /// <summary>Anything changed about the walls (found, lost, restored, removed).</summary>
        public event Action Changed;

        /// <summary>Walls reported and big enough to paint, tracked or not.</summary>
        public int CandidateCount => candidates.Count;

        /// <summary>Walls that exist as paintable surfaces right now (tracked).</summary>
        public int ActiveCount
        {
            get
            {
                var count = 0;
                foreach (var s in surfaces.Values)
                {
                    if (s != null && s.State == MRSurfaceState.Tracking)
                        count++;
                }

                return count;
            }
        }

        public int LostCount
        {
            get
            {
                var count = 0;
                foreach (var s in surfaces.Values)
                {
                    if (s != null && s.State == MRSurfaceState.Lost)
                        count++;
                }

                return count;
            }
        }

        /// <summary>Walls whose artwork from an earlier session was put back this session.</summary>
        public int RestoredCount { get; private set; }

        /// <summary>Walls with saved artwork that did not match (a different size) and was left aside.</summary>
        public int MismatchedCount { get; private set; }

        public int SurfaceCount => surfaces.Count;
        public float MinSide => minSide;
        public IReadOnlyCollection<MRSurface> Surfaces => surfaces.Values;

        /// <summary>Texture memory the wall surfaces hold, bytes (paint + base + upload copies).</summary>
        public long SurfaceBytes
        {
            get
            {
                long bytes = 0;
                foreach (var s in surfaces.Values)
                {
                    if (s != null && s.Paint != null)
                        bytes += (long)s.Paint.TextureSize * s.Paint.TextureSize * 4 * 4;
                }

                return bytes;
            }
        }

        // ---------- what the source reports ----------

        /// <summary>A wall was found, or its pose/size updated. `follow` is the transform that carries its pose.</summary>
        public void Report(string id, Vector2 size, Vector2 centerInPlane, Transform follow, bool tracking)
        {
            if (string.IsNullOrEmpty(id) || size.x < minSide || size.y < minSide)
            {
                // Too small to paint: if it was a wall before and shrank, it is gone.
                if (!string.IsNullOrEmpty(id))
                    Forget(id);
                return;
            }

            candidates[id] = new Candidate { Id = id, Size = size, Center = centerInPlane, Follow = follow, Tracking = tracking };

            if (surfaces.TryGetValue(id, out var existing) && existing != null)
            {
                if (existing.GeometryChanged(size, centerInPlane))
                {
                    // A different wall under the same name (the room was edited): artwork saved
                    // for the old size stays aside, this one starts clean.
                    SaveSurface(existing);
                    Destroy(existing.gameObject);
                    surfaces.Remove(id);
                }
                else
                {
                    existing.SetFollow(follow);
                    existing.SetTracking(tracking);
                    return;
                }
            }

            Reconcile();
            Changed?.Invoke();
        }

        /// <summary>A wall is no longer reported. Its surface stays (hidden, not paintable) so that finding it again brings it back as it was.</summary>
        public void Forget(string id)
        {
            candidates.Remove(id);
            if (surfaces.TryGetValue(id, out var surface) && surface != null)
            {
                surface.SetFollow(null);
                surface.SetTracking(false);
            }

            Changed?.Invoke();
        }

        // Which candidates have a surface: the largest MaxSurfaces. Creation is queued.
        void Reconcile()
        {
            var ranked = new List<Candidate>(candidates.Values);
            ranked.Sort((a, b) => (b.Size.x * b.Size.y).CompareTo(a.Size.x * a.Size.y));
            for (var i = 0; i < ranked.Count && i < maxSurfaces; i++)
            {
                var id = ranked[i].Id;
                if (!surfaces.ContainsKey(id) && !toCreate.Contains(id))
                    toCreate.Add(id);
            }
        }

        void Update()
        {
            if (toCreate.Count > 0)
            {
                var id = toCreate[0];
                toCreate.RemoveAt(0);
                if (candidates.TryGetValue(id, out var candidate) && !surfaces.ContainsKey(id) && surfaces.Count < maxSurfaces)
                    Create(candidate);
            }

            Autosave();
        }

        void Create(Candidate c)
        {
            if (template == null)
            {
                Debug.LogError($"{nameof(MRSurfaceHub)}: no wall template - walls cannot be painted.", this);
                return;
            }

            var start = Time.realtimeSinceStartup;
            var root = surfaceRoot != null ? surfaceRoot : transform;
            var go = Instantiate(template, root);
            go.name = "MRWall_" + (c.Id.Length > 10 ? c.Id.Substring(0, 10) : c.Id);

            var mesh = MRSurface.BuildQuadMesh(c.Size, c.Center);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshCollider>().sharedMesh = mesh;

            var paint = go.GetComponent<PaintSurface>();
            paint.ConfigureRuntime(c.Size, true, "mr:" + c.Id);

            var surface = go.GetComponent<MRSurface>();
            surface.Initialize(c.Id, c.Size, c.Center, c.Follow, head, frameMaterial);
            surfaces[c.Id] = surface;

            // Awake / OnEnable run here, with the wall's size and pose already set.
            go.SetActive(true);
            surface.SetTracking(c.Tracking);
            Restore(surface);

            Debug.Log($"[MR] wall {c.Id} {c.Size.x:F2}x{c.Size.y:F2}m surface built in {(Time.realtimeSinceStartup - start) * 1000f:F1} ms (restored {(surface.RestoredTexels >= 0 ? surface.RestoredTexels + " texels" : "no")})");
            Changed?.Invoke();
        }

        // ---------- the artwork between sessions ----------

        void Restore(MRSurface surface)
        {
            var paint = surface.Paint;
            if (paint == null || !MRWallStore.TryReadHeader(surface.Id, out var header))
                return;

            // The same wall? Its size must still be what it was. Otherwise the artwork would be
            // stretched over a different wall - it is kept aside, not applied.
            var sameSize = Mathf.Abs(header.sizeMeters.x - surface.Size.x) <= surface.Size.x * 0.08f
                           && Mathf.Abs(header.sizeMeters.y - surface.Size.y) <= surface.Size.y * 0.08f;
            if (!sameSize)
            {
                MismatchedCount++;
                Debug.LogWarning($"[MR] wall {surface.Id}: saved artwork is for {header.sizeMeters.x:F2}x{header.sizeMeters.y:F2}m, this wall is {surface.Size.x:F2}x{surface.Size.y:F2}m - not restored (kept aside).");
                MRWallStore.SetAside(surface.Id);
                return;
            }

            var pixels = new Color32[paint.TextureSize * paint.TextureSize];
            if (!MRWallStore.TryRead(surface.Id, paint.TextureSize, pixels, out header) || !paint.ReplacePaintSilently(pixels))
            {
                Debug.LogWarning($"[MR] wall {surface.Id}: saved artwork unreadable - not restored.");
                return;
            }

            // The upload of what was just put back is the next revision: it is already saved.
            surface.ExpectRestoreUpload = true;
            surface.RestoredTexels = header.paintedTexels;
            RestoredCount++;
        }

        void Autosave()
        {
            if (surfaces.Count == 0 || PaintHistory.StrokeOpen)
                return;

            foreach (var surface in surfaces.Values)
            {
                if (surface != null && surface.Unsaved && Time.unscaledTime - surface.LastChangeTime >= autosaveDelay)
                {
                    SaveSurface(surface);
                    break; // one write per frame
                }
            }
        }

        void SaveSurface(MRSurface surface)
        {
            if (surface == null || surface.Paint == null)
                return;

            // A wall whose old, mismatched artwork is on disk: that file was set aside already.
            var painted = MRWallStore.Write(surface.Id, surface.Size, surface.Paint.TextureSize, surface.Paint.Pixels);
            if (painted >= 0)
            {
                surface.Unsaved = false;
                surface.SavedRevision = surface.Paint.Revision;
            }
        }

        /// <summary>Saves every wall whose paint changed (mode left, app paused or closed).</summary>
        public void SaveAll()
        {
            foreach (var surface in surfaces.Values)
            {
                if (surface != null && surface.Unsaved)
                    SaveSurface(surface);
            }
        }

        /// <summary>Mixed reality is over: artwork saved, every wall surface removed (the paint is on disk; the walls come back when the mode is entered again).</summary>
        public void ReleaseAll()
        {
            SaveAll();
            scratch.Clear();
            scratch.AddRange(surfaces.Values);
            foreach (var surface in scratch)
            {
                if (surface != null)
                    Destroy(surface.gameObject);
            }

            surfaces.Clear();
            candidates.Clear();
            toCreate.Clear();
            RestoredCount = 0;
            MismatchedCount = 0;
            Changed?.Invoke();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused)
                SaveAll();
        }

        void OnDestroy() => SaveAll();
    }
}
