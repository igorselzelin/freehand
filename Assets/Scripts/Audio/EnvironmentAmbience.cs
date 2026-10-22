using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>
    /// The room's sound: occasional events from beyond the walls - a gust
    /// finding its way through the building, metal knocking somewhere far
    /// off, a train under the station, a warm pipe ticking - each placed at
    /// a point in the space around the room, at long irregular intervals.
    ///
    /// There is deliberately no continuous bed. (The earlier version looped
    /// four seconds of low-passed noise at all times, centred around 150 Hz:
    /// it read as a fan or engine running through the whole session and
    /// pulsed at every loop seam.) Silence between events is the point; the
    /// events are rare, quiet, and pick different clips, places and pitches,
    /// so nothing becomes a pattern. Everything yields to the spray: it is
    /// ducked through AudioBus while the player sprays.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class EnvironmentAmbience : MonoBehaviour
    {
        [SerializeField] [Range(0f, 1f)] float volume = 0.7f;

        [Tooltip("Seconds between events, at random: the first after a short while, then within this range.")]
        [SerializeField] Vector2 intervalSeconds = new Vector2(8f, 22f);

        [Tooltip("How far from the room's centre the events happen, meters.")]
        [SerializeField] Vector2 distanceRange = new Vector2(4f, 9f);

        enum Kind { Gust, Clank, Rumble, Pipe }

        AudioSource[] sources;
        float[] baseVolume;
        int next;
        float nextEventAt;
        Kind lastKind = Kind.Gust;
        int lastClip = -1;

        void Awake()
        {
            SfxBank.EnsureEnvironment();

            // The component the builder added with this one plays nothing.
            GetComponent<AudioSource>().playOnAwake = false;

            // Two voices, placed one by one around the room, so an event can
            // start while the last one is still fading out.
            sources = new AudioSource[2];
            baseVolume = new float[2];
            for (var i = 0; i < sources.Length; i++)
            {
                var voice = new GameObject("Ambience_" + i);
                voice.transform.SetParent(transform, false);
                sources[i] = voice.AddComponent<AudioSource>();
                AudioBus.Configure(sources[i], SfxCategory.Environment, 4f, 30f);
                sources[i].loop = false;
            }

            nextEventAt = Time.time + Random.Range(4f, 9f);
        }

        void Update()
        {
            // The mix can duck the room at any moment, even mid-event.
            for (var i = 0; i < sources.Length; i++)
            {
                if (sources[i].isPlaying)
                    sources[i].volume = baseVolume[i] * AudioBus.Gain(SfxCategory.Environment);
            }

            if (Time.time < nextEventAt)
                return;

            Play();
            nextEventAt = Time.time + Random.Range(intervalSeconds.x, intervalSeconds.y);
        }

        void Play()
        {
            // Any kind but the one just heard; gusts are the commonest.
            Kind kind;
            do
            {
                var roll = Random.value;
                kind = roll < 0.42f ? Kind.Gust : roll < 0.72f ? Kind.Clank : roll < 0.82f ? Kind.Rumble : Kind.Pipe;
            }
            while (kind == lastKind);
            lastKind = kind;

            AudioClip[] bank;
            float level;
            switch (kind)
            {
                case Kind.Gust: bank = SfxBank.Gust; level = 0.55f; break;
                case Kind.Clank: bank = SfxBank.DistantClank; level = 0.45f; break;
                case Kind.Rumble: bank = SfxBank.DistantRumble; level = 0.6f; break;
                default: bank = SfxBank.PipeTicks; level = 0.4f; break;
            }

            var clip = SfxBank.Choose(bank, ref lastClip);

            // Somewhere around the room, outside it: a ring of directions, mostly level with the ceiling.
            var angle = Random.value * Mathf.PI * 2f;
            var distance = Random.Range(distanceRange.x, distanceRange.y);
            var height = Random.Range(0.5f, 3f);
            var source = sources[next];
            next = (next + 1) % sources.Length;

            source.transform.position = transform.position + new Vector3(Mathf.Cos(angle) * distance, height, Mathf.Sin(angle) * distance);
            source.Stop();
            source.clip = clip;
            source.pitch = Random.Range(0.94f, 1.06f);
            baseVolume[System.Array.IndexOf(sources, source)] = volume * level;
            source.volume = volume * level * AudioBus.Gain(SfxCategory.Environment);
            source.Play();

#if UNITY_EDITOR
            SprayCanSfx.Trace.Add($"{Time.time:F3} Environment {clip.name} vol {source.volume:F2} pitch {source.pitch:F2} at {source.transform.position}");
#endif
        }
    }
}
