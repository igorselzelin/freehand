using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Freehand.VR
{
    /// <summary>
    /// One place to fire short haptic impulses on a controller. Uses
    /// UnityEngine.XR's InputDevice API directly (not Input System's
    /// XRControllerWithRumble) - it needs no action lookup, works the same
    /// regardless of which frontend reads buttons/axes, and is the API the
    /// OpenXR provider actually backs on Quest.
    ///
    /// Deliberately dumb and stateless: every caller owns its own rate
    /// limiting (a cooldown float, same pattern already used by
    /// ProximityRack/SprayCanShake) instead of this class trying to police
    /// every call site's frequency. That keeps "how often should this
    /// buzz" a decision made where the gameplay context actually lives.
    /// </summary>
    public static class HapticFeedback
    {
        static readonly List<InputDevice> DeviceBuffer = new List<InputDevice>(1);

        public static void Pulse(bool leftHand, float amplitude, float durationSeconds)
        {
            var node = leftHand ? XRNode.LeftHand : XRNode.RightHand;
            InputDevices.GetDevicesAtXRNode(node, DeviceBuffer);

            for (var i = 0; i < DeviceBuffer.Count; i++)
            {
                var device = DeviceBuffer[i];
                if (device.isValid && device.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse)
                    device.SendHapticImpulse(0u, Mathf.Clamp01(amplitude), Mathf.Max(0f, durationSeconds));
            }
        }
    }
}
