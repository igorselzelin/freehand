using System;
using System.Collections;
using System.Reflection;
using System.Text;
using Freehand.MR;
using Freehand.Paint;
using UnityEngine;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
// The AR plane layer cannot run in the Editor (no Quest runtime), so a stand-in transform plays the
// part of "the plane the runtime reported": it exercises everything from MRSurfaceHub down (the quad,
// the overlay paint, tracking loss, saving, restoring, undo / Clear All scope). It does NOT prove that
// the Quest reports walls, that passthrough shows, or that the permission flow works.
public static partial class FreehandPlayTests
{
    static readonly BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static Transform MakeTestPlane(string name, Vector3 position)
    {
        var go = new GameObject(name);
        // Plane space: normal = up. Euler(-90,0,0): up -> world -Z (toward the player), plane Z -> world up.
        go.transform.SetPositionAndRotation(position, Quaternion.Euler(-90f, 0f, 0f));
        return go.transform;
    }

    static MRSurface WaitSurface(MRSurfaceHub hub, string id) { foreach (var s in hub.Surfaces) { if (s != null && s.Id == id) return s; } return null; }

    static void Check(StringBuilder log, bool ok, string label, string detail = "")
    {
        log.AppendLine((ok ? "  OK    " : "  FALHA ") + label + (string.IsNullOrEmpty(detail) ? "" : ": " + detail));
    }

    static string WallFile(string id) => System.IO.Path.Combine(MRWallStore.Folder, id + ".fhwall");

    static void PaintLine(Vector3 from, Vector3 to, int ticks, Color color, float radius, float alpha, object source)
    {
        PaintManager.BeginStroke(source);
        for (var i = 0; i < ticks; i++)
        {
            var origin = Vector3.Lerp(from, to, i / (float)(ticks - 1));
            if (!Physics.Raycast(origin, Vector3.forward, out var hit, 6f))
                continue;
            var stamp = new SprayStamp { color = color, radiusMeters = radius, alpha = alpha, stretch = 1f, capName = "Standard", slope = 1f, downhillUv = new Vector2(0f, -1f) };
            PaintManager.ApplyPaint(hit, Vector3.forward, stamp, source);
        }

        PaintManager.EndStroke(source);
    }

    // The whole MR surface life with a stand-in plane.
    public static IEnumerator MRSurfaces(StringBuilder log)
    {
        var mr = GameObject.Find("MixedReality");
        var hub = mr.GetComponent<MRSurfaceHub>();
        System.IO.Directory.CreateDirectory(MRWallStore.Folder);
        foreach (var f in new[] { "test-wall-1", "test-wall-2" })
        {
            foreach (var file in System.IO.Directory.GetFiles(MRWallStore.Folder, f + ".fhwall*"))
                System.IO.File.Delete(file);
        }

        // The world as it is in MR: the virtual walls are gone (they would stand in front of the real one).
        var controller = mr.GetComponent<MRModeController>();
        typeof(MRModeController).GetMethod("ApplyMixedWorld", Any).Invoke(controller, null);
        yield return null;
        var plane = MakeTestPlane("TestPlane1", new Vector3(0f, 1.3f, 2.5f));
        var start = Time.realtimeSinceStartup;
        hub.Report("test-wall-1", new Vector2(3f, 2.2f), Vector2.zero, plane, true);
        yield return 0.3f;
        var surface = WaitSurface(hub, "test-wall-1");
        Check(log, surface != null, "wall surface built from a reported plane", $"{(Time.realtimeSinceStartup - start) * 1000f:F0} ms incl. waiting");
        if (surface == null)
            yield break;

        var paint = surface.Paint;
        var collider = surface.GetComponent<MeshCollider>();
        var bounds = collider.bounds;
        Check(log, paint.IsOverlay && paint.TextureSize == 512, "overlay paint surface 512px", $"overlay={paint.IsOverlay} size={paint.TextureSize}");
        Check(log, Mathf.Abs(bounds.size.x - 3f) < 0.01f && Mathf.Abs(bounds.size.y - 2.2f) < 0.01f && bounds.size.z < 0.01f, "collider is the reported size and pose", $"bounds {bounds.size.x:F2}x{bounds.size.y:F2}x{bounds.size.z:F3} at {bounds.center}");
        Check(log, paint.CountPaintedPixels() == 0 && paint.Pixels[1000].a == 0, "starts transparent", $"a={paint.Pixels[1000].a}");

        // Spray from the room side: hit lands, paint appears, opacity is real.
        PaintLine(new Vector3(-0.8f, 1.5f, 0.5f), new Vector3(0.8f, 1.2f, 0.5f), 50, new Color(0.9f, 0.15f, 0.1f), 0.06f, 1.5f, "t");
        yield return null;
        yield return null;
        var painted = paint.CountPaintedPixels();
        Check(log, painted > 2000, "paint lands on the wall quad", $"{painted} texels");
        var maxA = 0; var someRgb = default(Color32); var edgeSeen = false;
        for (var i = 0; i < paint.Pixels.Length; i++)
        {
            var p = paint.Pixels[i];
            if (p.a > maxA) { maxA = p.a; someRgb = p; }
            if (p.a > 0 && p.a < 128) edgeSeen = true;
        }

        Check(log, maxA > 200 && edgeSeen, "opacity builds up with coats, soft edge is partly transparent", $"max alpha {maxA}, partial alpha present={edgeSeen}");
        Check(log, someRgb.r > 200 && someRgb.g < 60, "colour of opaque paint is the paint's own (not darkened by the bare wall)", $"rgb {someRgb.r},{someRgb.g},{someRgb.b}");

        // The uploaded texture is premultiplied.
        var tex = surface.GetComponent<MeshRenderer>().sharedMaterial.GetTexture("_BaseMap") as Texture2D;
        var up = tex.GetPixels32();
        var matched = 0; var checkedPx = 0;
        for (var i = 0; i < paint.Pixels.Length; i += 37)
        {
            var p = paint.Pixels[i];
            if (p.a == 0) { if (up[i].a == 0 && up[i].r == 0) matched++; checkedPx++; continue; }
            var er = (p.r * p.a + 127) / 255;
            if (Mathf.Abs(up[i].r - er) <= 1 && up[i].a == p.a) matched++;
            checkedPx++;
        }

        Check(log, matched == checkedPx, "uploaded texture is premultiplied", $"{matched}/{checkedPx} sampled texels");

        // Undo restores transparency exactly; undo count is this mode's.
        var hashPainted = paint.PixelHash();
        Check(log, PaintHistory.UndoCount >= 1, "stroke is one undo step", $"undo count {PaintHistory.UndoCount}");
        PaintManager.Undo();
        Check(log, paint.CountPaintedPixels() == 0 && paint.PixelHash() != hashPainted, "undo takes the paint back to fully transparent", $"{paint.CountPaintedPixels()} texels");

        // Paint again, then wait for the autosave.
        PaintLine(new Vector3(-1.0f, 1.0f, 0.5f), new Vector3(1.0f, 1.0f, 0.5f), 40, Color.cyan, 0.05f, 1.6f, "t");
        yield return 0.2f;
        var hashBeforeSave = paint.PixelHash();
        var texels = paint.CountPaintedPixels();
        yield return 4.0f;
        Check(log, System.IO.File.Exists(WallFile("test-wall-1")), "autosave wrote the wall's file after the painting stopped", WallFile("test-wall-1"));
        MRWallStore.TryReadHeader("test-wall-1", out var header);
        Check(log, header.paintedTexels > 0 && Mathf.Abs(header.sizeMeters.x - 3f) < 0.01f, "file carries the wall's size and painted texels", $"{header.paintedTexels} texels, {header.sizeMeters}, {(System.IO.File.Exists(WallFile("test-wall-1")) ? new System.IO.FileInfo(WallFile("test-wall-1")).Length : -1)} bytes");

        // Lost tracking: hidden, not paintable. Found again: back as it was.
        hub.Forget("test-wall-1");
        yield return null;
        Check(log, surface.State == MRSurfaceState.Lost && !collider.enabled && !surface.GetComponent<MeshRenderer>().enabled, "a wall no longer reported is hidden and takes no paint");
        var blocked = !Physics.Raycast(new Vector3(0f, 1.2f, 0.5f), Vector3.forward, out var h0, 6f) || h0.collider != collider;
        Check(log, blocked, "spray no longer hits a lost wall");
        hub.Report("test-wall-1", new Vector2(3f, 2.2f), Vector2.zero, plane, true);
        yield return null;
        Check(log, surface.State == MRSurfaceState.Tracking && collider.enabled && paint.PixelHash() == hashBeforeSave, "found again: paintable and the paint is exactly as it was");

        // The wall follows its plane: move the plane, the quad follows.
        plane.position += new Vector3(0.3f, 0f, 0.4f);
        yield return null;
        yield return null;
        Check(log, (surface.transform.position - plane.position).sqrMagnitude < 1e-6f, "paint surface follows the tracked wall", $"{surface.transform.position} vs {plane.position}");
        plane.position -= new Vector3(0.3f, 0f, 0.4f);
        yield return null;

        // Mode release saves, destroys; reporting again restores, silently.
        hub.ReleaseAll();
        yield return 0.2f;
        Check(log, hub.SurfaceCount == 0, "leaving MR removes every wall surface");
        var undoBefore = PaintHistory.UndoCount;
        hub.Report("test-wall-1", new Vector2(3f, 2.2f), Vector2.zero, plane, true);
        yield return 0.3f;
        var again = WaitSurface(hub, "test-wall-1");
        yield return 0.2f;
        Check(log, again != null && again.RestoredTexels > 0, "artwork restored for the same wall", again != null ? $"{again.RestoredTexels} texels restored" : "no surface");
        Check(log, again != null && again.Paint.PixelHash() == hashBeforeSave, "restored paint is identical to what was saved (hash)", again != null ? $"{again.Paint.PixelHash()} vs {hashBeforeSave}" : "");
        Check(log, PaintHistory.UndoCount == undoBefore, "restoring is not an undo step", $"{undoBefore} -> {PaintHistory.UndoCount}");
        yield return 4.0f;
        var fileTime = System.IO.File.GetLastWriteTimeUtc(WallFile("test-wall-1"));
        yield return 1f;
        Check(log, !again.Unsaved, "restored artwork is not re-saved as a change");

        // A different wall under the same id: not restored, old file kept aside.
        hub.ReleaseAll();
        yield return 0.2f;
        hub.Report("test-wall-1", new Vector2(3.6f, 2.2f), Vector2.zero, plane, true);
        yield return 0.4f;
        var other = WaitSurface(hub, "test-wall-1");
        Check(log, other != null && other.RestoredTexels < 0 && other.Paint.CountPaintedPixels() == 0, "same id, different size: artwork is NOT put on it");
        // V0.15: every set-aside version is kept, timestamped ("<file>.old-<time>").
        var keptAside = System.IO.Directory.GetFiles(MRWallStore.Folder, "test-wall-1.fhwall.old*").Length;
        Check(log, keptAside >= 1 && !System.IO.File.Exists(WallFile("test-wall-1")), "...and the old artwork is kept aside, not thrown away", $"mismatched={hub.MismatchedCount}, kept {keptAside}");

        // Clean up.
        hub.ReleaseAll();
        typeof(MRModeController).GetMethod("ApplyVirtualWorld", Any).Invoke(controller, null);
        UnityEngine.Object.Destroy(plane.gameObject);
        foreach (var f in new[] { "test-wall-1", "test-wall-2" })
            foreach (var file in System.IO.Directory.GetFiles(MRWallStore.Folder, f + ".fhwall*"))
                System.IO.File.Delete(file);

        yield return 0.1f;
    }

    // What the player sees of the world swap, and what B / Clear All reach in each world. The swap
    // itself (the part after passthrough is verified) is called directly: passthrough cannot run here.
    public static IEnumerator MRScope(StringBuilder log)
    {
        var mr = GameObject.Find("MixedReality");
        var controller = mr.GetComponent<MRModeController>();
        var hub = mr.GetComponent<MRSurfaceHub>();
        var wall = GameObject.Find("PaintWall");
        var vrSurface = wall.GetComponent<PaintSurface>();
        var table = GameObject.Find("ToolTable").transform;
        var can = GameObject.Find("SprayCan").transform;
        var tablePos = table.position;
        var canPos = can.position;

        // A mark on the virtual wall, in the virtual room.
        PaintLine(new Vector3(-0.5f, 1.4f, 0f), new Vector3(0.5f, 1.4f, 0f), 30, Color.yellow, 0.05f, 1.4f, "v");
        var vrPainted = vrSurface.CountPaintedPixels();
        var vrHash = vrSurface.PixelHash();
        Check(log, vrPainted > 1000 && PaintHistory.UndoCount == 1, "virtual wall painted, one undo step", $"{vrPainted} texels");

        // Enter MR in the Editor: no passthrough subsystem here -> must stay in the virtual room, say why.
        controller.EnterMixed();
        yield return 1.0f;
        Check(log, controller.Mode == MRMode.Virtual && controller.Problem == MRProblem.Unsupported, "no passthrough device: stays in VR with the reason", $"mode={controller.Mode} problem={controller.Problem}");
        Check(log, wall.activeInHierarchy, "virtual room untouched after the failed attempt");
        var camera = Camera.main;
        Debug.Log($"[test] camera clearFlags={camera.clearFlags} bg={camera.backgroundColor}");

        // The world swap, as it runs once passthrough is confirmed.
        var apply = typeof(MRModeController).GetMethod("ApplyMixedWorld", Any);
        var restore = typeof(MRModeController).GetMethod("ApplyVirtualWorld", Any);
        var head = camera.transform;
        var headFloor = new Vector3(head.position.x, 0f, head.position.z);
        apply.Invoke(controller, null);
        yield return null;
        Check(log, !wall.activeInHierarchy && !GameObject.Find("Room").transform.Find("Ceiling").gameObject.activeSelf, "swap: virtual walls and ceiling are gone");
        Check(log, !GameObject.Find("Room").transform.Find("Floor").GetComponent<Renderer>().enabled && GameObject.Find("Room").transform.Find("Floor").GetComponent<Collider>().enabled, "swap: floor not drawn but still solid");
        Check(log, PaintHistory.UndoCount == 0, "swap: the undo list belongs to the old world (emptied)", $"undo count {PaintHistory.UndoCount}");
        Check(log, vrSurface.PixelHash() == vrHash, "swap: virtual wall's paint is untouched (kept for the return)");
        var tableRel = table.position - headFloor;
        Debug.Log($"[test] table rel head floor {tableRel} (VR layout: {tablePos})");
        Check(log, Vector3.Distance(table.position, headFloor + Quaternion.Euler(0f, Quaternion.LookRotation(Vector3.ProjectOnPlane(head.forward, Vector3.up)).eulerAngles.y, 0f) * (tablePos)) < 0.01f, "swap: tools stand where the player stands, facing as they face", $"table {table.position}");

        // Paint a real wall in this world; Clear All and undo reach only it.
        var plane = MakeTestPlane("TestPlane2", new Vector3(0f, 1.3f, 2.5f));
        hub.Report("test-wall-2", new Vector2(3f, 2.2f), Vector2.zero, plane, true);
        yield return 0.4f;
        var mrWall = WaitSurface(hub, "test-wall-2");
        PaintLine(new Vector3(-0.6f, 1.2f, 0.5f), new Vector3(0.6f, 1.2f, 0.5f), 30, Color.magenta, 0.05f, 1.4f, "m");
        yield return null;
        var mrPainted = mrWall.Paint.CountPaintedPixels();
        Check(log, mrPainted > 1000 && PaintHistory.UndoCount == 1, "real wall painted: one undo step", $"{mrPainted} texels");
        PaintManager.ClearAll();
        Check(log, mrWall.Paint.CountPaintedPixels() == 0 && vrSurface.PixelHash() == vrHash, "Clear All in MR clears the real wall and not the virtual one");
        PaintManager.Undo();
        Check(log, mrWall.Paint.CountPaintedPixels() == mrPainted && vrSurface.PixelHash() == vrHash, "undoing the Clear All brings back the real wall's paint only");

        // Return.
        UnityEngine.Object.Destroy(plane.gameObject);
        hub.ReleaseAll();
        restore.Invoke(controller, null);
        yield return null;
        Check(log, wall.activeInHierarchy && GameObject.Find("Room").transform.Find("Floor").GetComponent<Renderer>().enabled, "return: virtual room is back");
        Check(log, Vector3.Distance(table.position, tablePos) < 0.01f && Vector3.Distance(can.position, canPos) < 0.01f, "return: tools back at their virtual layout", $"table {table.position} can {can.position}");
        Check(log, vrSurface.PixelHash() == vrHash && vrSurface.CountPaintedPixels() == vrPainted, "return: the virtual wall's paint is exactly as it was");
        PaintManager.ClearAll();
        Check(log, vrSurface.CountPaintedPixels() == 0, "Clear All in VR clears the virtual wall");
        PaintManager.Undo();
        yield return 0.2f;
    }
}
