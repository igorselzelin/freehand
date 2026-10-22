using System.Collections;
using System.IO;
using System.Text;
using Freehand.MR;
using Freehand.Paint;
using UnityEngine;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
public static partial class FreehandPlayTests
{
    static string ShotDir => Path.Combine(Directory.GetCurrentDirectory(), "Logs", "headless", "shots");

    static void Shot(Camera source, string name, Vector3 position, Quaternion rotation, Color background, int width = 1024, int height = 640, float fov = 70f)
    {
        Directory.CreateDirectory(ShotDir);
        var go = new GameObject("ShotCam");
        var camera = go.AddComponent<Camera>();
        camera.CopyFrom(source);
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.fieldOfView = fov;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = background;
        camera.transform.SetPositionAndRotation(position, rotation);
        var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture = rt;
        camera.Render();
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), tex.EncodeToPNG());
        Object.Destroy(tex);
        camera.targetTexture = null;
        rt.Release();
        Object.Destroy(rt);
        Object.Destroy(go);
    }

    // How a painted real wall looks (over a stand-in "real wall" colour), and the panel and frame.
    public static IEnumerator MRLook(StringBuilder log)
    {
        var mr = GameObject.Find("MixedReality");
        var hub = mr.GetComponent<MRSurfaceHub>();
        var controller = mr.GetComponent<MRModeController>();
        typeof(MRModeController).GetMethod("ApplyMixedWorld", Any).Invoke(controller, null);
        yield return null;

        var plane = MakeTestPlane("TestPlaneLook", new Vector3(0f, 1.3f, 2.2f));
        hub.Report("test-look", new Vector2(3.4f, 2.4f), Vector2.zero, plane, true);
        yield return 0.5f;

        // A few coats in different colours and sizes, across the wall.
        PaintLine(new Vector3(-1.2f, 1.9f, 0.5f), new Vector3(-0.2f, 1.6f, 0.5f), 80, new Color(0.95f, 0.2f, 0.12f), 0.045f, 1.5f, "a");
        PaintLine(new Vector3(-0.2f, 1.6f, 0.5f), new Vector3(0.9f, 1.95f, 0.5f), 80, new Color(0.15f, 0.55f, 1f), 0.07f, 1.1f, "a");
        PaintLine(new Vector3(-1.0f, 0.9f, 0.5f), new Vector3(1.1f, 0.8f, 0.5f), 80, Color.white, 0.03f, 1.8f, "a");
        PaintLine(new Vector3(0.2f, 1.2f, 0.5f), new Vector3(0.2f, 1.2f, 0.5f), 2, new Color(1f, 0.85f, 0.1f), 0.05f, 1.0f, "a");
        for (var i = 0; i < 40; i++)
            PaintLine(new Vector3(-0.6f, 1.25f, 0.5f), new Vector3(-0.6f, 1.25f, 0.5f), 2, new Color(0.1f, 0.85f, 0.4f), 0.05f, 0.9f, "b");
        yield return 3.5f;

        var camera = Camera.main;
        Shot(camera, "mr_wall_painted", new Vector3(0f, 1.5f, 0.2f), Quaternion.identity, new Color(0.55f, 0.5f, 0.44f, 1f));
        Shot(camera, "mr_wall_closeup", new Vector3(-0.3f, 1.6f, 1.2f), Quaternion.identity, new Color(0.55f, 0.5f, 0.44f, 1f), 1024, 640, 45f);

        // Frame brightness right after creation (the "recognised wall" hint).
        hub.ReleaseAll();
        yield return 0.2f;
        hub.Report("test-look", new Vector2(3.4f, 2.4f), Vector2.zero, plane, true);
        yield return 0.8f;
        Shot(camera, "mr_wall_frame", new Vector3(0f, 1.5f, 0.2f), Quaternion.identity, new Color(0.55f, 0.5f, 0.44f, 1f));

        // The tools and the panel, in the mixed world, as the player faces them.
        var panel = GameObject.Find("MRPanel").transform;
        var toPanel = panel.position + Vector3.up * 1.0f;
        Shot(camera, "mr_panel", new Vector3(0.05f, 1.45f, -0.25f), Quaternion.LookRotation((toPanel - new Vector3(0.05f, 1.45f, -0.25f)).normalized), new Color(0.55f, 0.5f, 0.44f, 1f), 1024, 640, 40f);
        Shot(camera, "mr_tools", new Vector3(0f, 1.6f, -0.3f), Quaternion.LookRotation((new Vector3(-0.5f, 0.8f, 0.3f) - new Vector3(0f, 1.6f, -0.3f)).normalized), new Color(0.55f, 0.5f, 0.44f, 1f), 1024, 640, 60f);

        hub.ReleaseAll();
        typeof(MRModeController).GetMethod("ApplyVirtualWorld", Any).Invoke(controller, null);
        Object.Destroy(plane.gameObject);
        if (File.Exists(WallFile("test-look"))) File.Delete(WallFile("test-look"));
        log.AppendLine("shots written to " + ShotDir);
        yield return 0.1f;
    }

    // Drips on a real wall run down - in the world, not just in the texture.
    public static IEnumerator MRDrips(StringBuilder log)
    {
        var mr = GameObject.Find("MixedReality");
        var hub = mr.GetComponent<MRSurfaceHub>();
        var controller = mr.GetComponent<MRModeController>();
        typeof(MRModeController).GetMethod("ApplyMixedWorld", Any).Invoke(controller, null);
        yield return null;
        var plane = MakeTestPlane("TestPlaneDrips", new Vector3(0f, 1.3f, 2.2f));
        hub.Report("test-drip", new Vector2(3f, 2.2f), Vector2.zero, plane, true);
        yield return 0.5f;
        var surface = WaitSurface(hub, "test-drip");

        // Hold the spray on one spot (world point on the wall at x=0, y=1.6).
        for (var i = 0; i < 70; i++)
            PaintLine(new Vector3(0f, 1.6f, 0.5f), new Vector3(0f, 1.6f, 0.5f), 2, Color.red, 0.045f, 1.4f, "d");
        yield return 4.0f;

        // World height of every painted texel: the texture is 512, the wall 2.2 m tall, v up = world up in this fixture.
        var pixels = surface.Paint.Pixels;
        const int size = 512;
        float lowest = 9f, highest = -9f; int painted = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (pixels[y * size + x].a == 0) continue;
                painted++;
                var worldY = 1.3f - 1.1f + (y + 0.5f) / size * 2.2f;
                if (worldY < lowest) lowest = worldY;
                if (worldY > highest) highest = worldY;
            }
        }

        log.AppendLine($"  painted texels {painted}; world height of paint {lowest:F2} .. {highest:F2} m (spray held at 1.60 m, radius 4.5 cm)");
        Check(log, lowest < 1.6f - 0.12f, "a drip ran DOWN the wall (below the spray spot)", $"lowest {lowest:F2} m");
        Check(log, highest < 1.6f + 0.09f, "nothing ran up", $"highest {highest:F2} m");

        hub.ReleaseAll();
        typeof(MRModeController).GetMethod("ApplyVirtualWorld", Any).Invoke(controller, null);
        Object.Destroy(plane.gameObject);
        if (File.Exists(WallFile("test-drip"))) File.Delete(WallFile("test-drip"));
        yield return 0.1f;
    }

    // Cost of paint on a real wall, and of building one (Editor numbers, not Quest numbers).
    public static IEnumerator MRPerf(StringBuilder log)
    {
        var mr = GameObject.Find("MixedReality");
        var hub = mr.GetComponent<MRSurfaceHub>();
        var controller = mr.GetComponent<MRModeController>();
        typeof(MRModeController).GetMethod("ApplyMixedWorld", Any).Invoke(controller, null);
        yield return null;

        var planes = new Transform[6];
        for (var i = 0; i < planes.Length; i++)
        {
            planes[i] = MakeTestPlane("TestPlanePerf" + i, new Vector3(-9f + i * 3.6f, 1.3f, 3f));
            hub.Report("test-perf-" + i, new Vector2(3.4f, 2.4f), Vector2.zero, planes[i], true);
        }

        var t0 = Time.realtimeSinceStartup;
        while (hub.SurfaceCount < planes.Length && Time.realtimeSinceStartup - t0 < 5f)
            yield return null;
        log.AppendLine($"  {hub.SurfaceCount} wall surfaces built, one per frame, in {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms wall-clock; texture memory held {hub.SurfaceBytes / (1024f * 1024f):F1} MB");

        // Paint ticks on one wall: PaintSegment + premultiplied upload.
        var surface = WaitSurface(hub, "test-perf-3");
        var watch = new System.Diagnostics.Stopwatch();
        var ticks = 0;
        double total = 0, worst = 0;
        PaintManager.BeginStroke("p");
        for (var f = 0; f < 120; f++)
        {
            var from = new Vector3(1.0f + f * 0.005f, 1.3f + Mathf.Sin(f * 0.2f) * 0.3f, 0.5f);
            if (Physics.Raycast(from, Vector3.forward, out var hit, 8f))
            {
                watch.Restart();
                PaintManager.ApplyPaint(hit, Vector3.forward, new SprayStamp { color = Color.red, radiusMeters = 0.07f, alpha = 1.2f, stretch = 1f, capName = "Fat" }, "p");
                watch.Stop();
                var ms = watch.Elapsed.TotalMilliseconds;
                total += ms; ticks++;
                if (ms > worst) worst = ms;
            }

            yield return null;
        }

        PaintManager.EndStroke("p");
        log.AppendLine($"  paint tick on a real wall (Fat cap, 7 cm): avg {total / Mathf.Max(1, ticks):F2} ms, worst {worst:F2} ms over {ticks} ticks (Editor, this PC)");

        // The upload: dirty rectangle -> premultiplied -> SetPixels32 + Apply, per frame while painting.
        var frames = 0; var sum = 0.0;
        var start = Time.realtimeSinceStartup;
        PaintManager.BeginStroke("p");
        for (var f = 0; f < 90; f++)
        {
            var from = new Vector3(1.0f + f * 0.01f, 1.2f, 0.5f);
            if (Physics.Raycast(from, Vector3.forward, out var hit, 8f))
                PaintManager.ApplyPaint(hit, Vector3.forward, new SprayStamp { color = Color.blue, radiusMeters = 0.05f, alpha = 1.2f, stretch = 1f, capName = "Standard" }, "p");
            var t = Time.realtimeSinceStartup;
            yield return null;
            sum += Time.realtimeSinceStartup - t; frames++;
        }

        PaintManager.EndStroke("p");
        log.AppendLine($"  frame interval while painting a real wall: avg {sum / frames * 1000.0:F1} ms (Editor frame; includes everything)");

        // Save cost.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var written = MRWallStore.Write("test-perf-3", surface.Size, surface.Paint.TextureSize, surface.Paint.Pixels);
        sw.Stop();
        log.AppendLine($"  save of a painted wall: {sw.Elapsed.TotalMilliseconds:F1} ms, {written} texels, {new FileInfo(WallFile("test-perf-3")).Length} bytes");
        var pixels = new Color32[surface.Paint.TextureSize * surface.Paint.TextureSize];
        sw.Restart();
        MRWallStore.TryRead("test-perf-3", surface.Paint.TextureSize, pixels, out _);
        sw.Stop();
        log.AppendLine($"  load of a painted wall: {sw.Elapsed.TotalMilliseconds:F1} ms");

        hub.ReleaseAll();
        typeof(MRModeController).GetMethod("ApplyVirtualWorld", Any).Invoke(controller, null);
        foreach (var p in planes) Object.Destroy(p.gameObject);
        for (var i = 0; i < 6; i++) { var f = WallFile("test-perf-" + i); if (File.Exists(f)) File.Delete(f); }
        yield return 0.1f;
    }
}
