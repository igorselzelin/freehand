using Freehand.Audio;
using Freehand.Paint;
using Freehand.VR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.Customization
{
    /// <summary>
    /// The workshop's turntable: set the can down on it and it stays there,
    /// upright, while the other can paints it; turn it with either thumbstick
    /// to reach every side; pick it up to take it back.
    ///
    /// Mounting is a release near the plate (the same gesture as setting a cap
    /// on its peg): the can slides the last centimetres onto the plate and
    /// becomes part of it. While mounted, the can's own colliders step out of
    /// the spray's way (Ignore Raycast) and the canvas - a collider with the
    /// body's real mesh, outside the can's rigidbody - follows the body, so the
    /// spray lands on the can's own UVs. The can's physics is otherwise its
    /// own: taken off the stand it is the same can as before.
    /// </summary>
    public class CanWorkshopStand : MonoBehaviour
    {
        const int IgnoreRaycastLayer = 2;

        [SerializeField] VRGrabInteractable can;
        [SerializeField] Renderer canBody;
        [SerializeField] Transform turntable;
        [SerializeField] Transform mount;
        [SerializeField] PaintSurface canvas;

        [Tooltip("How close (meters) to the plate the can must be let go to be set on it.")]
        [SerializeField] float mountRadius = 0.15f;
        [SerializeField] float seatSeconds = 0.22f;

        [Header("Turning")]
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string leftStickAction = "Left Thumbstick";
        [SerializeField] string rightStickAction = "Right Thumbstick";
        [SerializeField] Transform head;

        [Tooltip("The sticks turn the plate only while the player is this close (meters) - elsewhere they mean nothing yet.")]
        [SerializeField] float controlRange = 1.8f;
        [SerializeField] float maxDegreesPerSecond = 150f;
        [SerializeField] float deadZone = 0.2f;

        InputAction leftStick;
        InputAction rightStick;
        Rigidbody canBodyRigidbody;
        Collider canvasCollider;
        Collider[] canColliders;
        int[] canLayers;
        bool mounted;
        float seatTime = -1f;
        Vector3 seatFromPosition;
        Quaternion seatFromRotation;
        Quaternion seatToRotation;
        float spin;
        float lastTick;

        public bool Mounted => mounted;
        public float TurntableAngle => turntable != null ? turntable.localEulerAngles.y : 0f;

        void Awake()
        {
            canBodyRigidbody = can != null ? can.GetComponent<Rigidbody>() : null;
            canvasCollider = canvas != null ? canvas.GetComponent<Collider>() : null;
            if (canvasCollider != null)
                canvasCollider.enabled = false;
        }

        void OnEnable()
        {
            var map = inputActions != null ? inputActions.FindActionMap(actionMapName, throwIfNotFound: false) : null;
            leftStick = map != null ? map.FindAction(leftStickAction, throwIfNotFound: false) : null;
            rightStick = map != null ? map.FindAction(rightStickAction, throwIfNotFound: false) : null;

            if (can != null)
            {
                can.Grabbed += OnGrabbed;
                can.Released += OnReleased;
            }
        }

        void OnDisable()
        {
            if (can != null)
            {
                can.Grabbed -= OnGrabbed;
                can.Released -= OnReleased;
            }
        }

        void OnReleased(VRGrabInteractable _)
        {
            // VRGrabInteractable has just put the can back under its previous
            // parent - the plate, if it was taken from here.
            var near = Vector3.Distance(can.transform.position, mount.position) <= mountRadius;
            if (near)
            {
                Mount();
                return;
            }

            if (can.transform.parent == mount)
                can.transform.SetParent(null, true);
        }

        void OnGrabbed(VRGrabInteractable _)
        {
            if (!mounted)
                return;

            mounted = false;
            seatTime = -1f;
            SetCanRaycastable(true);
            if (canvasCollider != null)
                canvasCollider.enabled = false;
        }

        void Mount()
        {
            mounted = true;
            if (canBodyRigidbody != null)
            {
                canBodyRigidbody.linearVelocity = Vector3.zero;
                canBodyRigidbody.angularVelocity = Vector3.zero;
                canBodyRigidbody.isKinematic = true;
                canBodyRigidbody.interpolation = RigidbodyInterpolation.None;
            }

            can.transform.SetParent(mount, true);

            // The last centimetres onto the plate: upright, keeping the turn
            // it was set down with.
            seatFromPosition = can.transform.localPosition;
            seatFromRotation = can.transform.localRotation;
            var forward = Vector3.ProjectOnPlane(can.transform.localRotation * Vector3.forward, Vector3.up);
            seatToRotation = forward.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(forward, Vector3.up) : Quaternion.identity;
            seatTime = 0f;

            SetCanRaycastable(false);
            if (canvasCollider != null)
                canvasCollider.enabled = true;
            FollowBody();

            var sfx = SprayCanSfx.Instance;
            if (sfx != null)
                sfx.PlayCapRack(mount.position);
        }

        // While on the plate the can's own colliders (body, seated cap) must
        // not catch the spray meant for the canvas around them.
        void SetCanRaycastable(bool raycastable)
        {
            if (can == null)
                return;

            if (!raycastable)
            {
                canColliders = can.GetComponentsInChildren<Collider>(true);
                canLayers = new int[canColliders.Length];
                for (var i = 0; i < canColliders.Length; i++)
                {
                    canLayers[i] = canColliders[i].gameObject.layer;
                    canColliders[i].gameObject.layer = IgnoreRaycastLayer;
                }

                return;
            }

            if (canColliders == null)
                return;
            for (var i = 0; i < canColliders.Length; i++)
            {
                if (canColliders[i] != null)
                    canColliders[i].gameObject.layer = canLayers[i];
            }

            canColliders = null;
        }

        void Update()
        {
            var dt = Time.deltaTime;
            if (seatTime >= 0f && mounted)
            {
                seatTime += dt;
                var t = Mathf.Clamp01(seatTime / seatSeconds);
                var eased = t * t * (3f - 2f * t);
                can.transform.localPosition = Vector3.Lerp(seatFromPosition, Vector3.zero, eased);
                can.transform.localRotation = Quaternion.Slerp(seatFromRotation, seatToRotation, eased);
                if (t >= 1f)
                    seatTime = -1f;
            }

            // Either stick, while the player is at the workshop: eased, so the
            // can turns like a heavy plate, not a dial.
            var input = 0f;
            if (mounted && head != null && turntable != null && Vector3.Distance(head.position, turntable.position) <= controlRange)
                input = Stick(leftStick) + Stick(rightStick);
            var target = Mathf.Clamp(input, -1f, 1f) * maxDegreesPerSecond;
            spin = Mathf.Lerp(spin, target, 1f - Mathf.Exp(-dt / 0.12f));
            if (Mathf.Abs(spin) > 0.05f && turntable != null)
            {
                turntable.Rotate(Vector3.up, -spin * dt, Space.Self);

                // A soft click every 30 degrees, felt more than heard.
                var angle = turntable.localEulerAngles.y;
                if (Mathf.Abs(Mathf.DeltaAngle(angle, lastTick)) >= 30f)
                {
                    lastTick = Mathf.Round(angle / 30f) * 30f;
                    HapticFeedback.Pulse(input < 0f ? true : false, 0.05f, 0.01f);
                }
            }
        }

        float Stick(InputAction action)
        {
            if (action == null)
                return 0f;
            var x = action.ReadValue<Vector2>().x;
            return Mathf.Abs(x) < deadZone ? 0f : (x - Mathf.Sign(x) * deadZone) / (1f - deadZone);
        }

        void LateUpdate()
        {
            if (mounted)
                FollowBody();
        }

        // The canvas collider is the body, wherever the plate has turned it.
        void FollowBody()
        {
            if (canvas == null || canBody == null)
                return;

            var body = canBody.transform;
            canvas.transform.SetPositionAndRotation(body.position, body.rotation);
            canvas.transform.localScale = body.lossyScale;
            Physics.SyncTransforms();
        }
    }
}
