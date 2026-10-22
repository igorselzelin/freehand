using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using Freehand.MR;
using Freehand.Paint;
using Freehand.Spray;
using Freehand.VR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.XR;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
public static partial class FreehandPlayTests
{
    const string TestLayout = @"{
        ""name"": ""FHTestController"",
        ""extend"": ""XRController"",
        ""controls"": [
            { ""name"": ""grip"", ""layout"": ""Axis"", ""usages"": [""Grip""] },
            { ""name"": ""trigger"", ""layout"": ""Axis"", ""usages"": [""Trigger""] },
            { ""name"": ""primaryButton"", ""layout"": ""Axis"", ""usages"": [""PrimaryButton""] },
            { ""name"": ""secondaryButton"", ""layout"": ""Axis"", ""usages"": [""SecondaryButton""] },
            { ""name"": ""thumbstickClicked"", ""layout"": ""Axis"" },
            { ""name"": ""thumbstick"", ""layout"": ""Stick"", ""usages"": [""Primary2DAxis""] }
        ]
    }";

    static InputDevice leftPad, rightPad;

    static InputDevice AddPad(bool left)
    {
        if (InputSystem.LoadLayout("FHTestController") == null)
            InputSystem.RegisterLayout(TestLayout);
        foreach (var old in InputSystem.devices.ToArray())
        {
            if (old.layout == "FHTestController" && old.usages.Contains(left ? UnityEngine.InputSystem.CommonUsages.LeftHand : UnityEngine.InputSystem.CommonUsages.RightHand))
                InputSystem.RemoveDevice(old);
        }

        var device = InputSystem.AddDevice("FHTestController");
        InputSystem.SetDeviceUsage(device, left ? UnityEngine.InputSystem.CommonUsages.LeftHand : UnityEngine.InputSystem.CommonUsages.RightHand);
        InputSystem.QueueDeltaStateEvent((IntegerControl)device["trackingState"], 3);
        InputSystem.QueueDeltaStateEvent((ButtonControl)device["isTracked"], true);
        InputSystem.Update();
        return device;
    }

    static void SetAxis(InputDevice device, string name, float value)
    {
        InputSystem.QueueDeltaStateEvent((AxisControl)device[name], value);
        InputSystem.Update();
    }

    static void SetPose(InputDevice device, Vector3 position, Quaternion rotation)
    {
        // The controller object lives under the XR Origin (at the world origin here), so device space = world space.
        InputSystem.QueueDeltaStateEvent((Vector3Control)device["devicePosition"], position);
        InputSystem.QueueDeltaStateEvent((QuaternionControl)device["deviceRotation"], rotation);
        InputSystem.Update();
    }

    static void RemovePads()
    {
        if (rightPad != null) InputSystem.RemoveDevice(rightPad);
        if (leftPad != null) InputSystem.RemoveDevice(leftPad);
        rightPad = leftPad = null;
    }

    // Move the right controller so the can's spray origin is at `origin` pointing along `direction`.
    static IEnumerator Aim(Transform controller, Transform sprayOrigin, Vector3 origin, Vector3 direction)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var q = Quaternion.FromToRotation(sprayOrigin.forward, direction);
            var rotation = q * controller.rotation;
            var position = controller.position + (origin - sprayOrigin.position);
            // rotate about the spray origin, not the controller
            position = origin + q * (controller.position - sprayOrigin.position);
            SetPose(rightPad, position, rotation);
            yield return null;
            yield return null;
        }
    }

    // The real can, held by a real (virtual) controller, sprays the virtual wall in VR and a detected wall in MR.
    public static IEnumerator E2ESpray(StringBuilder log)
    {
        var mr = GameObject.Find("MixedReality");
        var controller = mr.GetComponent<MRModeController>();
        var hub = mr.GetComponent<MRSurfaceHub>();
        var apply = typeof(MRModeController).GetMethod("ApplyMixedWorld", Any);
        var restore = typeof(MRModeController).GetMethod("ApplyVirtualWorld", Any);
        var can = GameObject.Find("SprayCan");
        var grab = can.GetComponent<VRGrabInteractable>();
        var emitter = can.GetComponentInChildren<SprayEmitter>(true);
        var rightController = GameObject.Find("Right Controller").transform;
        var vrWall = GameObject.Find("PaintWall").GetComponent<PaintSurface>();

        rightPad = AddPad(false);
        leftPad = AddPad(true);
        SetPose(leftPad, new Vector3(-0.3f, 1.0f, 0.2f), Quaternion.identity);
        SetPose(rightPad, new Vector3(0.3f, 1.2f, 0.3f), Quaternion.identity);
        yield return 0.5f;
        Check(log, Vector3.Distance(rightController.position, new Vector3(0.3f, 1.2f, 0.3f)) < 0.02f, "virtual controller drives the rig's right controller", $"{rightController.position}");

        // ---- VR: pick up the can from the table, spray the virtual wall ----
        var canGrip = can.transform.position + Vector3.up * 0.12f;
        var held = false;
        for (var attempt = 0; attempt < 12 && !held; attempt++)
        {
            SetPose(rightPad, canGrip + new Vector3(0f, -0.02f * (attempt % 6), 0.02f * (attempt / 6)), Quaternion.identity);
            yield return 0.15f;
            SetAxis(rightPad, "grip", 1f);
            yield return 0.25f;
            held = grab.IsGrabbed;
            if (!held) { SetAxis(rightPad, "grip", 0f); yield return 0.15f; }
        }

        Check(log, held, "can picked up with the grip (VR world)", held ? "held" : "not grabbed");
        if (!held)
        {
            RemovePads();
            yield break;
        }

        yield return Aim(rightController, emitter.SprayOrigin, new Vector3(0f, 1.4f, 0.3f), Vector3.forward);
        log.AppendLine($"  [diag] after aim: origin {emitter.SprayOrigin.position} fwd {emitter.SprayOrigin.forward}, controller {rightController.position} rot {rightController.rotation.eulerAngles}, held={grab.IsGrabbed}, can {can.transform.position}");
        var painted0 = vrWall.CountPaintedPixels();
        SetAxis(rightPad, "trigger", 1f);
        for (var i = 0; i < 40; i++)
        {
            yield return Aim(rightController, emitter.SprayOrigin, new Vector3(Mathf.Lerp(-0.5f, 0.5f, i / 39f), 1.4f, 0.3f), Vector3.forward);
        }

        log.AppendLine($"  [diag] spraying={emitter.IsSpraying} raw={emitter.RawTriggerValue} intensity={emitter.Intensity} suppressed={emitter.Suppressed} inspLock={emitter.InspectionLock} hasCap={emitter.HasCap} hit={emitter.LastHitDistance:F2} origin {emitter.SprayOrigin.position} fwd {emitter.SprayOrigin.forward}");
        SetAxis(rightPad, "trigger", 0f);
        yield return 0.3f;
        var vrPainted = vrWall.CountPaintedPixels() - painted0;
        Check(log, vrPainted > 800 && emitter.LastHitCollider != null, "VR: the can sprays the virtual wall through the real emitter", $"{vrPainted} texels, hit {(emitter.LastHitCollider != null ? emitter.LastHitCollider.name : "-")}");
        Check(log, PaintHistory.UndoCount >= 1, "VR: the stroke is an undo step", $"{PaintHistory.UndoCount}");

        // B = undo (secondary button)
        SetAxis(rightPad, "secondaryButton", 1f);
        yield return 0.15f;
        SetAxis(rightPad, "secondaryButton", 0f);
        yield return 0.3f;
        Check(log, vrWall.CountPaintedPixels() == painted0, "VR: B undoes the stroke", $"{vrWall.CountPaintedPixels()} vs {painted0}");

        // ---- MR: same can, same hand, a detected wall ----
        SetAxis(rightPad, "grip", 0f);
        yield return 0.6f; // let the can settle on whatever it fell onto
        apply.Invoke(controller, null);
        yield return 0.5f;
        var plane = MakeTestPlane("TestPlaneE2E", new Vector3(0f, 1.3f, 2.4f));
        hub.Report("test-e2e", new Vector2(3.2f, 2.3f), Vector2.zero, plane, true);
        yield return 0.5f;
        var wall = WaitSurface(hub, "test-e2e");
        Check(log, wall != null, "MR: wall surface exists");

        held = false;
        canGrip = can.transform.position + Vector3.up * 0.12f;
        for (var attempt = 0; attempt < 12 && !held; attempt++)
        {
            SetPose(rightPad, canGrip + new Vector3(0f, -0.02f * (attempt % 6), 0.02f * (attempt / 6)), Quaternion.identity);
            yield return 0.15f;
            SetAxis(rightPad, "grip", 1f);
            yield return 0.25f;
            held = grab.IsGrabbed;
            if (!held) { SetAxis(rightPad, "grip", 0f); yield return 0.15f; }
        }

        Check(log, held, "MR: can picked up from the relocated table", held ? $"can at {can.transform.position}" : "not grabbed");
        if (held && wall != null)
        {
            yield return Aim(rightController, emitter.SprayOrigin, new Vector3(0f, 1.4f, 0.5f), Vector3.forward);
            SetAxis(rightPad, "trigger", 1f);
            for (var i = 0; i < 50; i++)
                yield return Aim(rightController, emitter.SprayOrigin, new Vector3(Mathf.Lerp(-0.7f, 0.7f, i / 49f), 1.4f + 0.2f * Mathf.Sin(i * 0.3f), 0.5f), Vector3.forward);
            SetAxis(rightPad, "trigger", 0f);
            yield return 0.3f;
            var mrPainted = wall.Paint.CountPaintedPixels();
            Check(log, mrPainted > 800, "MR: the real can and emitter paint the detected wall", $"{mrPainted} texels; last hit {(emitter.LastHitCollider != null ? emitter.LastHitCollider.name : "-")}");
            Check(log, vrWall.CountPaintedPixels() == painted0, "MR: the hidden virtual wall received nothing");
            Check(log, PaintHistory.UndoCount == 1, "MR: one stroke, one undo step", $"{PaintHistory.UndoCount}");

            // Too far: the wall behind the spray range takes no paint.
            var before = wall.Paint.CountPaintedPixels();
            yield return Aim(rightController, emitter.SprayOrigin, new Vector3(0f, 1.4f, -2.5f), Vector3.forward);
            SetAxis(rightPad, "trigger", 1f);
            yield return 0.6f;
            SetAxis(rightPad, "trigger", 0f);
            Check(log, wall.Paint.CountPaintedPixels() == before, "MR: out of spray range paints nothing", $"max distance {emitter.MaxDistance:F1} m");

            // Undo, then Clear All by holding B.
            SetAxis(rightPad, "secondaryButton", 1f);
            yield return 0.15f;
            SetAxis(rightPad, "secondaryButton", 0f);
            yield return 0.3f;
            Check(log, wall.Paint.CountPaintedPixels() == 0, "MR: B undoes the stroke on the real wall", $"{wall.Paint.CountPaintedPixels()}");
        }

        // Leave.
        SetAxis(rightPad, "grip", 0f);
        yield return 0.4f;
        hub.ReleaseAll();
        restore.Invoke(controller, null);
        Object.Destroy(plane.gameObject);
        var f = WallFile("test-e2e"); if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
        yield return 0.3f;
        RemovePads();
    }
}
