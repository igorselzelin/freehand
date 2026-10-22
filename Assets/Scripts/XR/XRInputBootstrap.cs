using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.XR
{
    /// <summary>
    /// TrackedPoseDriver only auto-enables an input action when that action is defined
    /// directly on the component. When the Position/Rotation/Tracking State inputs are
    /// references to actions defined in an external InputActionAsset (as they are here,
    /// pointing at InputSystem_Actions.inputactions), Unity's own documentation for
    /// TrackedPoseDriver states the enabled state of those actions "must be managed
    /// externally from this behavior". This component is that external management: it
    /// makes sure the "XR" action map is enabled on startup so the referenced actions
    /// actually produce values.
    ///
    /// InputSystem_Actions is also this project's Project-wide Actions asset, which the
    /// Input System package enables on its own at startup. That is not guaranteed to hold
    /// in Editor Play Mode with Domain Reload disabled (this project's setting):
    /// InputActionState resets all action state in a BeforeSceneLoad callback whose order
    /// relative to the package's own enable is undefined. So this component stays, but
    /// only as a safety net: it never disables a map it did not enable itself.
    ///
    /// The map is looked up from the asset at the moment it is enabled, never cached
    /// from Awake. A cached InputActionMap goes stale when the asset's maps are
    /// re-created (asset reimport or deserialization, which with Domain/Scene Reload
    /// disabled can happen between one Play session and the next). The stale object
    /// still points at the asset but is no longer one of asset.actionMaps, so Enable()
    /// builds a state that doesn't contain it ("Map must be contained in state" /
    /// "Map index on InputActionMap is out of range").
    /// </summary>
    public class XRInputBootstrap : MonoBehaviour
    {
        [SerializeField]
        InputActionAsset inputActions;

        [SerializeField]
        string actionMapName = "XR";

        InputActionMap enabledByThis;

        void OnEnable()
        {
            if (inputActions == null)
            {
                Debug.LogError($"{nameof(XRInputBootstrap)}: no InputActionAsset assigned.", this);
                return;
            }

            var map = inputActions.FindActionMap(actionMapName, throwIfNotFound: false);
            if (map == null)
            {
                Debug.LogError($"{nameof(XRInputBootstrap)}: action map '{actionMapName}' not found in '{inputActions.name}'.", this);
                return;
            }

            // Enable() is a no-op for actions that are already enabled (e.g. by the Input
            // System's own project-wide actions startup). Only claim ownership - and
            // therefore the matching Disable() - if nothing had enabled this map yet.
            var wasEnabled = map.enabled;
            map.Enable();
            if (!wasEnabled)
                enabledByThis = map;
        }

        void OnDisable()
        {
            // Only undo our own Enable, and only if that map object is still the one the
            // asset actually holds - never Disable() a stale, detached map.
            if (enabledByThis != null && inputActions != null
                && inputActions.FindActionMap(actionMapName, throwIfNotFound: false) == enabledByThis)
            {
                enabledByThis.Disable();
            }

            enabledByThis = null;
        }
    }
}
