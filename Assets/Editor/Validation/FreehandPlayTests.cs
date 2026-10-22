using System.Collections;
using System.Text;
using Freehand.Paint;
using UnityEngine;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
public static partial class FreehandPlayTests
{
    public static IEnumerator Smoke(StringBuilder log)
    {
        log.AppendLine("frame " + Time.frameCount + " main camera: " + (Camera.main != null));
        yield return 0.2f;
    }

    // Deterministic strokes on a virtual wall, all inside one frame (no drips yet): the same numbers
    // before and after the V0.13 changes prove the VR painting path is untouched.
    public static IEnumerator PaintBaseline(StringBuilder log)
    {
        var wall = GameObject.Find("PaintWall");
        var surface = wall.GetComponent<PaintSurface>();
        log.AppendLine($"PaintWall size={surface.TextureSize} hash0={surface.PixelHash()} painted0={surface.CountPaintedPixels()}");

        var bounds = wall.GetComponent<Collider>().bounds;
        var origin = new Vector3(bounds.center.x, 1.4f, 0f);
        PaintManager.BeginStroke();
        for (var i = 0; i < 60; i++)
        {
            var from = origin + new Vector3(Mathf.Lerp(-1.2f, 1.2f, i / 59f), 0.25f * Mathf.Sin(i * 0.35f), 0f);
            if (!Physics.Raycast(from, Vector3.forward, out var hit, 5f))
                continue;
            var stamp = new SprayStamp { color = new Color(0.9f, 0.2f, 0.1f), radiusMeters = 0.05f, alpha = 1.4f, stretch = 1f, capName = "Standard" };
            PaintManager.ApplyPaint(hit, Vector3.forward, stamp, "baseline");
        }

        PaintManager.EndStroke();
        log.AppendLine($"after stroke: hash={surface.PixelHash()} painted={surface.CountPaintedPixels()} undo={PaintHistory.UndoCount}");

        PaintManager.Undo();
        log.AppendLine($"after undo:   hash={surface.PixelHash()} painted={surface.CountPaintedPixels()}");

        PaintManager.BeginStroke();
        if (Physics.Raycast(origin, Vector3.forward, out var hit2, 5f))
            PaintManager.ApplyPaint(hit2, Vector3.forward, new SprayStamp { color = Color.cyan, radiusMeters = 0.08f, alpha = 2f, stretch = 1f, capName = "Fat" }, "baseline");
        PaintManager.EndStroke();
        log.AppendLine($"stroke2: hash={surface.PixelHash()} painted={surface.CountPaintedPixels()}");
        PaintManager.ClearAll();
        log.AppendLine($"after clear all: hash={surface.PixelHash()} painted={surface.CountPaintedPixels()}");
        PaintManager.Undo();
        log.AppendLine($"after undo clear: hash={surface.PixelHash()} painted={surface.CountPaintedPixels()}");
        PaintManager.Undo();
        log.AppendLine($"final: hash={surface.PixelHash()} painted={surface.CountPaintedPixels()}");
        yield return 0.1f;
    }
}
