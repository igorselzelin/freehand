using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>
    /// What a thing is made of, as far as sound goes: how a can landing on it
    /// rings, and the grain of paint hitting it. Put on the object that owns
    /// the colliders; anything without one sounds like concrete.
    /// </summary>
    public class AudioSurface : MonoBehaviour
    {
        [SerializeField] SurfaceSound sound = SurfaceSound.Concrete;

        public SurfaceSound Sound => sound;

        public static SurfaceSound Of(Collider collider)
        {
            if (collider == null)
                return SurfaceSound.Concrete;
            var surface = collider.GetComponentInParent<AudioSurface>();
            return surface != null ? surface.sound : SurfaceSound.Concrete;
        }
    }
}
