using System.Collections;
using System.Text;
using Freehand.Customization;
using Freehand.Interaction;
using Freehand.Paint;
using Freehand.Spray;
using Freehand.VR;
using UnityEngine;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
// V0.14 freestyle: object ownership, toss and catch, pass between hands, interruption, and what must not change.
public static partial class FreehandPlayTests
{
    static VRGrabInteractable fsGrab;
    static CanFreestyle fsFree;
    static VRGrabber fsLeft, fsRight;
    static Transform fsRightController, fsLeftController;

    static readonly Vector3 RestRight = new Vector3(0.22f, 1.2f, 0.35f);
    static readonly Vector3 RestLeft = new Vector3(-0.22f, 1.2f, 0.35f);

    static RigidbodyInterpolation fsFreeInterpolation;

    static IEnumerator FreestyleSetup(StringBuilder log)
    {
        var can = GameObject.Find("SprayCan");
        fsGrab = can.GetComponent<VRGrabInteractable>();
        fsFreeInterpolation = can.GetComponent<Rigidbody>().interpolation;
        fsFree = can.GetComponent<CanFreestyle>();
        fsRightController = GameObject.Find("Right Controller").transform;
        fsLeftController = GameObject.Find("Left Controller").transform;
        fsRight = fsRightController.GetComponent<VRGrabber>();
        fsLeft = fsLeftController.GetComponent<VRGrabber>();
        rightPad = AddPad(false);
        leftPad = AddPad(true);
        SetPose(leftPad, RestLeft + new Vector3(-0.3f, 0f, 0f), Quaternion.identity);
        SetPose(rightPad, RestRight, Quaternion.identity);
        yield return 0.4f;

        var held = false;
        var canGrip = can.transform.position + Vector3.up * 0.12f;
        for (var attempt = 0; attempt < 12 && !held; attempt++)
        {
            SetPose(rightPad, canGrip + new Vector3(0f, -0.02f * (attempt % 6), 0.02f * (attempt / 6)), Quaternion.identity);
            yield return 0.15f;
            SetAxis(rightPad, "grip", 1f);
            yield return 0.25f;
            held = fsGrab.IsGrabbed;
            if (!held) { SetAxis(rightPad, "grip", 0f); yield return 0.15f; }
        }

        SetPose(rightPad, RestRight, Quaternion.Euler(-20f, 0f, 0f));
        yield return 0.4f;
        Check(log, held && fsFree != null, "setup: can held in the right hand, freestyle present");
    }

    static bool OneOwner() => !(fsLeft.Held == fsGrab && fsRight.Held == fsGrab);

    static float AttachError()
    {
        if (!fsGrab.AttachedToHand)
            return float.PositiveInfinity;
        var anchor = fsGrab.CurrentHand;
        return Vector3.Distance(fsGrab.transform.position, anchor.TransformPoint(fsGrab.AttachedLocalPosition));
    }

    static IEnumerator WaitIdle(float timeout)
    {
        var end = Time.realtimeSinceStartup + timeout;
        while (fsFree.Active && Time.realtimeSinceStartup < end)
            yield return null;
    }

    public static IEnumerator Freestyle(StringBuilder log)
    {
        yield return FreestyleSetup(log);
        if (!fsGrab.IsGrabbed)
        {
            RemovePads();
            yield break;
        }

        var body = fsGrab.GetComponent<Rigidbody>();
        var skin = fsGrab.GetComponent<CanSkin>();
        var skinHash = skin != null && skin.Canvas != null ? skin.Canvas.PixelHash() : 0u;
        var emitter = fsGrab.GetComponent<SprayEmitter>();

        // ---- 1. Toss and catch, 16 times, a still hand ----
        var catches0 = fsFree.Catches;
        var drops0 = fsFree.Drops;
        var worstJump = 0f;
        var everTwoOwners = false;
        var everSprayed = false;
        var highest = 0f;
        for (var i = 0; i < 16; i++)
        {
            fsFree.Play(i % 2 == 0 ? "TossFlip" : "TossSpin");
            var restY = fsGrab.transform.position.y;
            var previous = fsGrab.transform.position;
            var wasFlying = false;
            var end = Time.realtimeSinceStartup + 4f;
            while (fsFree.Active && Time.realtimeSinceStartup < end)
            {
                yield return null;
                var now = fsGrab.transform.position;
                if (wasFlying && fsGrab.AttachedToHand)
                    worstJump = Mathf.Max(worstJump, AttachError());
                wasFlying |= fsGrab.InFlight;
                highest = Mathf.Max(highest, now.y - restY);
                everTwoOwners |= !OneOwner();
                everSprayed |= emitter != null && emitter.IsSpraying;
                previous = now;
            }
        }

        Check(log, fsFree.Catches - catches0 == 16 && fsFree.Drops == drops0, "16 tosses with a still hand: 16 caught, none dropped", $"caught {fsFree.Catches - catches0}, dropped {fsFree.Drops - drops0}, apex up to {highest:F2} m");
        Check(log, worstJump < 0.002f, "the catch lands exactly in the hand's grip (no jump)", $"worst attach error right after a catch {worstJump * 1000f:F2} mm");
        Check(log, !everTwoOwners, "never two hands owning the can");
        Check(log, !everSprayed, "no spraying during the moves");
        Check(log, fsGrab.IsGrabbed && !fsGrab.InFlight && body.isKinematic && fsRight.Held == fsGrab, "after the tosses: held by the right hand, kinematic, not in flight");

        // ---- 2. Letting go of the grip in mid-air drops it ----
        fsFree.Play("TossFlip");
        var t0 = Time.realtimeSinceStartup;
        while (!fsGrab.InFlight && Time.realtimeSinceStartup - t0 < 1.5f)
            yield return null;
        yield return 0.12f;
        SetAxis(rightPad, "grip", 0f);
        yield return 0.1f;
        Check(log, !fsGrab.IsGrabbed && !fsGrab.InFlight && !body.isKinematic && fsRight.Held == null && !fsFree.Active, "grip let go mid-air: the can is dropped (a physics body), the run ends", $"kinematic={body.isKinematic} free={fsFree.Current}");
        var yBefore = fsGrab.transform.position.y;
        yield return 0.3f;
        Check(log, fsGrab.transform.position.y < yBefore - 0.05f, "...and it falls", $"{yBefore:F2} -> {fsGrab.transform.position.y:F2}");
        yield return 1.0f;

        // Grab it again where it lies.
        var held = false;
        for (var attempt = 0; attempt < 14 && !held; attempt++)
        {
            SetPose(rightPad, fsGrab.transform.position + Vector3.up * (0.12f - 0.02f * (attempt % 7)), Quaternion.identity);
            yield return 0.15f;
            SetAxis(rightPad, "grip", 1f);
            yield return 0.25f;
            held = fsGrab.IsGrabbed;
            if (!held) { SetAxis(rightPad, "grip", 0f); yield return 0.15f; }
        }

        Check(log, held, "grabbing it again right after a drop works");
        SetPose(rightPad, RestRight, Quaternion.Euler(-20f, 0f, 0f));
        yield return 0.5f;
        var interpolation = fsFreeInterpolation;

        // ---- 3. The hand moves far away mid-air: a miss ----
        fsFree.Play("TossSpin");
        t0 = Time.realtimeSinceStartup;
        while (!fsGrab.InFlight && Time.realtimeSinceStartup - t0 < 1.5f)
            yield return null;
        SetPose(rightPad, RestRight + new Vector3(0.6f, -0.2f, 0f), Quaternion.Euler(-20f, 0f, 0f));
        yield return 0.8f;
        Check(log, !fsGrab.IsGrabbed && !body.isKinematic, "hand gone from under the can: it is not magically caught, it falls");
        SetAxis(rightPad, "grip", 0f);
        yield return 1.0f;
        held = false;
        for (var attempt = 0; attempt < 14 && !held; attempt++)
        {
            SetPose(rightPad, fsGrab.transform.position + Vector3.up * (0.12f - 0.02f * (attempt % 7)), Quaternion.identity);
            yield return 0.15f;
            SetAxis(rightPad, "grip", 1f);
            yield return 0.25f;
            held = fsGrab.IsGrabbed;
            if (!held) { SetAxis(rightPad, "grip", 0f); yield return 0.15f; }
        }

        SetPose(rightPad, RestRight, Quaternion.Euler(-20f, 0f, 0f));
        yield return 0.5f;
        SetAxis(rightPad, "grip", 0f);
        yield return 0.4f;
        Check(log, body.interpolation == interpolation && !body.isKinematic, "released normally after a drop and a regrab: rigidbody back to its free settings", $"interpolation {body.interpolation}");
        yield return 0.8f;
        held = false;
        for (var attempt = 0; attempt < 14 && !held; attempt++)
        {
            SetPose(rightPad, fsGrab.transform.position + Vector3.up * (0.12f - 0.02f * (attempt % 7)), Quaternion.identity);
            yield return 0.15f;
            SetAxis(rightPad, "grip", 1f);
            yield return 0.25f;
            held = fsGrab.IsGrabbed;
            if (!held) { SetAxis(rightPad, "grip", 0f); yield return 0.15f; }
        }

        SetPose(rightPad, RestRight, Quaternion.Euler(-20f, 0f, 0f));
        yield return 0.5f;

        // ---- 4. Pass to the other hand (ready: empty, close, grip closed) ----
        SetPose(leftPad, RestLeft, Quaternion.Euler(-20f, 0f, 0f));
        SetAxis(leftPad, "grip", 1f);
        yield return 0.4f;
        everTwoOwners = false;
        var passed = fsFree.Play("Pass");
        var passEnd = Time.realtimeSinceStartup + 3f;
        var worstPassJump = 0f;
        var flew = false;
        while (fsFree.Active && Time.realtimeSinceStartup < passEnd)
        {
            yield return null;
            everTwoOwners |= !OneOwner();
            if (flew && fsGrab.AttachedToHand)
                worstPassJump = Mathf.Max(worstPassJump, AttachError());
            flew |= fsGrab.InFlight;
        }

        Check(log, passed && flew && fsLeft.Held == fsGrab && fsRight.Held == null && fsGrab.HeldByLeftHand, "pass: the can flies across and the left hand is now its only holder", $"left holds={fsLeft.Held == fsGrab} right holds={fsRight.Held == fsGrab}");
        Check(log, !everTwoOwners && worstPassJump < 0.002f, "pass: never two owners, no jump at the catch", $"{worstPassJump * 1000f:F2} mm");

        // And back, so the rest runs in the right hand.
        SetAxis(rightPad, "grip", 1f);
        yield return 0.3f;
        fsFree.Play("Pass");
        yield return WaitIdle(3f);
        Check(log, fsRight.Held == fsGrab && fsLeft.Held == null, "pass back to the right hand");
        SetAxis(leftPad, "grip", 0f);
        yield return 0.3f;

        // ---- 5. The other hand takes it out of the air ----
        fsFree.Play("TossFlip");
        t0 = Time.realtimeSinceStartup;
        while (!fsGrab.InFlight && Time.realtimeSinceStartup - t0 < 1.5f)
            yield return null;
        yield return 0.1f;
        SetPose(leftPad, fsGrab.transform.position, Quaternion.identity);
        yield return null;
        SetAxis(leftPad, "grip", 1f);
        yield return 0.3f;
        Check(log, fsLeft.Held == fsGrab && fsRight.Held == null && !fsFree.Active, "the other hand grabbing it mid-air takes it; the run stops", $"left={fsLeft.Held == fsGrab} right={fsRight.Held == fsGrab} free={fsFree.Current}");
        // Back to the right hand by a pass.
        SetPose(leftPad, RestLeft, Quaternion.Euler(-20f, 0f, 0f));
        SetAxis(rightPad, "grip", 1f);
        yield return 0.4f;
        fsFree.Play("Pass");
        yield return WaitIdle(3f);
        SetAxis(leftPad, "grip", 0f);
        yield return 0.3f;
        Check(log, fsRight.Held == fsGrab, "back in the right hand");

        // ---- 6. Start and cancel, 30 times, at random moments ----
        var inspection = fsGrab.GetComponent<ItemInspection>();
        for (var i = 0; i < 30; i++)
        {
            fsFree.Request();
            yield return Random.Range(0.02f, 0.7f);
            fsFree.Request();
            yield return WaitIdle(4f);
        }

        yield return 0.4f;
        var clean = !fsFree.Active && fsGrab.AttachedToHand && !fsGrab.Presenting && !fsGrab.InFlight && (inspection == null || !inspection.Active)
                    && emitter != null && !emitter.InspectionLock && !emitter.FreestyleLock && !fsLeft.CatchReady && !fsRight.CatchReady && AttachError() < 0.001f;
        Check(log, clean, "30 starts and cancels: nothing left over (no lock, no presentation, no open hand, can at its grip)", $"active={fsFree.Active} presenting={fsGrab.Presenting} locks={emitter.InspectionLock}/{emitter.FreestyleLock} attachErr={AttachError() * 1000f:F2}mm");

        // ---- 7. Spray straight after (and during) a run ----
        var wall = GameObject.Find("PaintWall").GetComponent<PaintSurface>();
        var before = wall.CountPaintedPixels();
        fsFree.Play("Roll,Fidget");
        yield return 0.5f;
        yield return Aim(fsRightController, emitter.SprayOrigin, new Vector3(0f, 1.4f, 0.3f), Vector3.forward);
        SetAxis(rightPad, "trigger", 1f);
        // Time, not frames: a headless Editor runs uncapped.
        var spraying = -1f;
        var t1 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t1 < 1f)
        {
            if (spraying < 0f && emitter.IsSpraying)
                spraying = Time.realtimeSinceStartup - t1;
            yield return null;
        }

        Check(log, spraying >= 0f && spraying < 0.25f && !fsFree.Active, "the trigger ends a run and the spray starts within a quarter second", spraying >= 0f ? $"{spraying * 1000f:F0} ms" : "never sprayed");
        for (var i = 0; i < 25; i++)
            yield return Aim(fsRightController, emitter.SprayOrigin, new Vector3(Mathf.Lerp(-0.3f, 0.3f, i / 24f), 1.4f, 0.3f), Vector3.forward);
        log.AppendLine($"  [diag] spraying={emitter.IsSpraying} raw={emitter.RawTriggerValue:F2} insp={emitter.InspectionLock} free={emitter.FreestyleLock} supp={emitter.Suppressed} cap={emitter.HasCap} hit={(emitter.LastHitCollider != null ? emitter.LastHitCollider.name : "-")} origin={emitter.SprayOrigin.position} fwd={emitter.SprayOrigin.forward} run={fsFree.Current} grabbed={fsGrab.IsGrabbed} left={fsGrab.HeldByLeftHand}");
        SetAxis(rightPad, "trigger", 0f);
        yield return 0.3f;
        Check(log, wall.CountPaintedPixels() > before + 500, "...and the paint lands on the wall", $"{wall.CountPaintedPixels() - before} texels");
        PaintManager.Undo();
        SetPose(rightPad, RestRight, Quaternion.Euler(-20f, 0f, 0f));
        yield return 0.4f;

        // ---- 8. The can's skin and cap after all of it ----
        var skinAfter = skin != null && skin.Canvas != null ? skin.Canvas.PixelHash() : 0u;
        Check(log, skinAfter == skinHash, "the can's paint (skin) is untouched by the moves");
        var socket = fsGrab.GetComponentInChildren<CapSocket>(true);
        Check(log, socket != null && socket.Installed != null, "the cap is still seated on the can");

        // ---- 10. Take the cap off with the other hand after the moves, and put it back ----
        var rig = Object.FindAnyObjectByType<HandRig>();
        var cap = socket != null ? socket.Installed : null;
        if (cap != null && rig != null && rig.PinchPoint(true).HasValue)
        {
            var capCenter = cap.GetComponent<Renderer>() != null ? cap.GetComponent<Renderer>().bounds.center : cap.transform.position;
            var offset = rig.PinchPoint(true).Value - fsLeftController.position;
            var gripAt = capCenter - offset;
            SetPose(leftPad, gripAt, fsLeftController.rotation);
            yield return 0.3f;
            offset = rig.PinchPoint(true).Value - fsLeftController.position;
            gripAt = capCenter - offset;
            SetPose(leftPad, gripAt, fsLeftController.rotation);
            yield return 0.2f;
            SetAxis(leftPad, "grip", 1f);
            yield return 0.25f;
            var gripped = cap.State == CapState.Gripped;
            var up = socket.InstallParent.up;
            for (var i = 1; i <= 12; i++)
            {
                SetPose(leftPad, gripAt + up * (0.01f * i), fsLeftController.rotation);
                yield return 0.04f;
            }

            yield return 0.2f;
            var removed = socket.Installed == null && (cap.State == CapState.Held || cap.State == CapState.AvailableForInstall) && fsLeft.Held != null;
            Check(log, gripped && removed, "after the moves: the other hand pulls the cap off the stem", $"gripped={gripped} state={cap.State} installed={(socket.Installed != null)}");

            SetPose(leftPad, gripAt + up * 0.005f, fsLeftController.rotation);
            yield return 0.3f;
            SetAxis(leftPad, "grip", 0f);
            yield return 0.8f;
            Check(log, socket.Installed == cap && cap.State == CapState.Installed, "...and puts it back on", $"state={cap.State}");
            SetPose(leftPad, RestLeft + new Vector3(-0.3f, 0f, 0f), Quaternion.identity);
            yield return 0.3f;
        }
        else
        {
            Check(log, false, "cap test could not run (no seated cap or no rendered hand)");
        }

        // ---- 9. Allocation: the main thread's bytes per frame, still hand, idle vs during tosses ----
        var idleBytes = 0L; var idleFrames = 0;
        for (var i = 0; i < 90; i++)
        {
            var a0 = System.GC.GetAllocatedBytesForCurrentThread();
            yield return null;
            idleBytes += System.GC.GetAllocatedBytesForCurrentThread() - a0;
            idleFrames++;
        }

        var runBytes = 0L; var runFrames = 0;
        for (var i = 0; i < 6; i++)
        {
            fsFree.Play("TossSpin");
            while (fsFree.Active)
            {
                var a0 = System.GC.GetAllocatedBytesForCurrentThread();
                yield return null;
                runBytes += System.GC.GetAllocatedBytesForCurrentThread() - a0;
                runFrames++;
            }
        }

        log.AppendLine($"  allocation per frame (main thread, Editor): idle {idleBytes / (float)idleFrames:F0} B, during tosses {runBytes / (float)Mathf.Max(1, runFrames):F0} B over {runFrames} frames");

        SetAxis(rightPad, "grip", 0f);
        yield return 0.5f;
        RemovePads();
    }

    // Graffiti feel: where the spray's mist goes. Counts the live particles and how many are past the wall's
    // face (inside or behind it) while spraying the virtual wall head-on from close up and from a metre.
    public static IEnumerator SprayMist(StringBuilder log)
    {
        yield return FreestyleSetup(log);
        var emitter = fsGrab.GetComponent<SprayEmitter>();
        var system = emitter.SprayOrigin.GetComponentInChildren<ParticleSystem>();
        var wall = GameObject.Find("PaintWall");
        var face = wall.GetComponent<Collider>().bounds.min.z;
        var buffer = new ParticleSystem.Particle[512];

        foreach (var distance in new[] { 0.15f, 0.3f, 1.0f })
        {
            yield return Aim(fsRightController, emitter.SprayOrigin, new Vector3(0f, 1.4f, face - distance), Vector3.forward);
            SetAxis(rightPad, "trigger", 1f);
            var t0 = Time.realtimeSinceStartup;
            int samples = 0, maxAlive = 0, maxPast = 0;
            float sumPastFraction = 0f, deepest = 0f;
            while (Time.realtimeSinceStartup - t0 < 1.2f)
            {
                yield return Aim(fsRightController, emitter.SprayOrigin, new Vector3(0f, 1.4f, face - distance), Vector3.forward);
                var alive = system.GetParticles(buffer);
                var past = 0;
                for (var i = 0; i < alive; i++)
                {
                    var depth = buffer[i].position.z - face;
                    if (depth > 0.005f)
                    {
                        past++;
                        deepest = Mathf.Max(deepest, depth);
                    }
                }

                if (Time.realtimeSinceStartup - t0 > 0.4f && alive > 0)
                {
                    samples++;
                    sumPastFraction += past / (float)alive;
                    maxAlive = Mathf.Max(maxAlive, alive);
                    maxPast = Mathf.Max(maxPast, past);
                }
            }

            SetAxis(rightPad, "trigger", 0f);
            yield return 0.8f;
            log.AppendLine($"  {distance:F2} m from the wall: up to {maxAlive} particles alive, {100f * sumPastFraction / Mathf.Max(1, samples):F0}% of them past the wall face on average (max {maxPast}), deepest {deepest:F2} m inside/behind it");
            MRSprayMistResult[distance] = sumPastFraction / Mathf.Max(1, samples);
        }

        PaintManager.Undo();
        SetAxis(rightPad, "grip", 0f);
        yield return 0.4f;
        RemovePads();
    }

    // What the moves look like: frames of a toss and of a pass, from where the player's eyes are. Needs a GPU
    // (tools/headless.ps1 -Graphics): still batchmode, still no window.
    public static IEnumerator FreestyleLook(StringBuilder log)
    {
        yield return FreestyleSetup(log);
        var camera = Camera.main;
        var eye = new Vector3(0f, 1.6f, -0.05f);
        Quaternion Look(Vector3 at) => Quaternion.LookRotation((at - eye).normalized);
        var bg = new Color(0.32f, 0.33f, 0.35f, 1f);

        SetPose(rightPad, RestRight, Quaternion.Euler(-35f, -10f, 0f));
        yield return 0.4f;
        Shot(camera, "fs_00_rest", eye, Look(fsGrab.transform.position + Vector3.up * 0.1f), bg, 960, 720, 60f);

        fsFree.Play("TossFlip");
        var frames = new[] { 0.12f, 0.3f, 0.42f, 0.52f, 0.62f, 0.72f, 0.95f };
        var t0 = Time.realtimeSinceStartup;
        var shot = 0;
        while (fsFree.Active && shot < frames.Length)
        {
            if (Time.realtimeSinceStartup - t0 >= frames[shot])
            {
                Shot(camera, $"fs_toss_{shot:00}_{fsFree.Current}", eye, Look(RestRight + Vector3.up * 0.2f), bg, 960, 720, 60f);
                shot++;
            }

            yield return null;
        }

        yield return WaitIdle(3f);

        // The same toss from the side, more often, to see the whole arc.
        var side = new Vector3(0.95f, 1.45f, 0.35f);
        fsFree.Play("TossFlip");
        t0 = Time.realtimeSinceStartup;
        shot = 0;
        var sideFrames = new[] { 0.2f, 0.26f, 0.32f, 0.38f, 0.44f, 0.5f };
        while (shot < sideFrames.Length)
        {
            if (Time.realtimeSinceStartup - t0 >= sideFrames[shot])
            {
                var shotRotation = Quaternion.LookRotation((RestRight + Vector3.up * 0.22f - side).normalized);
                log.AppendLine($"  side {shot}: state {fsFree.Current} can pos {fsGrab.transform.position} rot {fsGrab.transform.rotation.eulerAngles} scale {fsGrab.transform.lossyScale}");
                Shot(camera, $"fs_side_{shot:00}_{fsFree.Current}", side, shotRotation, bg, 960, 720, 50f);
                shot++;
            }

            yield return null;
        }

        yield return WaitIdle(3f);
        SetPose(leftPad, RestLeft, Quaternion.Euler(-35f, 10f, 0f));
        SetAxis(leftPad, "grip", 1f);
        yield return 0.4f;
        fsFree.Play("Pass");
        t0 = Time.realtimeSinceStartup;
        shot = 0;
        var passFrames = new[] { 0.1f, 0.25f, 0.38f, 0.5f, 0.75f };
        while (shot < passFrames.Length)
        {
            if (Time.realtimeSinceStartup - t0 >= passFrames[shot])
            {
                Shot(camera, $"fs_pass_{shot:00}_{fsFree.Current}", eye, Look((RestRight + RestLeft) * 0.5f + Vector3.up * 0.05f), bg, 960, 720, 60f);
                shot++;
            }

            yield return null;
        }

        yield return WaitIdle(3f);
        fsFree.Play("Roll");
        yield return 0.45f;
        Shot(camera, "fs_roll_mid", eye, Look(fsGrab.transform.position + Vector3.up * 0.08f), bg, 960, 720, 50f);
        yield return WaitIdle(3f);
        log.AppendLine("  shots: " + ShotDir);
        SetAxis(leftPad, "grip", 0f);
        SetAxis(rightPad, "grip", 0f);
        yield return 0.3f;
        RemovePads();
    }

    static readonly System.Collections.Generic.Dictionary<float, float> MRSprayMistResult = new System.Collections.Generic.Dictionary<float, float>();
}
