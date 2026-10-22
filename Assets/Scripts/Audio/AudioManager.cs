using UnityEngine;
using Freehand.Spray;

namespace Freehand.Audio
{
    /// <summary>
    /// The sound of the spray, as a small state machine over the emitter:
    /// Idle -> Starting (valve burst, the jet fades in) -> Continuous (two
    /// layers of aerosol whose mix follows the trigger, and a faint contact
    /// layer where the paint lands) -> Stopping (the jet fades out and dies
    /// away in a tail). The Cleaner has its own timbre throughout.
    ///
    /// It lives on the spray's particle object - the nozzle - so the sound
    /// comes from the tip of the can. It also tells the mix how much the
    /// player is spraying (AudioBus.SprayLevel), which the room's ambience
    /// and the interface sounds duck under.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class AudioManager : MonoBehaviour
    {
        enum State { Idle, Starting, Continuous, Stopping }

        [SerializeField] SprayEmitter sprayEmitter;
        [SerializeField] float baseVolume = 0.95f;
        [SerializeField] float pitchVariation = 0.03f;
        [SerializeField] float minDistance = 1f;
        [SerializeField] float maxDistance = 12f;

        [Tooltip("Seconds the jet takes to fade in and out - short enough to feel immediate, long enough not to click.")]
        [SerializeField] float fadeInSeconds = 0.035f;
        [SerializeField] float fadeOutSeconds = 0.07f;

        AudioSource airy;
        AudioSource full;
        AudioSource contact;
        State state = State.Idle;
        bool erase;
        float jet;                  // 0-1 envelope of the jet itself
        float intensity;            // smoothed trigger (0.25-1)
        float contactLevel;
        float sprayStarted = -10f;
        float sprayStopped = -10f;
        float capPitch;
        float pitchRandom;
        float driftPhase;
        int lastStart = -1, lastTail = -1;
        SurfaceSound contactSurface = SurfaceSound.Concrete;
        Collider lastCollider;
        bool contactNeedsSwitch;

        public string StateName => state.ToString();

        int sprayLevelSlot = -1;

        void Awake()
        {
            SfxBank.EnsureSpray();
            SfxBank.EnsureCan();

            airy = GetComponent<AudioSource>();
            AudioBus.Configure(airy, SfxCategory.Core, minDistance, maxDistance);
            airy.loop = true;

            full = gameObject.AddComponent<AudioSource>();
            AudioBus.Configure(full, SfxCategory.Core, minDistance, maxDistance);
            full.loop = true;

            // The paint landing sits at the wall, not at the nozzle.
            var contactGo = new GameObject("PaintContact");
            contactGo.transform.SetParent(transform, false);
            contact = contactGo.AddComponent<AudioSource>();
            AudioBus.Configure(contact, SfxCategory.Secondary, 1f, maxDistance);
            contact.loop = true;

            erase = sprayEmitter != null && sprayEmitter.EraseMode;
            AssignJetClips();
            sprayLevelSlot = AudioBus.RegisterSpray();
        }

        void OnEnable()
        {
            if (sprayEmitter != null)
                sprayEmitter.EraseModeChanged += OnEraseModeChanged;
        }

        void OnDisable()
        {
            if (sprayEmitter != null)
                sprayEmitter.EraseModeChanged -= OnEraseModeChanged;
            StopAll();
            AudioBus.SetSprayLevel(sprayLevelSlot, 0f);
        }

        void OnEraseModeChanged(bool value)
        {
            erase = value;
            if (state == State.Idle)
                AssignJetClips();
        }

        void AssignJetClips()
        {
            if (airy.isPlaying)
                return;
            airy.clip = erase ? SfxBank.SolventAiry : SfxBank.SprayAiry;
            full.clip = erase ? SfxBank.SolventFull : SfxBank.SprayFull;
        }

        void Update()
        {
            if (sprayEmitter == null)
                return;

            var dt = Time.deltaTime;
            var spraying = sprayEmitter.IsSpraying;

            if (spraying && (state == State.Idle || state == State.Stopping))
                Begin();
            else if (!spraying && (state == State.Starting || state == State.Continuous))
                End();

            // The trigger, eased: it shapes the jet without every wobble of the finger being heard.
            intensity = Mathf.Lerp(intensity, spraying ? sprayEmitter.Intensity : intensity, 1f - Mathf.Exp(-dt / 0.06f));
            jet = Mathf.MoveTowards(jet, spraying ? 1f : 0f, dt / (spraying ? Mathf.Max(0.005f, fadeInSeconds) : Mathf.Max(0.005f, fadeOutSeconds)));

            AudioBus.SetSprayLevel(sprayLevelSlot, Mathf.MoveTowards(AudioBus.GetSprayLevel(sprayLevelSlot), spraying ? 1f : 0f, dt / (spraying ? 0.25f : 0.8f)));

            if (state == State.Starting && jet >= 1f)
                state = State.Continuous;
            if (state == State.Stopping && jet <= 0f)
            {
                StopAll();
                state = State.Idle;
                if (!spraying)
                    AssignJetClips();
                return;
            }

            if (state == State.Idle)
                return;

            UpdateJet(dt);
            UpdateContact(dt, spraying);
        }

        void Begin()
        {
            var now = Time.time;
            var quickRepeat = now - sprayStopped < 0.15f;
            sprayStarted = now;
            state = State.Starting;

            AssignJetClips();
            var cap = sprayEmitter.ActiveCap;
            capPitch = cap != null ? cap.audioPitchOffset : 0f;
            pitchRandom = Random.Range(-pitchVariation, pitchVariation);
            intensity = Mathf.Max(intensity, sprayEmitter.Intensity);

            // Each loop starts somewhere else in its take, so two sprays
            // never begin with the same waveform.
            StartLoop(airy);
            StartLoop(full);

            // The valve opening. A rapid re-press is quieter: the valve is
            // already wet and the burst has not had time to build.
            var clips = erase ? SfxBank.SolventStart : SfxBank.SprayStart;
            var volume = Mathf.Lerp(0.4f, 0.8f, intensity) * (quickRepeat ? 0.55f : 1f);
            PlayOneShotHere(SfxBank.Choose(clips, ref lastStart), volume, Random.Range(0.97f, 1.03f), 0.004f);
        }

        void End()
        {
            sprayStopped = Time.time;
            state = State.Stopping;

            // A tap leaves a short tail, a long spray a longer one.
            var heldFor = Time.time - sprayStarted;
            var volume = Mathf.Lerp(0.3f, 0.7f, Mathf.Clamp01(heldFor / 0.6f)) * Mathf.Lerp(0.6f, 1f, intensity);
            var clips = erase ? SfxBank.SolventTail : SfxBank.SprayTail;
            PlayOneShotHere(SfxBank.Choose(clips, ref lastTail), volume, Random.Range(0.97f, 1.03f), 0f);
        }

        void PlayOneShotHere(AudioClip clip, float volume, float pitch, float delay)
        {
            var sfx = SprayCanSfx.Instance;
            if (sfx != null)
                sfx.PlayAt(clip, SfxCategory.Core, volume, transform.position, pitch, delay);
        }

        void StartLoop(AudioSource source)
        {
            // Still running from a tap just before: keep it going, a restart would click.
            if (source.clip == null || source.isPlaying)
                return;
            source.volume = 0f;
            source.time = Random.value * Mathf.Max(0f, source.clip.length - 0.01f);
            source.Play();
        }

        void StopAll()
        {
            if (airy != null) airy.Stop();
            if (full != null) full.Stop();
            if (contact != null) contact.Stop();
            jet = 0f;
            contactLevel = 0f;
        }

        // The trigger decides the mix: a light press is the airy layer
        // alone, a firm one brings in the full-bodied layer and lifts the
        // pitch a little. Nothing here is linear in the trigger.
        void UpdateJet(float dt)
        {
            var t = Mathf.InverseLerp(0.25f, 1f, intensity);
            var core = AudioBus.Gain(SfxCategory.Core) * baseVolume * jet;

            // Spraying into open air has no wall to bounce off.
            var hit = sprayEmitter.LastHitCollider != null;
            var open = hit ? 1f : 0.8f;

            driftPhase += dt;
            var drift = 0.012f * Mathf.Sin(driftPhase * 1.7f) + 0.008f * Mathf.Sin(driftPhase * 4.3f + 1f);
            var pitch = 1f + capPitch + pitchRandom + 0.10f * (t - 0.5f) + drift;

            airy.pitch = pitch;
            full.pitch = pitch * 0.98f;
            airy.volume = core * Mathf.Lerp(0.5f, 0.85f, t) * open;
            full.volume = core * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.9f, t)) * 0.9f * open;
        }

        // A very faint layer of paint hitting the wall, in the grain of the
        // wall's material; it follows where the jet lands and how far it flew.
        void UpdateContact(float dt, bool spraying)
        {
            var collider = sprayEmitter.LastHitCollider;
            var hit = spraying && collider != null;
            var target = 0f;
            if (hit)
            {
                if (collider != lastCollider)
                {
                    lastCollider = collider;
                    var surface = AudioSurface.Of(collider);
                    if (surface != contactSurface || contact.clip == null)
                    {
                        contactSurface = surface;
                        contactNeedsSwitch = true;
                    }
                }

                var distance = Mathf.Clamp01(sprayEmitter.LastHitDistance / 0.7f);
                target = (1f - 0.7f * distance) * Mathf.InverseLerp(0.25f, 1f, intensity) * jet;
                contact.transform.position = sprayEmitter.LastHitPoint;
            }

            // Fades out before the grain is swapped, and in again after.
            if (contactNeedsSwitch)
            {
                target = 0f;
                if (contactLevel <= 0.01f)
                {
                    contact.clip = SfxBank.PaintContact[(int)contactSurface];
                    contact.time = Random.value * Mathf.Max(0f, contact.clip.length - 0.01f);
                    contactNeedsSwitch = false;
                }
            }

            contactLevel = Mathf.MoveTowards(contactLevel, target, dt / (target > contactLevel ? 0.05f : 0.08f));
            contact.volume = contactLevel * 0.35f * AudioBus.Gain(SfxCategory.Secondary);
            if (contactLevel > 0.005f && !contact.isPlaying && contact.clip != null)
                contact.Play();
            else if (contactLevel <= 0.005f && contact.isPlaying)
                contact.Stop();
        }
    }
}
