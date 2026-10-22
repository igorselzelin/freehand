using System;
using Freehand.Audio;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Customization
{
    /// <summary>
    /// A real push button on the workbench: the index fingertip of either hand
    /// pushes its cap down, and past most of its travel it clicks - once, until
    /// the finger comes back up. No ray, no menu: the finger has to be there.
    /// </summary>
    public class WorkshopButton : MonoBehaviour
    {
        [SerializeField] Transform cap;

        [Tooltip("Meters, around the button's axis, that a fingertip presses it within.")]
        [SerializeField] float radius = 0.03f;

        [Tooltip("Meters the cap travels down; it clicks past 60% of it.")]
        [SerializeField] float travel = 0.008f;

        [SerializeField] Transform leftController;
        [SerializeField] Transform rightController;

        public event Action Pressed;

        HandRig handRig;
        Vector3 capRest;
        float depth;
        bool latched;

        public bool Down => latched;

        void Awake()
        {
            handRig = FindAnyObjectByType<HandRig>();
            if (cap != null)
                capRest = cap.localPosition;
        }

        void Update()
        {
            var push = 0f;
            var pushingLeft = false;
            for (var side = 0; side < 2; side++)
            {
                // The index fingertip; the controller only when there is no
                // rendered hand (a fist reaching past must not press it).
                var left = side == 0;
                if (handRig != null && handRig.TryGetFingertips(left, out _, out var index))
                {
                    Consider(index, left, ref push, ref pushingLeft);
                    continue;
                }

                var controller = left ? leftController : rightController;
                if (controller != null)
                    Consider(controller.position, left, ref push, ref pushingLeft);
            }

            depth = Mathf.MoveTowards(depth, push, Time.deltaTime / 0.04f * travel);
            if (cap != null)
                cap.localPosition = capRest - Vector3.up * depth;

            if (!latched && push >= travel * 0.6f)
            {
                latched = true;
                HapticFeedback.Pulse(pushingLeft, 0.35f, 0.025f);
                var sfx = SprayCanSfx.Instance;
                if (sfx != null)
                    sfx.PlayNozzleClick(true);
                Pressed?.Invoke();
            }
            else if (latched && push <= travel * 0.15f)
            {
                latched = false;
                var sfx = SprayCanSfx.Instance;
                if (sfx != null)
                    sfx.PlayNozzleClick(false);
            }
        }

        // How far a point (fingertip) pushes the cap: inside the button's
        // radius, from the cap's top down to its full travel.
        void Consider(Vector3 world, bool left, ref float push, ref bool pushingLeft)
        {
            var local = transform.InverseTransformPoint(world);
            if (new Vector2(local.x, local.z).magnitude > radius)
                return;

            var top = capRest.y + 0.004f;
            if (local.y > top + 0.01f || local.y < top - travel - 0.03f)
                return;

            var pressed = Mathf.Clamp(top - local.y, 0f, travel);
            if (pressed > push)
            {
                push = pressed;
                pushingLeft = left;
            }
        }
    }
}
