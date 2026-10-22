using System.Collections.Generic;
using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>
    /// The game's one-shot voice pool and the cues built on it: a handful of
    /// reusable AudioSources (no source per event), each played with its own
    /// pitch, level and position, with priorities deciding which sound gives
    /// way when all are busy. It sits under the can, so anything played
    /// "at the can" follows the can; other sounds are placed at a world point.
    ///
    /// The sounds themselves come from SfxBank; what level, pitch and variant
    /// a physical event deserves is decided by whoever knows the physics
    /// (CanMotionAudio, ImpactAudio, the cap, the hands) - this class only
    /// plays and mixes.
    /// </summary>
    public class SprayCanSfx : MonoBehaviour
    {
        public static SprayCanSfx Instance { get; private set; }

        const int VoiceCount = 10;

        struct Voice
        {
            public AudioSource Source;
            public SfxCategory Category;
            public float BusyUntil;
        }

        Voice[] voices;
        AudioSource holdLoop;
        float holdLevel;
        int lastGrabSoft = -1, lastGrabHard = -1, lastRelease = -1, lastPress = -1, lastReleaseClick = -1;
        int lastTouch = -1, lastTension = -1, lastPop = -1, lastClick = -1, lastRack = -1, lastUndo = -1;

#if UNITY_EDITOR
        /// <summary>Editor-only trace of what played, for validating timing and levels.</summary>
        public static readonly List<string> Trace = new List<string>();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
#if UNITY_EDITOR
            Trace.Clear();
#endif
        }

        void Awake()
        {
            Instance = this;
            SfxBank.EnsureCan();
            SfxBank.EnsureCap();
            SfxBank.EnsureFeedback();

            // The component the builder used to add alongside this one: not a voice.
            var legacy = GetComponent<AudioSource>();
            if (legacy != null)
                legacy.playOnAwake = false;

            voices = new Voice[VoiceCount];
            for (var i = 0; i < VoiceCount; i++)
            {
                var go = new GameObject("Voice_" + i);
                go.transform.SetParent(transform, false);
                var source = go.AddComponent<AudioSource>();
                AudioBus.Configure(source, SfxCategory.Secondary, 1f, 12f);
                voices[i] = new Voice { Source = source, BusyUntil = 0f, Category = SfxCategory.Environment };
            }

            var holdGo = new GameObject("ClearHoldLoop");
            holdGo.transform.SetParent(transform, false);
            holdLoop = holdGo.AddComponent<AudioSource>();
            AudioBus.Configure(holdLoop, SfxCategory.Feedback, 1f, 8f);
            holdLoop.loop = true;
            holdLoop.clip = SfxBank.ClearHold;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            // The hold loop fades with the hold, never cut.
            if (holdLoop != null && holdLoop.isPlaying && holdLevel <= 0f)
            {
                holdLoop.volume = Mathf.MoveTowards(holdLoop.volume, 0f, Time.deltaTime / 0.08f);
                if (holdLoop.volume <= 0.001f)
                    holdLoop.Stop();
            }
        }

        // ---------- voices ----------

        /// <summary>Plays a clip at the can (the voice follows the can).</summary>
        public void Play(AudioClip clip, SfxCategory category, float volume, float pitch = 1f, float delay = 0f)
        {
            var voice = Acquire(category, clip, pitch, delay);
            if (voice < 0)
                return;
            voices[voice].Source.transform.localPosition = Vector3.zero;
            Begin(voice, clip, category, volume, pitch, delay);
        }

        /// <summary>Plays a clip at a world point.</summary>
        public void PlayAt(AudioClip clip, SfxCategory category, float volume, Vector3 position, float pitch = 1f, float delay = 0f)
        {
            var voice = Acquire(category, clip, pitch, delay);
            if (voice < 0)
                return;
            voices[voice].Source.transform.position = position;
            Begin(voice, clip, category, volume, pitch, delay);
        }

        int Acquire(SfxCategory category, AudioClip clip, float pitch, float delay)
        {
            if (clip == null || voices == null)
                return -1;

            var now = Time.time;
            for (var i = 0; i < voices.Length; i++)
            {
                if (voices[i].BusyUntil <= now)
                    return i;
            }

            // All busy: give way only to something more important (or equal and older).
            var victim = -1;
            for (var i = 0; i < voices.Length; i++)
            {
                if ((int)voices[i].Category < (int)category)
                    continue;
                if (victim < 0 || (int)voices[i].Category > (int)voices[victim].Category
                    || ((int)voices[i].Category == (int)voices[victim].Category && voices[i].BusyUntil < voices[victim].BusyUntil))
                    victim = i;
            }

            return victim;
        }

        void Begin(int index, AudioClip clip, SfxCategory category, float volume, float pitch, float delay)
        {
            var source = voices[index].Source;
            source.Stop();
            source.clip = clip;
            source.pitch = pitch;
            source.volume = Mathf.Clamp01(volume * AudioBus.Gain(category));
            source.priority = AudioBus.Priority(category);
            if (delay > 0f)
                source.PlayDelayed(delay);
            else
                source.Play();

            voices[index].Category = category;
            voices[index].BusyUntil = Time.time + delay + clip.length / Mathf.Max(0.1f, pitch);

#if UNITY_EDITOR
            Trace.Add($"{Time.time:F3} {category} {clip.name} vol {source.volume:F2} pitch {pitch:F2} delay {delay:F3}");
            if (Trace.Count > 600)
                Trace.RemoveRange(0, 200);
#endif
        }

        static float Jitter(float amount) => 1f + Random.Range(-amount, amount);

        // ---------- hands ----------

        /// <summary>The can taken in hand. 0 = a gentle grip (almost silent), 1 = fingers arriving fast.</summary>
        public void PlayGrab(float hardness01 = 0.3f)
        {
            if (hardness01 < 0.12f)
                return;
            var hard = hardness01 > 0.55f;
            var clip = hard ? SfxBank.Choose(SfxBank.GrabHard, ref lastGrabHard) : SfxBank.Choose(SfxBank.GrabSoft, ref lastGrabSoft);
            var volume = hard ? Mathf.Lerp(0.45f, 0.9f, hardness01) : Mathf.Lerp(0.1f, 0.4f, hardness01 / 0.55f);
            Play(clip, SfxCategory.Secondary, volume, Jitter(0.05f));
        }

        public void PlayRelease(float speed01 = 0f)
        {
            var clip = SfxBank.Choose(SfxBank.Release, ref lastRelease);
            Play(clip, SfxCategory.Secondary, Mathf.Lerp(0.18f, 0.35f, speed01), Jitter(0.06f));
        }

        // ---------- nozzle and cap ----------

        public void PlayNozzleClick(bool press = true)
        {
            var clip = press ? SfxBank.Choose(SfxBank.NozzlePress, ref lastPress) : SfxBank.Choose(SfxBank.NozzleRelease, ref lastReleaseClick);
            Play(clip, SfxCategory.Secondary, press ? 0.25f : 0.2f, Jitter(0.04f));
        }

        /// <summary>Fingers meeting a cap.</summary>
        public void PlayCapTouch(Vector3 position, float hardness01 = 0.3f)
        {
            PlayAt(SfxBank.Choose(SfxBank.CapTouch, ref lastTouch), SfxCategory.Core, Mathf.Lerp(0.25f, 0.6f, hardness01), position, Jitter(0.05f));
        }

        /// <summary>The cap holding on to the stem while it is pulled (progress 0-1 of the pull).</summary>
        public void PlayCapTension(Vector3 position, float progress01)
        {
            PlayAt(SfxBank.Choose(SfxBank.CapTension, ref lastTension), SfxCategory.Core, Mathf.Lerp(0.35f, 0.75f, progress01), position, Mathf.Lerp(0.92f, 1.12f, progress01) * Jitter(0.03f));
        }

        /// <summary>The cap coming off the stem: this is the moment the cap actually separates.</summary>
        public void PlayCapRemove() => Play(SfxBank.Choose(SfxBank.CapPop, ref lastPop), SfxCategory.Core, 0.9f, Jitter(0.04f));

        /// <summary>The cap reaching its seat: the moment it is installed.</summary>
        public void PlayCapInstall() => Play(SfxBank.Choose(SfxBank.CapClick, ref lastClick), SfxCategory.Core, 1f, Jitter(0.03f));

        /// <summary>A cap onto its peg.</summary>
        public void PlayCapRack(Vector3 position) => PlayAt(SfxBank.Choose(SfxBank.CapRack, ref lastRack), SfxCategory.Core, 0.7f, position, Jitter(0.05f));

        // ---------- feedback ----------

        /// <summary>A can touched in the color station.</summary>
        public void PlayColorChange() => Play(SfxBank.SwatchTick, SfxCategory.Feedback, 0.5f, Jitter(0.05f));

        /// <summary>One step back; `restoredClear` when the step undone was a Clear All.</summary>
        public void PlayUndo(Vector3 position, bool restoredClear = false)
        {
            var clip = SfxBank.Undo[restoredClear ? 1 : 0];
            PlayAt(clip, SfxCategory.Feedback, restoredClear ? 0.8f : 0.55f, position, Jitter(0.04f));
        }

        public void PlayClearAll(Vector3 position) => PlayAt(SfxBank.ClearAll, SfxCategory.Feedback, 0.85f, position);

        /// <summary>While B is held: a faint wash that rises with the hold. 0 ends it.</summary>
        public void SetClearHold(float progress01, Vector3 position)
        {
            if (holdLoop == null)
                return;

            holdLevel = progress01;
            if (progress01 <= 0f)
                return;

            holdLoop.transform.position = position;
            holdLoop.pitch = Mathf.Lerp(0.8f, 1.5f, progress01);
            holdLoop.volume = 0.45f * Mathf.SmoothStep(0f, 1f, progress01) * AudioBus.Gain(SfxCategory.Feedback);
            if (!holdLoop.isPlaying)
            {
                holdLoop.time = Random.value * 0.5f;
                holdLoop.Play();
            }
        }
    }
}
