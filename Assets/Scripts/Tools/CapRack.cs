using System.Collections.Generic;
using Freehand.Audio;
using Freehand.Spray;
using UnityEngine;

namespace Freehand.Tools
{
    /// <summary>
    /// A physical cap rack: a board with pegs, each peg holding one real
    /// PhysicalCap. Nothing is "selected" here any more - the rack only
    /// stores caps. Changing the can's cap means taking one off a peg and
    /// pushing it onto the can (CapSocket); a cap released near a free peg
    /// slides onto it.
    ///
    /// Replaces the V0.4 touch-a-cube rack, which selected a cap by index and
    /// would have been a second, conflicting way to change the cap (the rack
    /// saying "Fat" while a Skinny cap physically sits on the can).
    ///
    /// Slots are child Transforms at each peg's seat point, +Z facing the
    /// player (so a parked cap's orifice faces out, which is also how you
    /// tell caps apart at a glance).
    /// </summary>
    public class CapRack : MonoBehaviour
    {
        [SerializeField] Transform[] slots;

        [Tooltip("A cap released within this distance (meters) of a free peg slides onto it.")]
        [SerializeField] float placeRadius = 0.06f;

        [SerializeField] SprayCanSfx sfx;

        static readonly List<CapRack> ActiveRacks = new List<CapRack>();

        PhysicalCap[] occupants;

        public int SlotCount => slots != null ? slots.Length : 0;

        void Awake()
        {
            EnsureOccupants();

            for (var i = 0; i < SlotCount; i++)
            {
                var cap = slots[i] != null ? slots[i].GetComponentInChildren<PhysicalCap>(true) : null;
                if (cap != null)
                    cap.EnterRackImmediate(this, i);
            }
        }

        void OnEnable()
        {
            if (!ActiveRacks.Contains(this))
                ActiveRacks.Add(this);
        }

        void OnDisable() => ActiveRacks.Remove(this);

        void EnsureOccupants()
        {
            if (occupants == null || occupants.Length != SlotCount)
                occupants = new PhysicalCap[SlotCount];
        }

        public Transform Slot(int index) => slots[index];

        public bool IsFree(int index)
        {
            EnsureOccupants();
            return index >= 0 && index < SlotCount && slots[index] != null && occupants[index] == null;
        }

        public void Occupy(int index, PhysicalCap cap)
        {
            EnsureOccupants();
            occupants[index] = cap;
        }

        public void Vacate(int index)
        {
            EnsureOccupants();
            if (index >= 0 && index < SlotCount)
                occupants[index] = null;
        }

        public void OnCapSeated(PhysicalCap cap)
        {
            if (sfx != null)
                sfx.PlayCapRack(cap.transform.position);
        }

        public static CapRack FindFreeSlotNear(Vector3 position, out int slot)
        {
            CapRack best = null;
            slot = -1;
            var bestDistance = float.MaxValue;

            foreach (var rack in ActiveRacks)
            {
                for (var i = 0; i < rack.SlotCount; i++)
                {
                    if (!rack.IsFree(i))
                        continue;

                    var distance = Vector3.Distance(position, rack.slots[i].position);
                    if (distance <= rack.placeRadius && distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = rack;
                        slot = i;
                    }
                }
            }

            return best;
        }

        public static CapRack FindAnyFreeSlot(out int slot)
        {
            foreach (var rack in ActiveRacks)
            {
                for (var i = 0; i < rack.SlotCount; i++)
                {
                    if (rack.IsFree(i))
                    {
                        slot = i;
                        return rack;
                    }
                }
            }

            slot = -1;
            return null;
        }
    }
}
