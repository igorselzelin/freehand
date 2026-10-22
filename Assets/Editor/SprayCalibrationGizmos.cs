using Freehand.Spray;
using UnityEditor;
using UnityEngine;

// Scene-view calibration overlay for the spray origin - so the "where does
// the spray actually leave the can" question is answered by looking, not by
// moving SprayOrigin until it seems right.
//
// For the can (drawn whenever gizmos are on, in Edit and Play Mode):
//   yellow dot + ring  SprayOrigin, the exact emission point
//   green arrow        spray direction (SprayOrigin.forward)
//   cyan line / dot    the first stretch of the paint raycast, to its hit
//   red/green/blue     NozzlePivot's X/Y/Z axes (Z = the can's front)
//   red line           seated cap's orifice != SprayOrigin (should never show)
//   label              only while the can is selected
// For every cap: a magenta arrow on its orifice.
// For the socket: the snap zone a released cap must be inside to seat.
static class SprayCalibrationGizmos
{
    const float AxisLength = 0.025f;
    const float ArrowLength = 0.08f;

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
    static void DrawEmitter(SprayEmitter emitter, GizmoType type)
    {
        var origin = emitter.SprayOrigin;
        if (origin == null)
            return;

        var position = origin.position;
        var forward = origin.forward;

        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(position, 0.0015f);
        Gizmos.DrawWireSphere(position, 0.006f);

        Handles.color = Color.green;
        Handles.DrawLine(position, position + forward * ArrowLength, 2f);
        Handles.ConeHandleCap(0, position + forward * ArrowLength, Quaternion.LookRotation(forward), 0.012f, EventType.Repaint);

        if (Physics.Raycast(position, forward, out var hit, emitter.MaxDistance, emitter.PaintableMask, QueryTriggerInteraction.Ignore))
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(position, hit.point);
            Gizmos.DrawWireSphere(hit.point, 0.02f);
        }
        else
        {
            Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
            Gizmos.DrawLine(position, position + forward * emitter.MaxDistance);
        }

        var pivot = origin.parent;
        if (pivot != null)
        {
            var p = pivot.position;
            Handles.color = Color.red;
            Handles.DrawLine(p, p + pivot.right * AxisLength);
            Handles.color = Color.green;
            Handles.DrawLine(p, p + pivot.up * AxisLength);
            Handles.color = Color.blue;
            Handles.DrawLine(p, p + pivot.forward * AxisLength);
        }

        var socket = emitter.GetComponentInChildren<CapSocket>(true);
        var seated = socket != null ? (Application.isPlaying ? socket.Installed : SeatedInEditMode(socket)) : null;
        if (seated != null && seated.Orifice != null && Vector3.Distance(seated.Orifice.position, position) > 0.001f)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawLine(seated.Orifice.position, position);
        }

        if ((type & GizmoType.Selected) != 0)
        {
            Handles.color = Color.white;
            Handles.Label(position + Vector3.up * 0.02f,
                $"SprayOrigin\nforward {forward.x:F2}, {forward.y:F2}, {forward.z:F2}\n{(seated != null ? $"cap: {seated.name}" : "sem cap")}");
        }
    }

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
    static void DrawCap(PhysicalCap cap, GizmoType type)
    {
        var orifice = cap.Orifice;
        if (orifice == null)
            return;

        Handles.color = Color.magenta;
        Handles.DrawLine(orifice.position, orifice.position + orifice.forward * 0.03f, 2f);
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(orifice.position, 0.001f);
    }

    [DrawGizmo(GizmoType.Selected)]
    static void DrawSocket(CapSocket socket, GizmoType type)
    {
        if (socket.InstallParent == null)
            return;

        Gizmos.color = new Color(0.45f, 0.95f, 1f, 0.8f);
        Gizmos.DrawWireSphere(socket.InstallParent.position, socket.SnapRadius);
    }

    // Outside Play Mode CapSocket.Installed is not populated yet (Awake has
    // not run); the seated cap is simply the one parented to the pivot.
    static PhysicalCap SeatedInEditMode(CapSocket socket)
    {
        var pivot = socket.InstallParent;
        return pivot != null ? pivot.GetComponentInChildren<PhysicalCap>(true) : null;
    }
}
