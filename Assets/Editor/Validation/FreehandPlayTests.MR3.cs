using System.Collections;
using System.Reflection;
using System.Text;
using Freehand.Customization;
using Freehand.MR;
using Freehand.Paint;
using UnityEngine;
using UnityEngine.InputSystem;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
public static partial class FreehandPlayTests
{
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Any).GetValue(target);

    // The panel, the failure path, the permission path and the shortcut action, as far as the Editor can go.
    public static IEnumerator MRFlow(StringBuilder log)
    {
        var mr = GameObject.Find("MixedReality");
        var controller = mr.GetComponent<MRModeController>();
        var source = mr.GetComponent<ARPlaneSurfaceSource>();
        var hub = mr.GetComponent<MRSurfaceHub>();
        var panel = Object.FindFirstObjectByType<MRPanel>();
        var camera = Camera.main;

        // Starts in the virtual room with every AR piece off.
#if FREEHAND_MR
        var arSession = Object.FindFirstObjectByType<UnityEngine.XR.ARFoundation.ARSession>(FindObjectsInactive.Include);
        var arCamera = camera.GetComponent<UnityEngine.XR.ARFoundation.ARCameraManager>();
        var arPlanes = Object.FindFirstObjectByType<UnityEngine.XR.ARFoundation.ARPlaneManager>(FindObjectsInactive.Include);
        Check(log, controller.Mode == MRMode.Virtual && !arSession.enabled && !arCamera.enabled && !arPlanes.enabled, "game opens in VR: AR session, passthrough and planes are off");
#else
        // V0.15.1 build without the MR packages: no AR piece exists at all.
        Check(log, controller.Mode == MRMode.Virtual && GameObject.Find("AR Session") == null && !MRModeController.DeviceSupportsPassthrough(), "game opens in VR; this build has no AR session or passthrough at all");
#endif
        Check(log, source.Status == MRSurfaceStatus.Idle && hub.SurfaceCount == 0, "no wall surfaces and no plane query in VR");

        // The panel button: the same event the finger press raises.
        var modeButton = Field<WorkshopButton>(panel, "modeButton");
        var pressed = (System.Delegate)typeof(WorkshopButton).GetField("Pressed", Any).GetValue(modeButton);
        var flags = camera.clearFlags; var background = camera.backgroundColor; var hdr = camera.allowHDR;
        pressed.DynamicInvoke();
        yield return 1.2f;
        Check(log, controller.Mode == MRMode.Virtual && controller.Problem == MRProblem.Unsupported, "MODO button with no passthrough device: stays in VR, reason recorded", $"{controller.Mode}/{controller.Problem}");
        Check(log, camera.clearFlags == flags && camera.backgroundColor == background && camera.allowHDR == hdr, "camera untouched by the failed attempt", $"{camera.clearFlags} {camera.backgroundColor} hdr={camera.allowHDR}");
#if FREEHAND_MR
        Check(log, !arCamera.enabled && !arPlanes.enabled, "passthrough camera manager stays off after the failed attempt");
#endif
        panel.SendMessage("Update");
        var key = Field<string>(panel, "shownKey");
        Check(log, key != null && key.Contains("MR SO NO QUEST"), "panel tells the player why", key);

        // The wall workflow without the Quest: permission is not granted outside an Android device.
        source.Begin();
        yield return 0.3f;
#if FREEHAND_MR
        Check(log, source.Status == MRSurfaceStatus.PermissionDenied, "no scene permission: status is PermissionDenied, plane manager never started", source.Status.ToString());
        Check(log, !arPlanes.enabled, "plane manager not enabled without permission");
#else
        Check(log, source.Status == MRSurfaceStatus.Unavailable, "no MR packages: the wall source says Unavailable", source.Status.ToString());
#endif
        source.End();
        Check(log, source.Status == MRSurfaceStatus.Idle, "End() returns to idle");

        // The shortcut's actions exist and are bound to the thumbstick click.
        var actions = Field<InputActionAsset>(mr.GetComponent<MRModeInput>(), "inputActions");
        var map = actions.FindActionMap("XR");
        var left = map.FindAction("Left Thumbstick Click");
        var right = map.FindAction("Right Thumbstick Click");
        Check(log, left != null && right != null && left.bindings[0].path.EndsWith("thumbstickClicked") && right.bindings[0].path.EndsWith("thumbstickClicked"), "thumbstick click actions bound", left != null ? left.bindings[0].path : "-");

        // No object leaks over repeated walls: textures, materials, meshes back to where they were.
        var plane = MakeTestPlane("TestPlaneLeak", new Vector3(0f, 1.3f, 2.2f));
        var apply = typeof(MRModeController).GetMethod("ApplyMixedWorld", Any);
        var restore = typeof(MRModeController).GetMethod("ApplyVirtualWorld", Any);
        apply.Invoke(controller, null);
        yield return null;
        hub.Report("test-leak", new Vector2(3f, 2.2f), Vector2.zero, plane, true);
        yield return 0.4f;
        hub.ReleaseAll();
        yield return 0.5f;
        System.GC.Collect();
        yield return Resources.UnloadUnusedAssets();
        var textures0 = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
        var materials0 = Resources.FindObjectsOfTypeAll<Material>().Length;
        var meshes0 = Resources.FindObjectsOfTypeAll<Mesh>().Length;
        for (var round = 0; round < 6; round++)
        {
            hub.Report("test-leak", new Vector2(3f, 2.2f), Vector2.zero, plane, true);
            yield return 0.3f;
            PaintLine(new Vector3(-0.5f, 1.3f, 0.5f), new Vector3(0.5f, 1.3f, 0.5f), 10, Color.red, 0.05f, 1.2f, "l");
            yield return 0.1f;
            hub.ReleaseAll();
            yield return 0.3f;
        }

        System.GC.Collect();
        yield return Resources.UnloadUnusedAssets();
        var textures1 = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
        var materials1 = Resources.FindObjectsOfTypeAll<Material>().Length;
        var meshes1 = Resources.FindObjectsOfTypeAll<Mesh>().Length;
        Check(log, textures1 <= textures0 && materials1 <= materials0 && meshes1 <= meshes0, "6 rounds of create / paint / release leave no textures, materials or meshes behind", $"textures {textures0}->{textures1}, materials {materials0}->{materials1}, meshes {meshes0}->{meshes1}");
        restore.Invoke(controller, null);
        Object.Destroy(plane.gameObject);
        var f = WallFile("test-leak"); if (System.IO.File.Exists(f)) System.IO.File.Delete(f);

        // The world swap for a player who is not at the origin and not facing +Z.
        var origin = GameObject.Find("XR Origin").transform;
        var table = GameObject.Find("ToolTable").transform;
        var vrTable = table.position;
        origin.SetPositionAndRotation(new Vector3(1.5f, 0f, -2f), Quaternion.Euler(0f, 90f, 0f));
        yield return null;
        apply.Invoke(controller, null);
        var headFloor = new Vector3(camera.transform.position.x, 0f, camera.transform.position.z);
        var yaw = camera.transform.eulerAngles.y;
        var expected = headFloor + Quaternion.Euler(0f, yaw, 0f) * vrTable;
        Check(log, Vector3.Distance(table.position, expected) < 0.01f, "player at (1.5, -2) facing +X: the tool table is beside them, not where it was", $"head {camera.transform.position} yaw {yaw:F0} -> table {table.position}, expected {expected}");
        var player = (table.position - headFloor).magnitude;
        Check(log, player < 1.2f, "...within arm's reach of the player", $"{player:F2} m");
        origin.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        yield return null;
        restore.Invoke(controller, null);
        yield return null;
        Check(log, Vector3.Distance(table.position, vrTable) < 0.01f, "back in VR the table is in its virtual place again", $"{table.position}");
        yield return 0.1f;
    }
}
