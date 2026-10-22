using Freehand.VR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.Interaction
{
    /// <summary>
    /// The inspect command: the primary button (A / X) of the hand that is
    /// holding something inspects that thing. The same button on an empty hand
    /// stays the Color Orb's (which ignores a hand that holds something), so
    /// one button means one thing at a time, decided by what the hand holds.
    /// </summary>
    public class InspectionInput : MonoBehaviour
    {
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string leftAction = "Left Primary Button";
        [SerializeField] string rightAction = "Right Primary Button";
        [SerializeField] VRGrabber leftGrabber;
        [SerializeField] VRGrabber rightGrabber;

        InputAction left;
        InputAction right;
        bool leftWas;
        bool rightWas;

        void OnEnable()
        {
            var map = inputActions != null ? inputActions.FindActionMap(actionMapName, throwIfNotFound: false) : null;
            left = map != null ? map.FindAction(leftAction, throwIfNotFound: false) : null;
            right = map != null ? map.FindAction(rightAction, throwIfNotFound: false) : null;
            if (left == null || right == null)
                Debug.LogError($"{nameof(InspectionInput)}: '{leftAction}' / '{rightAction}' not found in '{actionMapName}' - items cannot be inspected.", this);
        }

        void Update()
        {
            if (Pressed(left, ref leftWas))
                Inspect(leftGrabber);
            if (Pressed(right, ref rightWas))
                Inspect(rightGrabber);
        }

        static void Inspect(VRGrabber grabber)
        {
            var held = grabber != null ? grabber.Held : null;
            if (held == null)
                return;

            // V0.14: the can plays freestyle runs (which use its in-hand moves); a cap keeps its inspection.
            var freestyle = held.GetComponent<CanFreestyle>();
            if (freestyle != null)
            {
                freestyle.Request();
                return;
            }

            var inspection = held.GetComponent<ItemInspection>();
            if (inspection != null)
                inspection.Request();
        }

        static bool Pressed(InputAction action, ref bool was)
        {
            var down = action != null && action.ReadValue<float>() > 0.5f;
            var pressed = down && !was;
            was = down;
            return pressed;
        }
    }
}
