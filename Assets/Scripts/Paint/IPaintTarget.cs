using UnityEngine;

namespace Freehand.Paint
{
    /// <summary>
    /// Everything the spray needs from a paintable surface. SprayEmitter and
    /// PaintManager only ever talk through this, so the CPU Texture2D backend
    /// (PaintSurface) can later be swapped for a RenderTexture/fragment-shader
    /// backend - the approach DokoDemoPainter uses to avoid CPU/GPU texture
    /// transfers - without touching the spray or tool code.
    ///
    /// Radii are in world meters, not pixels: the surface converts to its own
    /// texel density. That keeps a Fat cap the same physical size on a small
    /// sign and on a large wall.
    ///
    /// Undo (V0.10): a target saves what it is about to change into
    /// PaintHistory.Recording itself, piece by piece, before changing it; the
    /// history only stores those pieces and hands them back.
    /// </summary>
    public interface IPaintTarget
    {
        /// <summary>Stamps along the UV segment from a to b, so fast hand movement leaves a continuous line instead of separated dots.</summary>
        void PaintSegment(Vector2 uvFrom, Vector2 uvTo, in SprayStamp stamp);

        /// <summary>Puts back a piece of state this target saved into a PaintHistory.Record.</summary>
        void RestoreUndoState(object state);

        /// <summary>Removes all paint (back to the unpainted surface), saving what it removes into PaintHistory.Recording. False if there was no paint.</summary>
        bool ClearPaint();

        /// <summary>Whether Clear All clears this target (a can's skin is not wiped with the walls).</summary>
        bool ClearedByClearAll { get; }

        /// <summary>Stable identifier for this surface (defaults to the GameObject name) - used by PaintHistory's stroke metadata to record which surface(s) a stroke touched.</summary>
        string SurfaceId { get; }
    }
}
