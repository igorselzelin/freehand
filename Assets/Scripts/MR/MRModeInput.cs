using Freehand.VR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.MR
{
    /// <summary>
    /// The way out that does not depend on finding the panel: press both thumbsticks in
    /// (click) together and hold them for a moment, anywhere, in either mode. It is the
    /// same switch as the panel's MODO button. Held, not tapped: a stick clicked by
    /// accident while walking the thumb around must not throw the player into another world.
    /// </summary>
    public class MRModeInput : MonoBehaviour
    {
        [SerializeField] MRModeController controller;
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string leftAction = "Left Thumbstick Click";
        [SerializeField] string rightAction = "Right Thumbstick Click";

        [Tooltip("Seconds both sticks must be held in.")]
        [SerializeField] float holdSeconds = 1.2f;

        InputAction left;
        InputAction right;
        float held;
        bool fired;

        void OnEnable()
        {
            var map = inputActions != null ? inputActions.FindActionMap(actionMapName, throwIfNotFound: false) : null;
            left = map != null ? map.FindAction(leftAction, throwIfNotFound: false) : null;
            right = map != null ? map.FindAction(rightAction, throwIfNotFound: false) : null;
            if (left == null || right == null)
                Debug.LogError($"{nameof(MRModeInput)}: '{leftAction}' / '{rightAction}' not found in '{actionMapName}' - the thumbstick shortcut is off (the panel still works).", this);
        }

        void Update()
        {
            var both = Down(left) && Down(right);
            if (!both)
            {
                held = 0f;
                fired = false;
                return;
            }

            held += Time.unscaledDeltaTime;
            if (!fired && held >= holdSeconds && controller != null)
            {
                fired = true;
                HapticFeedback.Pulse(true, 0.5f, 0.06f);
                HapticFeedback.Pulse(false, 0.5f, 0.06f);
                controller.Toggle();
            }
        }

        static bool Down(InputAction action) => action != null && action.ReadValue<float>() > 0.5f;
    }
}
