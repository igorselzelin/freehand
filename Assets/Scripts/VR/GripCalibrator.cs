using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.VR
{
    /// <summary>
    /// Development tool for settling the one value that cannot be derived from
    /// the project: how far the OpenXR grip pose is rotated from the direction
    /// a player thinks they are aiming.
    ///
    /// Press the bound button in headset to cycle candidate attach rotations on
    /// the held object. The chosen value is logged so it can be written back
    /// into VRGrabInteractable as the permanent default, after which this
    /// component can be removed.
    /// </summary>
    public class GripCalibrator : MonoBehaviour
    {
        [SerializeField] VRGrabInteractable target;
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string cycleActionName = "Left Secondary Button";

        [Tooltip("Candidate hand->object rotations, in order. X pitches the spray down (positive) or up (negative). Starts at zero - the V0.3-validated baseline - with small nearby offsets before larger ones, since zero is the most likely correct value.")]
        [SerializeField]
        Vector3[] presets =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(15f, 0f, 0f),
            new Vector3(-15f, 0f, 0f),
            new Vector3(30f, 0f, 0f),
            new Vector3(-30f, 0f, 0f),
            new Vector3(45f, 0f, 0f),
        };

        InputAction cycleAction;
        int index;
        bool wasPressed;

        void Awake()
        {
            if (inputActions == null)
                return;

            var map = inputActions.FindActionMap(actionMapName, throwIfNotFound: false);
            cycleAction = map != null ? map.FindAction(cycleActionName, throwIfNotFound: false) : null;

            if (cycleAction == null)
                Debug.LogWarning($"{nameof(GripCalibrator)}: action '{cycleActionName}' not found - calibration disabled.", this);
        }

        void Update()
        {
            if (cycleAction == null || target == null || presets.Length == 0)
                return;

            var pressed = cycleAction.ReadValue<float>() > 0.5f;
            if (pressed && !wasPressed)
            {
                index = (index + 1) % presets.Length;
                target.AttachRotationEuler = presets[index];
                Debug.Log($"[GripCalibrator] attachRotationEuler = {presets[index]}  (preset {index + 1}/{presets.Length})");
            }

            wasPressed = pressed;
        }
    }
}
