using System;
using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// Which cap, if any, is physically seated on the can right now. The cap
    /// objects themselves (PhysicalCap) carry their own SprayCap data, and
    /// CapSocket is the only thing that ever calls SetInstalled - so this can
    /// never disagree with what the player actually sees on the can. There is
    /// no "select cap N" path any more: changing the cap means physically
    /// swapping it.
    /// </summary>
    public class CapManager : MonoBehaviour
    {
        /// <summary>Raised with the newly installed cap's data, or null when the cap was removed.</summary>
        public event Action<SprayCap> CapChanged;

        /// <summary>The installed cap's spray behaviour, or null when the can has no cap.</summary>
        public SprayCap Current { get; private set; }

        public bool HasCap => Current != null;

        public void SetInstalled(SprayCap cap)
        {
            if (ReferenceEquals(cap, Current))
                return;

            Current = cap;
            CapChanged?.Invoke(cap);
        }
    }
}
