using System.Collections.Generic;
using UnityEngine;

namespace Freehand.Paint
{
    /// <summary>
    /// Immutable summary of one completed stroke - no pixel data, no
    /// persistence. V0.7 foundation for whatever needs to reason about
    /// strokes later (replay, save, gallery) without re-deriving them from
    /// raw paint calls; undo itself does not use this.
    /// </summary>
    public readonly struct StrokeInfo
    {
        public readonly int Order;
        public readonly Color Color;
        public readonly string CapName;
        public readonly Vector3 StartPosition;
        public readonly Vector3 EndPosition;
        public readonly int PointCount;
        public readonly float DurationSeconds;
        public readonly string[] SurfaceIds;

        public StrokeInfo(int order, Color color, string capName, Vector3 startPosition, Vector3 endPosition, int pointCount, float durationSeconds, string[] surfaceIds)
        {
            Order = order;
            Color = color;
            CapName = capName;
            StartPosition = startPosition;
            EndPosition = endPosition;
            PointCount = pointCount;
            DurationSeconds = durationSeconds;
            SurfaceIds = surfaceIds;
        }
    }

    /// <summary>
    /// Undo for painting: every undoable action (a stroke, or Clear All) is a
    /// Record of what it changed, saved just before it changed it, and undoing
    /// puts those pieces back - newest action first.
    ///
    /// Incremental, not snapshots: a surface saves only the tiles (small
    /// blocks of its texture) an action actually touches, the first time it
    /// touches each one. A stroke costs memory in proportion to the area it
    /// painted, not to the size of the wall, so the history is not a fixed
    /// number of actions: it is bounded only by MemoryBudgetBytes. Past that
    /// budget the oldest actions are dropped, oldest first, and that is
    /// logged - an explicit policy, never a silent cap.
    ///
    /// A stroke is "one trigger press to release" (SprayEmitter merges a
    /// release shorter than a blink into the same stroke). Paint that keeps
    /// running after a stroke ends - drips - is saved into the newest record,
    /// so undoing it removes the drip too.
    ///
    /// Clear All is one record like any other: undoing it brings back
    /// everything it cleared, and the history before it stays intact.
    /// </summary>
    public static class PaintHistory
    {
        const int MaxRecentStrokeSummaries = 20;

        /// <summary>Memory the undo history may use before the oldest actions are dropped. 192MB: thousands of ordinary strokes on Quest 3, well inside its app memory.</summary>
        public static long MemoryBudgetBytes = 192L * 1024 * 1024;

        /// <summary>One undoable action and the pieces of surface state it replaced.</summary>
        public sealed class Record
        {
            internal readonly List<IPaintTarget> Targets = new List<IPaintTarget>();
            internal readonly List<object> States = new List<object>();
            internal readonly HashSet<string> SurfaceIds = new HashSet<string>();
            internal long Bytes;
            internal bool Touched;

            public readonly string Label;
            public int Order;
            public float StartTime;
            public bool HasFirstPoint;
            public Color Color;
            public string CapName = "";
            public Vector3 StartPosition;
            public Vector3 EndPosition;
            public int PointCount;

            public Record(string label) => Label = label;

            /// <summary>Saves a piece of a surface's state, taken before the surface changes it. Restored in reverse order on undo.</summary>
            public void Save(IPaintTarget target, object state, int bytes)
            {
                Targets.Add(target);
                States.Add(state);
                Bytes += bytes;
                totalBytes += bytes;
            }
        }

        static readonly List<Record> Records = new List<Record>();
        static readonly List<StrokeInfo> recentStrokes = new List<StrokeInfo>();
        static readonly List<IPaintTarget> Targets = new List<IPaintTarget>();
        static Record activeStroke;
        static Record clearing;
        static int strokeCounter;
        static long totalBytes;
        static int droppedForMemory;

        /// <summary>How many actions can be undone right now.</summary>
        public static int UndoCount => Records.Count;

        /// <summary>Memory the undo history holds right now, bytes.</summary>
        public static long HistoryBytes => totalBytes;

        /// <summary>Actions dropped so far because the history reached MemoryBudgetBytes.</summary>
        public static int DroppedForMemory => droppedForMemory;

        public static bool StrokeOpen => activeStroke != null;

        /// <summary>
        /// The record a surface saves into before changing its pixels: the open stroke, a Clear All in
        /// progress, or - for paint still running after its stroke ended (drips) - the newest action.
        /// Null when nothing is being recorded (the environment's own start-up marks).
        /// </summary>
        public static Record Recording => clearing ?? activeStroke ?? (Records.Count > 0 ? Records[Records.Count - 1] : null);

        /// <summary>Read-only summaries of the most recent completed strokes, oldest first.</summary>
        public static IReadOnlyList<StrokeInfo> RecentStrokes => recentStrokes;

        // Play Mode can start with statics still holding the previous
        // session's data when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Records.Clear();
            recentStrokes.Clear();
            Targets.Clear();
            activeStroke = null;
            clearing = null;
            strokeCounter = 0;
            totalBytes = 0;
            droppedForMemory = 0;
        }

        /// <summary>A paintable surface joining the session (Clear All reaches every registered one).</summary>
        public static void Register(IPaintTarget target)
        {
            if (target != null && !Targets.Contains(target))
                Targets.Add(target);
        }

        public static void Unregister(IPaintTarget target) => Targets.Remove(target);

        public static void BeginStroke()
        {
            activeStroke = new Record("Stroke") { Order = ++strokeCounter, StartTime = Time.time };
        }

        /// <summary>Called before paint lands on a target during the open stroke - marks the stroke as having painted something.</summary>
        public static void RecordTargetIfNeeded(IPaintTarget target)
        {
            if (activeStroke == null || target == null)
                return;

            activeStroke.Touched = true;
        }

        /// <summary>Called once per paint tick with the point/metadata just applied (endpoints, count, surfaces) - no pixel data.</summary>
        public static void RecordPoint(Vector3 worldPosition, Color color, string capName, string surfaceId)
        {
            if (activeStroke == null)
                return;

            if (!activeStroke.HasFirstPoint)
            {
                activeStroke.StartPosition = worldPosition;
                activeStroke.Color = color;
                activeStroke.CapName = capName ?? "";
                activeStroke.HasFirstPoint = true;
            }

            activeStroke.EndPosition = worldPosition;
            activeStroke.PointCount++;

            if (!string.IsNullOrEmpty(surfaceId))
                activeStroke.SurfaceIds.Add(surfaceId);
        }

        public static void EndStroke()
        {
            if (activeStroke == null)
                return;

            var stroke = activeStroke;
            activeStroke = null;

            // A trigger pull that never actually hit a surface is not a stroke.
            if (!stroke.Touched || stroke.States.Count == 0)
            {
                Release(stroke);
                return;
            }

            Push(stroke);

            var surfaceIds = new string[stroke.SurfaceIds.Count];
            stroke.SurfaceIds.CopyTo(surfaceIds);
            recentStrokes.Add(new StrokeInfo(stroke.Order, stroke.Color, stroke.CapName, stroke.StartPosition, stroke.EndPosition,
                stroke.PointCount, Time.time - stroke.StartTime, surfaceIds));
            if (recentStrokes.Count > MaxRecentStrokeSummaries)
                recentStrokes.RemoveAt(0);
        }

        /// <summary>
        /// Clears the paint off every registered surface as one undoable action. Ends an open stroke
        /// first. False if there was no paint to clear (nothing is recorded then).
        /// </summary>
        public static bool ClearAll()
        {
            EndStroke();

            var record = new Record(ClearAllLabel);
            clearing = record;
            var cleared = false;
            try
            {
                foreach (var target in Targets)
                {
                    if (target.ClearedByClearAll)
                        cleared |= target.ClearPaint();
                }
            }
            finally
            {
                clearing = null;
            }

            if (!cleared || record.States.Count == 0)
            {
                Release(record);
                return false;
            }

            Push(record);
            return true;
        }

        const string ClearAllLabel = "Clear All";

        /// <summary>
        /// Runs `change` as one undoable action labelled `label` (clearing one surface, loading a saved
        /// skin onto it): whatever the targets save while it runs is what an undo puts back. `change`
        /// returns whether it changed anything; if not, no action is recorded.
        /// </summary>
        public static bool Run(string label, System.Func<bool> change)
        {
            EndStroke();

            var record = new Record(label);
            clearing = record;
            bool changed;
            try
            {
                changed = change();
            }
            finally
            {
                clearing = null;
            }

            if (!changed || record.States.Count == 0)
            {
                Release(record);
                return false;
            }

            Push(record);
            return true;
        }

        /// <summary>Whether the action the last UndoLast undid was a Clear All (the restore has its own sound).</summary>
        public static bool LastUndoWasClearAll { get; private set; }

        /// <summary>Undoes the most recent action (a stroke, or a Clear All). Returns false if there was nothing to undo.</summary>
        public static bool UndoLast()
        {
            // An undo while still spraying first closes that stroke: it is the
            // one that gets undone.
            EndStroke();

            if (Records.Count == 0)
                return false;

            var record = Records[Records.Count - 1];
            Records.RemoveAt(Records.Count - 1);
            LastUndoWasClearAll = record.Label == ClearAllLabel;

            for (var i = record.States.Count - 1; i >= 0; i--)
                record.Targets[i].RestoreUndoState(record.States[i]);

            Release(record);
            return true;
        }

        /// <summary>
        /// Forgets every undoable action (the paint itself stays). Used when the world the actions
        /// were made in is swapped for another (virtual room / mixed reality): B must never reach
        /// back into paint that is no longer on screen.
        /// </summary>
        public static void ClearHistory()
        {
            EndStroke();
            foreach (var record in Records)
                Release(record);
            Records.Clear();
            recentStrokes.Clear();
        }

        /// <summary>Kept for existing callers: undoes the most recent action.</summary>
        public static bool UndoLastStroke() => UndoLast();

        static void Push(Record record)
        {
            Records.Add(record);

            // Memory, not a count: past the budget the oldest actions go,
            // never the one just made.
            while (totalBytes > MemoryBudgetBytes && Records.Count > 1)
            {
                var oldest = Records[0];
                Records.RemoveAt(0);
                Release(oldest);
                droppedForMemory++;
                Debug.Log($"[PaintHistory] Undo history at its memory budget ({MemoryBudgetBytes / (1024 * 1024)}MB): oldest action dropped ({droppedForMemory} so far). {Records.Count} actions still undoable.");
            }
        }

        // Gives the saved pieces back to whoever pooled them and forgets
        // their bytes.
        static void Release(Record record)
        {
            totalBytes -= record.Bytes;
            for (var i = 0; i < record.States.Count; i++)
                (record.States[i] as IPooledUndoState)?.ReturnToPool();

            record.Targets.Clear();
            record.States.Clear();
            record.Bytes = 0;
        }
    }

    /// <summary>A saved piece of surface state that can hand its memory back to a pool once its record is gone.</summary>
    public interface IPooledUndoState
    {
        void ReturnToPool();
    }
}
