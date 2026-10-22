using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>What a sound is for, which decides how loud it sits and how readily it is dropped or ducked.</summary>
    public enum SfxCategory
    {
        /// <summary>Spray, cap, impacts: the physical actions the player must always hear.</summary>
        Core,

        /// <summary>Shake, paint contact, cleaner, grab: physical, but under the core.</summary>
        Secondary,

        /// <summary>Color Orb, undo, Clear All, swatches: the interface's own voice.</summary>
        Feedback,

        /// <summary>The room: wind, distant metal.</summary>
        Environment,
    }

    public enum SurfaceSound
    {
        Concrete,
        Brick,
        Metal,
        Wood,
        Plastic,
    }

    /// <summary>
    /// The mix, in code: one gain per category, a priority per category for
    /// the audio engine, and ducking driven by what the player is doing. A
    /// mixer asset would add nothing here (no per-bus effects are used), and
    /// every source - voices, loops - asks this class for its level, so there
    /// is one place to tune the balance.
    /// </summary>
    public static class AudioBus
    {
        /// <summary>0-1: how much the player is spraying right now, eased (fast attack, slow release) so the world comes back smoothly. The loudest of every spray (a can in each hand).</summary>
        public static float SprayLevel
        {
            get
            {
                var level = 0f;
                for (var i = 0; i < sprayLevels.Count; i++)
                    level = Mathf.Max(level, sprayLevels[i]);
                return level;
            }
        }

        static readonly System.Collections.Generic.List<float> sprayLevels = new System.Collections.Generic.List<float>();

        /// <summary>A spray's slot in the mix (call once per spray).</summary>
        public static int RegisterSpray()
        {
            sprayLevels.Add(0f);
            return sprayLevels.Count - 1;
        }

        public static void SetSprayLevel(int slot, float level)
        {
            if (slot >= 0 && slot < sprayLevels.Count)
                sprayLevels[slot] = level;
        }

        public static float GetSprayLevel(int slot) => slot >= 0 && slot < sprayLevels.Count ? sprayLevels[slot] : 0f;

        static readonly float[] CategoryGain = { 1f, 0.8f, 0.6f, 0.5f };
        static readonly int[] CategoryPriority = { 40, 90, 130, 200 };

        public const float Master = 0.9f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => sprayLevels.Clear();

        /// <summary>The current level of a category, ducking included.</summary>
        public static float Gain(SfxCategory category)
        {
            var duck = 1f;
            switch (category)
            {
                case SfxCategory.Environment: duck = 1f - 0.5f * SprayLevel; break;   // about -6 dB while spraying
                case SfxCategory.Secondary: duck = 1f - 0.2f * SprayLevel; break;
                case SfxCategory.Feedback: duck = 1f - 0.2f * SprayLevel; break;
            }

            return Master * CategoryGain[(int)category] * duck;
        }

        public static int Priority(SfxCategory category) => CategoryPriority[(int)category];

        /// <summary>Common setup of a spatial source: no doppler (a can swung past the head must not bend in pitch), logarithmic fall-off that starts at a metre, so things in arm's reach are at full level and distance only counts beyond it.</summary>
        public static void Configure(AudioSource source, SfxCategory category, float minDistance = 1f, float maxDistance = 10f, float spatialBlend = 1f)
        {
            source.playOnAwake = false;
            source.spatialBlend = spatialBlend;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.priority = Priority(category);
        }
    }
}
