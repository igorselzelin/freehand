using Freehand.VR;
using UnityEngine;

namespace Freehand.Tools
{
    /// <summary>
    /// Shared behaviour for the physical racks in the room (colors, caps).
    ///
    /// Selection is plain distance testing against the controller transforms -
    /// no physics triggers, no raycasts, no VR UI framework. On Quest that is
    /// the most reliable option available and it costs a handful of distance
    /// checks per frame; a collider or pointer based approach would add failure
    /// modes (missed trigger frames, layer mistakes, pointer setup) for no
    /// benefit at this scale.
    ///
    /// Slots are just child Transforms, so the room can be re-arranged in the
    /// scene without touching code.
    /// </summary>
    public abstract class ProximityRack : MonoBehaviour
    {
        [SerializeField] protected Transform[] slots;
        [SerializeField] Transform leftHand;
        [SerializeField] Transform rightHand;
        [SerializeField] float selectRadius = 0.11f;

        [Tooltip("Optional marker moved onto whichever slot is active, so the player can see the current selection without a HUD.")]
        [SerializeField] Transform selectionMarker;

        [SerializeField] Vector3 markerOffset = new Vector3(0f, 0.13f, 0f);
        [SerializeField] float selectedPopScale = 1.18f;

        [Tooltip("Seconds before a different slot can be selected again, so brushing past the rack does not fire several times.")]
        [SerializeField] float reselectCooldown = 0.35f;

        Vector3[] baseScales;
        int lastSelected = -1;
        float cooldownRemaining;

        protected abstract void OnSlotSelected(int index);

        /// <summary>Which hand touched the slot just selected (for OnSlotSelected).</summary>
        protected bool LastTouchedByLeftHand { get; private set; }

        protected Transform LeftHand => leftHand;
        protected Transform RightHand => rightHand;

        protected virtual void OnEnable()
        {
            CacheScales();
        }

        void CacheScales()
        {
            if (slots == null)
                return;

            baseScales = new Vector3[slots.Length];
            for (var i = 0; i < slots.Length; i++)
                baseScales[i] = slots[i] != null ? slots[i].localScale : Vector3.one;
        }

        /// <summary>Call after selecting a slot from code so the room shows the same state the systems hold. A negative index shows no slot as selected (marker hidden).</summary>
        protected void Visualize(int index)
        {
            if (slots == null || baseScales == null || baseScales.Length != slots.Length)
                return;

            if (index < 0 && selectionMarker != null)
                selectionMarker.gameObject.SetActive(false);

            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                    continue;

                slots[i].localScale = i == index ? baseScales[i] * selectedPopScale : baseScales[i];
            }

            if (selectionMarker != null && index >= 0 && index < slots.Length && slots[index] != null)
            {
                selectionMarker.position = slots[index].position + slots[index].TransformVector(markerOffset);
                selectionMarker.gameObject.SetActive(true);
            }
        }

        protected virtual void Update()
        {
            if (slots == null || slots.Length == 0)
                return;

            if (cooldownRemaining > 0f)
            {
                cooldownRemaining -= Time.deltaTime;
                return;
            }

            var hit = FindTouchedSlot(out var touchedByLeftHand);
            if (hit < 0)
            {
                lastSelected = -1;
                return;
            }

            if (hit == lastSelected)
                return;

            lastSelected = hit;
            cooldownRemaining = reselectCooldown;
            LastTouchedByLeftHand = touchedByLeftHand;
            OnSlotSelected(hit);
            Visualize(hit);

            // Small confirmation buzz in whichever hand actually touched the
            // slot - covers the color palette, the cap rack and the cleaner
            // slot (it is just another palette entry) from one place.
            HapticFeedback.Pulse(touchedByLeftHand, 0.18f, 0.035f);
        }

        int FindTouchedSlot(out bool touchedByLeftHand)
        {
            var radiusSq = selectRadius * selectRadius;

            for (var i = 0; i < slots.Length; i++)
            {
                // A slot switched off (an empty place on a shelf) cannot be picked.
                if (slots[i] == null || !slots[i].gameObject.activeInHierarchy)
                    continue;

                if (IsWithin(leftHand, slots[i].position, radiusSq))
                {
                    touchedByLeftHand = true;
                    return i;
                }

                if (IsWithin(rightHand, slots[i].position, radiusSq))
                {
                    touchedByLeftHand = false;
                    return i;
                }
            }

            touchedByLeftHand = false;
            return -1;
        }

        static bool IsWithin(Transform hand, Vector3 point, float radiusSq)
        {
            return hand != null && (hand.position - point).sqrMagnitude <= radiusSq;
        }
    }
}
