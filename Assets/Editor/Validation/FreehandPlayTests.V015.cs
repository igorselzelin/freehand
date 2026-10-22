using System.Collections;
using System.IO;
using System.Text;
using Freehand.Customization;
using Freehand.Interaction;
using Freehand.MR;
using Freehand.Paint;
using Freehand.Spray;
using Freehand.VR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
// V0.15: freestyle edge cases and interruptions, graffiti feel measurements, save-data safety.
public static partial class FreehandPlayTests
{
    static void SetTracked(InputDevice device, bool tracked)
    {
        InputSystem.QueueDeltaStateEvent((IntegerControl)device["trackingState"], tracked ? 3 : 0);
        InputSystem.QueueDeltaStateEvent((ButtonControl)device["isTracked"], tracked);
        InputSystem.Update();
    }

    static IEnumerator WaitFlight(float timeout)
    {
        var end = Time.realtimeSinceStartup + timeout;
        while (!fsGrab.InFlight && Time.realtimeSinceStartup < end)
            yield return null;
    }

    static IEnumerator RegrabRight()
    {
        var held = fsGrab.IsGrabbed && fsRight.Held == fsGrab;
        for (var attempt = 0; attempt < 16 && !held; attempt++)
        {
            SetAxis(rightPad, "grip", 0f);
            yield return 0.1f;
            SetPose(rightPad, fsGrab.transform.position + Vector3.up * (0.12f - 0.02f * (attempt % 8)), Quaternion.identity);
            yield return 0.15f;
            SetAxis(rightPad, "grip", 1f);
            yield return 0.25f;
            held = fsGrab.IsGrabbed && fsRight.Held == fsGrab;
        }

        SetPose(rightPad, RestRight, Quaternion.Euler(-20f, 0f, 0f));
        yield return 0.4f;
    }

    // A valid end state after any interruption: one owner or none, nothing stuck.
    static string StateProblems(SprayEmitter emitter)
    {
        var problems = new StringBuilder();
        if (fsLeft.Held == fsGrab && fsRight.Held == fsGrab) problems.Append("two owners; ");
        if (fsGrab.IsGrabbed && fsGrab.InFlight) problems.Append("grabbed and in flight; ");
        if (!fsGrab.IsGrabbed && !fsGrab.InFlight && fsGrab.GetComponent<Rigidbody>().isKinematic) problems.Append("free but kinematic; ");
        if ((fsLeft.Held == fsGrab || fsRight.Held == fsGrab) != fsGrab.IsGrabbed) problems.Append("grabber/grab disagree; ");
        if (!fsFree.Active && (emitter.FreestyleLock || emitter.InspectionLock)) problems.Append("spray lock left on; ");
        if (!fsFree.Active && (fsLeft.CatchReady || fsRight.CatchReady)) problems.Append("hand left open for a catch; ");
        if (!fsFree.Active && fsGrab.Presenting) problems.Append("presentation left on; ");
        if (!fsGrab.InFlight)
            foreach (var c in fsGrab.GetComponentsInChildren<Collider>())
                if (c.attachedRigidbody == fsGrab.GetComponent<Rigidbody>() && c.isTrigger) { problems.Append($"collider {c.name} left as a trigger; "); break; }
        return problems.Length == 0 ? "ok" : problems.ToString();
    }

    public static IEnumerator FreestyleEdges(StringBuilder log)
    {
        yield return FreestyleSetup(log);
        if (!fsGrab.IsGrabbed)
        {
            RemovePads();
            yield break;
        }

        var emitter = fsGrab.GetComponent<SprayEmitter>();

        // ---- A. The button through the real input path (A on the right controller) ----
        SetAxis(rightPad, "primaryButton", 1f);
        yield return 0.1f;
        SetAxis(rightPad, "primaryButton", 0f);
        yield return 0.1f;
        Check(log, fsFree.Active, "A on the hand holding the can starts a run (through InspectionInput)", fsFree.Current.ToString());
        SetAxis(rightPad, "primaryButton", 1f);
        yield return 0.1f;
        SetAxis(rightPad, "primaryButton", 0f);
        yield return WaitIdle(4f);
        Check(log, !fsFree.Active, "A again ends it");

        // ---- B. Variety: no two identical runs in a row ----
        var previous = "";
        var repeats = 0;
        for (var i = 0; i < 16; i++)
        {
            fsFree.Request();
            var run = fsFree.LastRun;
            if (run == previous) repeats++;
            previous = run;
            fsFree.Request();
            yield return WaitIdle(4f);
        }

        Check(log, repeats == 0, "16 runs: never the same run twice in a row", $"{repeats} repeat(s)");

        // ---- C. Stop pressed while the can is in the air: it is caught first, then the run stops ----
        fsFree.Play("TossFlip,Roll,Fidget");
        yield return WaitFlight(1.5f);
        fsFree.Request();
        yield return WaitIdle(3f);
        Check(log, fsRight.Held == fsGrab && !fsFree.Active, "stop during a toss: caught, then stopped (no further moves)", $"held={fsRight.Held == fsGrab} state={fsFree.Current}");
        Check(log, StateProblems(emitter) == "ok", "...valid state", StateProblems(emitter));

        // ---- D. Pass whose receiving hand moves far away mid-flight ----
        SetPose(leftPad, RestLeft, Quaternion.Euler(-20f, 0f, 0f));
        SetAxis(leftPad, "grip", 1f);
        yield return 0.4f;
        fsFree.Play("Pass");
        yield return WaitFlight(1.5f);
        yield return 0.06f;
        SetPose(leftPad, RestLeft + new Vector3(-0.8f, -0.3f, 0.2f), Quaternion.Euler(-20f, 0f, 0f));
        yield return 1.0f;
        var passResult = fsLeft.Held == fsGrab ? "caught by the moved hand" : (!fsGrab.IsGrabbed ? "dropped" : "other");
        Check(log, passResult == "dropped", "pass: a receiving hand that moved 0.8 m away does not pull the can after it - it is dropped", passResult);
        Check(log, StateProblems(emitter) == "ok", "...valid state", StateProblems(emitter));
        SetAxis(leftPad, "grip", 0f);
        SetPose(leftPad, RestLeft + new Vector3(-0.3f, 0f, 0f), Quaternion.identity);
        yield return 1.0f;
        yield return RegrabRight();

        // ---- E. A loose object in the flight path is not shoved by the flying can ----
        var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obstacle.name = "TestObstacle";
        obstacle.transform.localScale = Vector3.one * 0.06f;
        var obstacleBody = obstacle.AddComponent<Rigidbody>();
        obstacleBody.useGravity = false;
        SetPose(leftPad, RestLeft, Quaternion.Euler(-20f, 0f, 0f));
        SetAxis(leftPad, "grip", 1f);
        yield return 0.4f;
        var renderer = fsGrab.GetComponentInChildren<Renderer>();
        obstacle.transform.position = (renderer.bounds.center + (renderer.bounds.center + (fsLeftController.position - fsRightController.position))) * 0.5f + Vector3.up * 0.05f;
        obstacleBody.linearVelocity = Vector3.zero;
        fsFree.Play("Pass");
        yield return WaitIdle(3f);
        var shove = obstacleBody.linearVelocity.magnitude;
        log.AppendLine($"  obstacle speed after the pass flew through it: {shove:F2} m/s");
        Check(log, shove < 0.05f, "a flying can does not shove loose objects (no infinite-mass push)", $"{shove:F2} m/s");
        Object.Destroy(obstacle);
        Check(log, StateProblems(emitter) == "ok", "...valid state", StateProblems(emitter));
        if (fsLeft.Held == fsGrab)
        {
            SetAxis(rightPad, "grip", 1f);
            yield return 0.3f;
            fsFree.Play("Pass");
            yield return WaitIdle(3f);
        }

        SetAxis(leftPad, "grip", 0f);
        yield return 0.3f;
        if (fsRight.Held != fsGrab)
            yield return RegrabRight();

        // ---- F. The holding hand loses tracking for a moment mid-toss ----
        fsFree.Play("TossSpin");
        yield return WaitFlight(1.5f);
        SetTracked(rightPad, false);
        yield return 0.3f;
        SetTracked(rightPad, true);
        yield return WaitIdle(3f);
        yield return 0.3f;
        Check(log, StateProblems(emitter) == "ok", "tracking lost for 0.3 s mid-toss: valid state afterwards", $"{StateProblems(emitter)} held={fsGrab.IsGrabbed}");
        if (fsRight.Held != fsGrab)
            yield return RegrabRight();

        // ---- G. The other hand takes the cap during an in-hand move ----
        var rig = Object.FindAnyObjectByType<HandRig>();
        var socket = fsGrab.GetComponentInChildren<CapSocket>(true);
        var cap = socket != null ? socket.Installed : null;
        if (cap != null && rig != null)
        {
            fsFree.Play("Flip,Roll");
            yield return 0.4f;
            var capCenter = cap.GetComponent<Renderer>().bounds.center;
            var offset = rig.PinchPoint(true).Value - fsLeftController.position;
            SetPose(leftPad, capCenter - offset, fsLeftController.rotation);
            yield return 0.15f;
            offset = rig.PinchPoint(true).Value - fsLeftController.position;
            capCenter = cap.GetComponent<Renderer>().bounds.center;
            SetPose(leftPad, capCenter - offset, fsLeftController.rotation);
            yield return 0.1f;
            SetAxis(leftPad, "grip", 1f);
            yield return 0.6f;
            Check(log, !fsFree.Active || fsFree.Current == CanFreestyle.State.Recovery, "the other hand on the cap ends the run", fsFree.Current.ToString());
            yield return WaitIdle(2f);
            Check(log, StateProblems(emitter) == "ok" && fsRight.Held == fsGrab, "...valid state, can still in the right hand", StateProblems(emitter));
            SetAxis(leftPad, "grip", 0f);
            yield return 0.8f;
            Check(log, socket.Installed == cap, "cap still (or again) on the can", cap.State.ToString());
            SetPose(leftPad, RestLeft + new Vector3(-0.3f, 0f, 0f), Quaternion.identity);
            yield return 0.3f;
        }

        // ---- H. Grip released during an in-hand move: dropped where it is, valid ----
        fsFree.Play("Roll");
        yield return 0.35f;
        SetAxis(rightPad, "grip", 0f);
        yield return 0.6f;
        Check(log, !fsGrab.IsGrabbed && !fsFree.Active && StateProblems(emitter) == "ok", "grip let go during an in-hand move: dropped, valid state", StateProblems(emitter));

        SetAxis(rightPad, "grip", 0f);
        SetAxis(leftPad, "grip", 0f);
        yield return 0.3f;
        RemovePads();
    }

    // ---------- graffiti feel: measured, per cap ----------

    public static IEnumerator GraffitiFeel(StringBuilder log)
    {
        var wall = GameObject.Find("PaintWall");
        var surface = wall.GetComponent<PaintSurface>();
        var face = wall.GetComponent<Collider>().bounds.min.z;
        var size = surface.TextureSize;
        var emitter = GameObject.Find("SprayCan").GetComponentInChildren<SprayEmitter>();
        var interval = (float)typeof(SprayEmitter).GetField("paintIntervalSeconds", Any).GetValue(emitter);
        log.AppendLine($"  paint tick {interval * 1000f:F0} ms; wall texture {size}px");

        void Paint(SprayCap cap, Vector3 origin, Vector3 direction, float distance, float flow)
        {
            if (!Physics.Raycast(origin, direction, out var hit, 4f))
                return;
            var stamp = new SprayStamp
            {
                color = Color.black,
                radiusMeters = cap.RadiusAtDistance(hit.distance),
                alpha = cap.AlphaAtDistance(hit.distance) * flow,
                stretch = 1f,
                capName = cap.displayName,
            };
            PaintManager.ApplyPaint(hit, direction, stamp, "feel");
        }

        int Painted(out int solid)
        {
            solid = 0;
            var n = 0;
            var pixels = surface.Pixels;
            var baseline = surface.BasePixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].r == baseline[i].r && pixels[i].g == baseline[i].g && pixels[i].b == baseline[i].b)
                    continue;
                n++;
                if (pixels[i].r < 40) solid++;
            }

            return n;
        }

        var faceSize = wall.GetComponent<Collider>().bounds.size;
        var ppmX = size / faceSize.x;
        var ppmY = size / faceSize.y;
        var cm2 = 10000f / (ppmX * ppmY);
        var roundness = new System.Collections.Generic.Dictionary<string, float>();

        foreach (var cap in SprayCap.CreateDefaults())
        {
            // 1. Held still for 0.5 s at each distance: how big, how solid.
            var line = new StringBuilder($"  {cap.displayName,-8} hold 0.5 s:");
            foreach (var d in new[] { 0.05f, 0.15f, 0.3f, 0.6f, 1.0f, 2.0f })
            {
                PaintManager.BeginStroke("feel");
                for (var t = 0; t < 25; t++)
                    Paint(cap, new Vector3(0f, 1.4f, face - d), Vector3.forward, d, 1f);
                PaintManager.EndStroke("feel");
                var area = Painted(out var solid) * cm2;
                line.Append($" {d:F2}m={area:F0}cm2({solid * cm2:F0} solid)");
                PaintManager.Undo();
            }

            log.AppendLine(line.ToString());

            // 2. A 1 m sweep at three hand speeds, 0.3 m away: does the line break?
            var sweep = new StringBuilder($"  {cap.displayName,-8} sweep 1 m at 0.3 m:");
            foreach (var speed in new[] { 0.5f, 1.5f, 3f })
            {
                var ticks = Mathf.CeilToInt(1f / speed / interval);
                PaintManager.BeginStroke("feel");
                for (var t = 0; t <= ticks; t++)
                    Paint(cap, new Vector3(Mathf.Lerp(-0.5f, 0.5f, t / (float)ticks), 1.4f, face - 0.3f), Vector3.forward, 0.3f, 1f);
                PaintManager.EndStroke("feel");

                // Along the line's centre row: count gaps.
                Physics.Raycast(new Vector3(-0.5f, 1.4f, face - 0.3f), Vector3.forward, out var a, 2f);
                Physics.Raycast(new Vector3(0.5f, 1.4f, face - 0.3f), Vector3.forward, out var b, 2f);
                var row = Mathf.RoundToInt(a.textureCoord.y * size);
                var x0 = Mathf.RoundToInt(Mathf.Min(a.textureCoord.x, b.textureCoord.x) * size);
                var x1 = Mathf.RoundToInt(Mathf.Max(a.textureCoord.x, b.textureCoord.x) * size);
                var gaps = 0;
                var inGap = false;
                for (var x = x0; x <= x1; x++)
                {
                    var p = surface.Pixels[row * size + x];
                    var q = surface.BasePixels[row * size + x];
                    var bare = p.r == q.r && p.g == q.g && p.b == q.b;
                    if (bare && !inGap) gaps++;
                    inGap = bare;
                }

                sweep.Append($" {speed:F1}m/s={gaps} gap(s), {Painted(out _) * cm2:F0}cm2;");
                PaintManager.Undo();
            }

            log.AppendLine(sweep.ToString());

            // 3. Angle: the spot's width across and along the tilt, 0.3 m away.
            var tilt = new StringBuilder($"  {cap.displayName,-8} angle at 0.3 m:");
            foreach (var degrees in new[] { 0f, 45f, 70f })
            {
                var direction = Quaternion.Euler(0f, degrees, 0f) * Vector3.forward;
                var origin = new Vector3(0f, 1.4f, face) - direction * 0.3f;
                PaintManager.BeginStroke("feel");
                for (var t = 0; t < 10; t++)
                    Paint(cap, origin, direction, 0.3f, 1f);
                PaintManager.EndStroke("feel");

                // Shape from the paint's own coverage (darkness-weighted second moments), in meters per axis -
                // the face's texels are not square (3.6 m wide, 2.5 m tall on one 512 texture), and a bounding
                // box would measure the stray overspray droplets.
                double sw = 0, sx = 0, sy = 0, sxx = 0, syy = 0;
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var p = surface.Pixels[y * size + x];
                        var q = surface.BasePixels[y * size + x];
                        var weight = Mathf.Max(0, q.r - p.r) / 255.0;
                        if (weight <= 0) continue;
                        var mx = x / ppmX;
                        var my = y / ppmY;
                        sw += weight; sx += weight * mx; sy += weight * my; sxx += weight * mx * mx; syy += weight * my * my;
                    }

                var vx = sxx / sw - (sx / sw) * (sx / sw);
                var vy = syy / sw - (sy / sw) * (sy / sw);
                var w = (float)(2.0 * System.Math.Sqrt(System.Math.Max(0, vx)) * 100.0);
                var h = (float)(2.0 * System.Math.Sqrt(System.Math.Max(0, vy)) * 100.0);
                if (degrees == 0f) roundness[cap.displayName] = w / h;
                tilt.Append($" {degrees:F0}deg={w:F1}x{h:F1}cm(2sd);");
                PaintManager.Undo();
            }

            log.AppendLine(tilt.ToString());
        }

        foreach (var pair in roundness)
            Check(log, Mathf.Abs(pair.Value - 1f) < 0.12f, $"{pair.Key}: a head-on spray lands round", $"width/height {pair.Value:F2}");
        Check(log, surface.CountPaintedPixels() == 0, "measurements leave the wall clean (every pass undone)");
        yield return 0.1f;
    }

    // ---------- save data safety ----------

    public static IEnumerator SaveSafety(StringBuilder log)
    {
        var folder = CanSkinStore.Folder;
        var aside = folder + "_testbackup";
        if (Directory.Exists(aside)) Directory.Delete(aside, true);
        if (Directory.Exists(folder)) Directory.Move(folder, aside);

        var skin = GameObject.Find("SprayCan").GetComponent<CanSkin>();
        var canvas = skin.Canvas;
        typeof(CanSkin).GetField("index", Any).SetValue(skin, CanSkinStore.LoadIndex());
        skin.Load(null);

        // Two saved cans.
        for (var i = 0; i < 2; i++)
        {
            skin.Load(null);
            var pixels = (Color32[])canvas.BasePixels.Clone();
            for (var k = 1000 + i * 7000; k < 4000 + i * 7000; k++) pixels[k] = new Color32(200, 30, (byte)(i * 100), 255);
            canvas.ReplacePaint(pixels);
            yield return null;
            skin.Save();
        }

        var index = CanSkinStore.LoadIndex();
        Check(log, index.skins.Count == 2, "two cans saved");

        // 1. index.json unreadable (power cut mid-write, disk error): the cans must not silently disappear.
        var indexPath = Path.Combine(folder, "index.json");
        File.WriteAllText(indexPath, "{ this is not json");
        var afterCorrupt = CanSkinStore.LoadIndex();
        var corruptKept = Directory.GetFiles(folder, "index.json.corrupt*").Length > 0;
        Check(log, afterCorrupt.skins.Count == 2, "corrupt index.json: the saved cans are recovered from their files", $"{afterCorrupt.skins.Count} recovered");
        Check(log, corruptKept, "...and the unreadable index is kept aside, not overwritten");
        CanSkinStore.SaveIndex(afterCorrupt.skins.Count == 2 ? afterCorrupt : index);

        // 2. A write interrupted between removing the old file and putting the new one in place.
        index = CanSkinStore.LoadIndex();
        var entry = index.skins[0];
        var path = Path.Combine(folder, entry.paintFile);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        File.WriteAllBytes(path + ".bak", bytes);
        var afterCrash = CanSkinStore.LoadIndex();
        Check(log, afterCrash.Find(entry.id) != null && File.Exists(path), "interrupted save: the can comes back from its previous copy", $"entries {afterCrash.skins.Count}, file restored {File.Exists(path)}");

        // 3. Real-wall artwork set aside twice (the room was edited twice): both earlier versions kept.
        var wallId = "test-safety-wall";
        var wallPixels = new Color32[64 * 64];
        for (var i = 0; i < 300; i++) wallPixels[i] = new Color32(255, 0, 0, 255);
        MRWallStore.Write(wallId, new Vector2(2f, 2f), 64, wallPixels);
        MRWallStore.SetAside(wallId);
        MRWallStore.Write(wallId, new Vector2(3f, 2f), 64, wallPixels);
        MRWallStore.SetAside(wallId);
        var kept = Directory.GetFiles(MRWallStore.Folder, wallId + "*.old*").Length;
        Check(log, kept == 2, "wall artwork set aside twice: both earlier versions are kept", $"{kept} kept");
        foreach (var f in Directory.GetFiles(MRWallStore.Folder, wallId + "*")) File.Delete(f);

        // Back to the Editor's own data.
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        if (Directory.Exists(aside)) Directory.Move(aside, folder);
        typeof(CanSkin).GetField("index", Any).SetValue(skin, CanSkinStore.LoadIndex());
        yield return 0.1f;
    }
}
