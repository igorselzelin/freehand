using UnityEngine;

namespace Freehand.Spray
{
    /// <summary>
    /// Small cosmetic jitter on the can's visible body while SprayCanShake
    /// detects an active shake. Applied to the "Visual" transform (the
    /// imported can model), never to SprayCan's own root - that root carries
    /// the V0.4.1 grab/GrabPoint chain and must stay rigid in the hand, and
    /// SprayCanShake itself reads the root's position every frame to detect
    /// the gesture, so jittering that same transform would feed the
    /// detector noise from its own output.
    /// </summary>
    public class ShakeVisualFeedback : MonoBehaviour
    {
        [SerializeField] Transform visualRoot;
        [SerializeField] SprayCanShake shake;
        [SerializeField] float maxOffsetMeters = 0.003f;
        [SerializeField] float frequency = 22f;

        Vector3 restLocalPosition;

        void Awake()
        {
            if (visualRoot != null)
                restLocalPosition = visualRoot.localPosition;
        }

        void Update()
        {
            if (visualRoot == null || shake == null)
                return;

            var intensity = shake.ShakeIntensity01;
            if (intensity <= 0f)
            {
                if (visualRoot.localPosition != restLocalPosition)
                    visualRoot.localPosition = restLocalPosition;
                return;
            }

            var noise = (Mathf.PerlinNoise(Time.time * frequency, 0.5f) - 0.5f) * 2f;
            var noiseZ = (Mathf.PerlinNoise(0.5f, Time.time * frequency) - 0.5f) * 2f;
            var offset = new Vector3(noise, 0f, noiseZ) * (maxOffsetMeters * intensity);
            visualRoot.localPosition = restLocalPosition + offset;
        }
    }
}
