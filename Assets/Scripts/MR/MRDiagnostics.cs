using UnityEngine;
using UnityEngine.Profiling;

namespace Freehand.MR
{
    /// <summary>
    /// What the headset can tell about itself, written to the log every few seconds
    /// (adb logcat -s Unity | find "[MR-PERF]"): frame interval average and worst,
    /// frames that missed a 72 Hz budget, the CPU and GPU frame time the engine measures
    /// (when the device reports them), how many walls and how much texture memory they hold,
    /// and managed memory / collections. One line per interval, no per-frame allocation.
    /// This is measurement only - it never changes anything. Numbers from a run in the
    /// Editor are not numbers for the Quest.
    /// </summary>
    public class MRDiagnostics : MonoBehaviour
    {
        [SerializeField] MRModeController controller;
        [SerializeField] MRSurfaceHub hub;
        [SerializeField] float interval = 5f;

        [Tooltip("Frame interval over which a frame counts as late (14.0 ms is 72 Hz less a margin).")]
        [SerializeField] float lateFrameMilliseconds = 14.6f;

        readonly FrameTiming[] timings = new FrameTiming[1];
        float elapsed;
        int frames;
        float sum;
        float worst;
        int late;
        int collections;
        long managed;

        void OnEnable()
        {
            collections = System.GC.CollectionCount(0);
            managed = System.GC.GetTotalMemory(false);
        }

        void Update()
        {
            var dt = Time.unscaledDeltaTime;
            elapsed += dt;
            frames++;
            sum += dt;
            if (dt > worst)
                worst = dt;
            if (dt * 1000f > lateFrameMilliseconds)
                late++;

            if (elapsed >= interval)
                Report();
        }

        void Report()
        {
            var cpu = 0.0;
            var gpu = 0.0;
            if (FrameTimingManager.IsFeatureEnabled())
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    cpu = timings[0].cpuFrameTime;
                    gpu = timings[0].gpuFrameTime;
                }
            }

            var mode = controller != null ? controller.Mode.ToString() : "?";
            var walls = hub != null ? hub.SurfaceCount : 0;
            var textureMb = hub != null ? hub.SurfaceBytes / (1024f * 1024f) : 0f;
            var gcNow = System.GC.CollectionCount(0);
            var managedNow = System.GC.GetTotalMemory(false);
            Debug.Log($"[MR-PERF] mode={mode} frames={frames} avg={sum / frames * 1000f:F1}ms worst={worst * 1000f:F1}ms late={late} cpu={cpu:F1}ms gpu={gpu:F1}ms walls={walls} wallTex={textureMb:F1}MB managed={managedNow / (1024f * 1024f):F1}MB gc0=+{gcNow - collections} unityMem={Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f):F0}MB");

            elapsed = 0f;
            frames = 0;
            sum = 0f;
            worst = 0f;
            late = 0;
            collections = gcNow;
            managed = managedNow;
        }
    }
}
