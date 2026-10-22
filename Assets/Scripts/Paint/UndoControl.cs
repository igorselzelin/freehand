using Freehand.Audio;
using Freehand.VR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Freehand.Paint
{
    /// <summary>
    /// The undo button. Undo is a paint-wide action rather than a spray-can
    /// action, so it lives here instead of on the can.
    ///
    ///   tap             undo the last action (on release)
    ///   press and hold  after holdGraceSeconds a ring starts filling over
    ///                   the hand, with haptic ticks getting stronger; at
    ///                   clearHoldSeconds every wall is cleared (one action -
    ///                   undoable like any other). Letting go before that is
    ///                   just an undo.
    ///
    /// Action is resolved by name (same pattern as XRInputBootstrap).
    /// </summary>
    public class UndoControl : MonoBehaviour
    {
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string undoActionName = "Right Secondary Button";
        [SerializeField] SprayCanSfx sfx;

        [Header("Hold to clear all")]
        [Tooltip("Seconds the button must be held to clear every wall.")]
        [SerializeField] float clearHoldSeconds = 2f;

        [Tooltip("Seconds of holding before the clear starts showing - a slow tap never reads as the start of a clear.")]
        [SerializeField] float holdGraceSeconds = 0.35f;

        [Tooltip("The controller the button is on: the progress ring floats over it and the haptics play in it.")]
        [SerializeField] Transform buttonHand;
        [SerializeField] bool buttonHandIsLeft;

        [Tooltip("Faces the progress ring toward the player.")]
        [SerializeField] Transform head;

        [Tooltip("Thin ring drawn over the hand while holding (a LineRenderer with a persistent material, built by the scene builder).")]
        [SerializeField] LineRenderer progressRing;

        [SerializeField] float ringRadius = 0.028f;
        [SerializeField] Color ringColor = new Color(1f, 0.55f, 0.15f, 1f);
        [SerializeField] Color ringDoneColor = new Color(1f, 0.95f, 0.85f, 1f);

        const int RingPoints = 41;

        InputAction undoAction;
        bool wasPressed;
        float pressedAt;
        bool clearedThisPress;
        float nextTick;
        float fadeOut;
        readonly Vector3[] ringPositions = new Vector3[RingPoints];

        /// <summary>0 while not holding; 0-1 while the hold is filling (diagnostics, tests).</summary>
        public float HoldProgress { get; private set; }

        void Awake()
        {
            if (inputActions == null)
            {
                Debug.LogError($"{nameof(UndoControl)}: no InputActionAsset assigned.", this);
                return;
            }

            var map = inputActions.FindActionMap(actionMapName, throwIfNotFound: false);
            undoAction = map != null ? map.FindAction(undoActionName, throwIfNotFound: false) : null;

            if (undoAction == null)
                Debug.LogError($"{nameof(UndoControl)}: action '{undoActionName}' not found.", this);

            if (progressRing != null)
            {
                progressRing.useWorldSpace = true;
                progressRing.positionCount = 0;
                progressRing.enabled = false;
            }
        }

        // The sounds come from the hand that pressed the button.
        Vector3 SoundPosition => buttonHand != null ? buttonHand.position : transform.position;

        void Update()
        {
            if (undoAction == null)
                return;

            var pressed = undoAction.ReadValue<float>() > 0.5f;
            var now = Time.time;

            if (pressed && !wasPressed)
            {
                pressedAt = now;
                clearedThisPress = false;
                nextTick = now + holdGraceSeconds;
            }

            if (pressed && !clearedThisPress)
            {
                var held = now - pressedAt;
                HoldProgress = Mathf.Clamp01((held - holdGraceSeconds) / Mathf.Max(0.01f, clearHoldSeconds - holdGraceSeconds));
                if (held >= holdGraceSeconds && now >= nextTick)
                {
                    // Ticks closer together and stronger as the clear nears.
                    HapticFeedback.Pulse(buttonHandIsLeft, Mathf.Lerp(0.08f, 0.35f, HoldProgress), 0.02f);
                    nextTick = now + Mathf.Lerp(0.22f, 0.08f, HoldProgress);
                }

                if (held >= clearHoldSeconds)
                {
                    clearedThisPress = true;
                    HoldProgress = 0f;
                    var cleared = PaintManager.ClearAll();
                    HapticFeedback.Pulse(buttonHandIsLeft, cleared ? 0.8f : 0.3f, cleared ? 0.14f : 0.05f);
                    if (cleared && sfx != null)
                        sfx.PlayClearAll(SoundPosition);
                    fadeOut = 0.35f;
                }
            }

            if (!pressed && wasPressed)
            {
                // Let go before the clear: an ordinary undo.
                if (!clearedThisPress && PaintManager.Undo() && sfx != null)
                    sfx.PlayUndo(SoundPosition, PaintHistory.LastUndoWasClearAll);
                if (!clearedThisPress && HoldProgress > 0f)
                    fadeOut = 0.15f;
                HoldProgress = 0f;
            }

            wasPressed = pressed;

            // A faint rising wash while the hold builds; the clear itself resets it.
            if (sfx != null)
                sfx.SetClearHold(pressed && !clearedThisPress ? HoldProgress : 0f, SoundPosition);

            DrawRing();
        }

        // A ring over the button hand, facing the player, filling clockwise;
        // after a clear it closes in a bright flash and fades.
        void DrawRing()
        {
            if (progressRing == null || buttonHand == null)
                return;

            float fill;
            Color color;
            if (HoldProgress > 0f)
            {
                fill = HoldProgress;
                color = Color.Lerp(ringColor * 0.7f, ringColor, HoldProgress);
            }
            else if (fadeOut > 0f)
            {
                fadeOut -= Time.deltaTime;
                fill = clearedThisPress ? 1f : 0f;
                color = Color.Lerp(Color.clear, ringDoneColor, Mathf.Clamp01(fadeOut / 0.35f));
            }
            else
            {
                if (progressRing.enabled)
                {
                    progressRing.enabled = false;
                    progressRing.positionCount = 0;
                }

                return;
            }

            var count = Mathf.Clamp(Mathf.CeilToInt(fill * (RingPoints - 1)) + 1, 2, RingPoints);
            if (fill <= 0f)
            {
                progressRing.enabled = false;
                return;
            }

            var center = buttonHand.position + Vector3.up * 0.075f;
            var toHead = head != null ? (head.position - center).normalized : -buttonHand.forward;
            var right = Vector3.Cross(Vector3.up, toHead).normalized;
            if (right.sqrMagnitude < 1e-4f)
                right = buttonHand.right;
            var up = Vector3.Cross(toHead, right);

            for (var i = 0; i < count; i++)
            {
                var angle = Mathf.PI * 0.5f - fill * Mathf.PI * 2f * i / (count - 1);
                ringPositions[i] = center + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * ringRadius;
            }

            progressRing.positionCount = count;
            for (var i = 0; i < count; i++)
                progressRing.SetPosition(i, ringPositions[i]);
            progressRing.startColor = progressRing.endColor = color;
            progressRing.enabled = true;
        }
    }
}
