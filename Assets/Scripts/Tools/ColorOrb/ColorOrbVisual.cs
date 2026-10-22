using Freehand.Audio;
using UnityEngine;

namespace Freehand.Tools
{
    public enum ColorOrbState
    {
        /// <summary>Not there: nothing rendered.</summary>
        Dormant,
        /// <summary>Forming over the palm.</summary>
        Summon,
        Idle,
        /// <summary>Something is pointing at it.</summary>
        Selecting,
        /// <summary>A pick just landed.</summary>
        Confirmed,
        /// <summary>Folding away.</summary>
        Dismiss,
    }

    public enum ColorOrbTarget
    {
        None,
        /// <summary>The orb's surface: hue and saturation.</summary>
        Core,
        /// <summary>A preset bead (the palette colors, the cleaner).</summary>
        Bead,
        /// <summary>The orbital arc: brightness.</summary>
        ValueArc,
    }

    /// <summary>What ColorOrb wants shown this frame - the visual has no other input.</summary>
    public struct ColorOrbFrame
    {
        public ColorOrbState State;
        public float StateTime;
        public float Value;
        public float Hue;
        public float Saturation;
        public ColorOrbTarget Hover;
        public Vector3 HoverLocalDirection;
        public int HoverBead;
        public float HoverValue;
        public Color HoverColor;
        public Color ConfirmColor;
        public bool BeamOn;
        public Vector3 BeamFrom;
        public Vector3 BeamTo;

        /// <summary>+1: the value arc on the orb's +X side (the player's left - for an orb over the right hand); -1: mirrored, away from a left hand.</summary>
        public float ArcSide;
    }

    /// <summary>
    /// The Color Orb's look and motion, nothing else: it reads a ColorOrbFrame
    /// each frame and animates. Geometry lives in the orb root's space, whose
    /// +Z faces the player (ColorOrb turns the root), so the core's shader
    /// and ColorOrb's picking share one mapping - ColorAt.
    ///
    ///   Summon     scales up with a slight overshoot while the surface forms
    ///              out of noise; beads fly out to their orbit; sparks.
    ///   Idle       breathes; pigment flows; beads orbit.
    ///   Selecting  beads slow to a stop; a glint follows the pointer; the
    ///              target swells; the halo brightens.
    ///   Confirmed  a pulse in the picked color, sparks in that color.
    ///   Dismiss    folds into itself and dissolves; then nothing renders.
    ///
    /// Cost while open: ~12 draw calls, one additive layer, 24 particles at
    /// most; no allocations per frame.
    /// </summary>
    public class ColorOrbVisual : MonoBehaviour
    {
        [SerializeField] ColorOrbSettings settings;

        [Header("Parts (built by the scene builder)")]
        [SerializeField] Transform body;
        [SerializeField] Renderer core;
        [SerializeField] Renderer halo;
        [SerializeField] Renderer valueArc;
        [SerializeField] Renderer valueKnob;
        [SerializeField] Renderer cursor;
        [SerializeField] Renderer[] beads;
        [SerializeField] ParticleSystem sparks;
        [SerializeField] LineRenderer beam;
        [SerializeField] AudioSource audioSource;

        [Tooltip("Index in `beads` of the cleaner (drawn as a striped solvent bead).")]
        [SerializeField] int cleanerBead = -1;

        static readonly int ValueId = Shader.PropertyToID("_Value");
        static readonly int SolidColorId = Shader.PropertyToID("_SolidColor");
        static readonly int GlowId = Shader.PropertyToID("_Glow");
        static readonly int PulseId = Shader.PropertyToID("_Pulse");
        static readonly int PulseColorId = Shader.PropertyToID("_PulseColor");
        static readonly int HoverDirId = Shader.PropertyToID("_HoverDir");
        static readonly int HoverId = Shader.PropertyToID("_Hover");
        static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        static readonly int ArcHueId = Shader.PropertyToID("_ArcHue");
        static readonly int ArcSaturationId = Shader.PropertyToID("_ArcSaturation");
        static readonly int StripesId = Shader.PropertyToID("_Stripes");
        static readonly int HaloColorId = Shader.PropertyToID("_Color");
        static readonly int HaloIntensityId = Shader.PropertyToID("_Intensity");

        Material coreMaterial;
        Material haloMaterial;
        Material arcMaterial;
        Material knobMaterial;
        Material cursorMaterial;
        Material[] beadMaterials;
        Color[] beadColors;
        float beadAngle;
        float beadSpin = 1f;
        float hoverBlend;
        float beadSpread;
        ColorOrbState lastState = ColorOrbState.Dormant;
        bool visible;
        float lastHoverSound = -10f;

        /// <summary>Which side the value arc is on (see ColorOrbFrame.ArcSide).</summary>
        public float ArcSide { get; private set; } = 1f;

        /// <summary>The core's radius right now, meters (in the orb root's space).</summary>
        public float CoreRadius { get; private set; }
        public float ArcRadius { get; private set; }
        public float BeadRadius { get; private set; }
        public float ArcHalfAngle => settings != null ? settings.valueArcHalfAngle : 70f;
        public int BeadCount => beads != null ? beads.Length : 0;

        /// <summary>Whether bead i is taking pointers right now (beads are only targets once fully out).</summary>
        public bool BeadsReady => beadSpread > 0.95f;

        /// <summary>Bead i's position in the orb root's space.</summary>
        public Vector3 BeadLocalPosition(int i) => beads[i].transform.localPosition;

        /// <summary>The color the orb shows - and gives - at a direction from its centre, at brightness `value` (orb root space, +Z toward the player).</summary>
        public static Color ColorAt(Vector3 localDirection, float value, out float hue, out float saturation)
        {
            var d = localDirection.normalized;
            hue = Mathf.Repeat(Mathf.Atan2(d.y, d.x) / (Mathf.PI * 2f) + 1f, 1f);
            saturation = Mathf.Clamp01(new Vector2(d.x, d.y).magnitude);
            return Color.HSVToRGB(hue, saturation, value);
        }

        void Awake()
        {
            coreMaterial = core != null ? core.material : null;
            haloMaterial = halo != null ? halo.material : null;
            arcMaterial = valueArc != null ? valueArc.material : null;
            knobMaterial = valueKnob != null ? valueKnob.material : null;
            cursorMaterial = cursor != null ? cursor.material : null;

            beadMaterials = new Material[beads != null ? beads.Length : 0];
            for (var i = 0; i < beadMaterials.Length; i++)
                beadMaterials[i] = beads[i] != null ? beads[i].material : null;

            SfxBank.EnsureFeedback();
            if (audioSource != null)
                AudioBus.Configure(audioSource, SfxCategory.Feedback, 0.3f, 6f);

            SetVisible(false);
        }

        /// <summary>The preset beads' colors, in bead order (the cleaner's entry is its tint).</summary>
        public void SetBeadColors(Color[] colors)
        {
            beadColors = colors;
            for (var i = 0; i < beadMaterials.Length && i < colors.Length; i++)
            {
                if (beadMaterials[i] == null)
                    continue;
                beadMaterials[i].SetColor(SolidColorId, colors[i]);
                beadMaterials[i].SetFloat(StripesId, i == cleanerBead ? 1f : 0f);
            }
        }

        public void PlayConfirm(Color color)
        {
            if (sparks != null)
            {
                var main = sparks.main;
                main.startColor = color;
                sparks.Emit(14);
            }

            // The chime's pitch follows the color's hue, within one octave.
            Color.RGBToHSV(color, out var hue, out _, out _);
            Play(SfxBank.OrbConfirm, 0.85f, Mathf.Lerp(0.9f, 1.26f, hue));
        }

        /// <summary>The pointer moved onto a new target: a small glassy tick whose pitch follows where it is (the core's hue, a bead's place in the ring, the arc's brightness).</summary>
        public void PlayHover(float pitch01)
        {
            // Never a rattle of ticks when the pointer sweeps across several targets.
            if (Time.time - lastHoverSound < 0.05f)
                return;
            lastHoverSound = Time.time;
            Play(SfxBank.OrbHover, 0.45f, Mathf.Lerp(0.85f, 1.35f, Mathf.Clamp01(pitch01)));
        }

        public void Tick(in ColorOrbFrame frame)
        {
            if (settings == null)
                return;

            if (frame.State != lastState)
                Enter(frame.State);

            if (frame.State == ColorOrbState.Dormant)
                return;

            var dt = Time.deltaTime;
            float scale;
            float dissolve;
            float spread;
            var pulse = 0f;

            switch (frame.State)
            {
                case ColorOrbState.Summon:
                {
                    var t = Mathf.Clamp01(frame.StateTime / settings.summonSeconds);
                    scale = EaseOutBack(t);
                    dissolve = 1f - Mathf.SmoothStep(0f, 1f, t * 1.25f);
                    spread = EaseOutCubic(Mathf.Clamp01((t - 0.25f) / 0.75f));
                    break;
                }
                case ColorOrbState.Dismiss:
                {
                    var t = Mathf.Clamp01(frame.StateTime / settings.dismissSeconds);
                    scale = 1f - t * t;
                    dissolve = Mathf.SmoothStep(0f, 1f, t);
                    spread = 1f - EaseOutCubic(Mathf.Clamp01(t * 1.6f));
                    break;
                }
                case ColorOrbState.Confirmed:
                {
                    var t = Mathf.Clamp01(frame.StateTime / settings.confirmSeconds);
                    pulse = 1f - t;
                    scale = 1f + 0.12f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, t * 1.5f));
                    dissolve = 0f;
                    spread = 1f;
                    break;
                }
                default:
                    scale = 1f;
                    dissolve = 0f;
                    spread = 1f;
                    break;
            }

            beadSpread = spread;

            // Breathing, everywhere but the formation/folding.
            var breathe = frame.State == ColorOrbState.Summon || frame.State == ColorOrbState.Dismiss
                ? 1f
                : 1f + settings.breatheAmplitude * Mathf.Sin(Time.time * settings.breatheHz * Mathf.PI * 2f);

            var selecting = frame.State == ColorOrbState.Selecting || (frame.State == ColorOrbState.Confirmed && frame.Hover != ColorOrbTarget.None);
            hoverBlend = Mathf.MoveTowards(hoverBlend, selecting ? 1f : 0f, dt * 6f);

            var coreScale = scale * breathe;
            CoreRadius = settings.coreRadius * coreScale;
            ArcRadius = settings.valueArcRadius * scale;
            BeadRadius = settings.beadRadius * scale;

            if (body != null)
                body.localScale = Vector3.one * coreScale;

            // The arc keeps its size through the breathing: it is a slider.
            // It sits on the side away from the hand holding the orb.
            ArcSide = frame.ArcSide < 0f ? -1f : 1f;
            if (valueArc != null)
            {
                valueArc.transform.localScale = Vector3.one * scale;
                valueArc.transform.localRotation = ArcSide > 0f ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f);
            }

            if (coreMaterial != null)
            {
                coreMaterial.SetFloat(ValueId, frame.Hover == ColorOrbTarget.ValueArc ? frame.HoverValue : frame.Value);
                coreMaterial.SetFloat(DissolveId, dissolve);
                coreMaterial.SetFloat(GlowId, 0.3f + hoverBlend * 0.7f);
                coreMaterial.SetFloat(PulseId, pulse);
                coreMaterial.SetColor(PulseColorId, frame.ConfirmColor);
                coreMaterial.SetFloat(HoverId, frame.Hover == ColorOrbTarget.Core ? 1f : 0f);
                coreMaterial.SetVector(HoverDirId, frame.HoverLocalDirection);
            }

            if (haloMaterial != null)
            {
                var glow = frame.Hover != ColorOrbTarget.None ? frame.HoverColor : Color.HSVToRGB(frame.Hue, frame.Saturation * 0.6f, 1f);
                haloMaterial.SetColor(HaloColorId, Color.Lerp(glow, Color.white, 0.25f));
                haloMaterial.SetFloat(HaloIntensityId, (0.55f + 0.12f * Mathf.Sin(Time.time * 2.1f) + hoverBlend * 0.35f + pulse * 0.8f) * (1f - dissolve));
            }

            if (arcMaterial != null)
            {
                arcMaterial.SetFloat(ArcHueId, frame.Hue);
                arcMaterial.SetFloat(ArcSaturationId, frame.Saturation);
                arcMaterial.SetFloat(DissolveId, dissolve);
            }

            PlaceValueKnob(frame, scale, dissolve);
            PlaceBeads(frame, scale, spread, selecting, dt);
            PlaceCursor(frame);
            PlaceBeam(frame);
        }

        void Enter(ColorOrbState state)
        {
            var wasDormant = lastState == ColorOrbState.Dormant;
            lastState = state;

            switch (state)
            {
                case ColorOrbState.Dormant:
                    SetVisible(false);
                    break;
                case ColorOrbState.Summon:
                    SetVisible(true);
                    beadSpin = 1f;
                    if (sparks != null)
                    {
                        sparks.Play();
                        sparks.Emit(12);
                    }

                    Play(SfxBank.OrbSummon, 0.8f, 1f);
                    break;
                case ColorOrbState.Dismiss:
                    if (sparks != null)
                        sparks.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    Play(SfxBank.OrbDismiss, 0.7f, 1f);
                    break;
                default:
                    if (wasDormant)
                        SetVisible(true);
                    break;
            }
        }

        void SetVisible(bool on)
        {
            visible = on;
            if (core != null) core.enabled = on;
            if (halo != null) halo.enabled = on;
            if (valueArc != null) valueArc.enabled = on;
            if (valueKnob != null) valueKnob.enabled = on;
            if (cursor != null) cursor.enabled = false;
            if (beads != null)
                foreach (var bead in beads)
                    if (bead != null)
                        bead.enabled = on;
            if (beam != null) beam.enabled = false;
            if (!on && sparks != null)
                sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        // The knob rides the arc at the current brightness.
        void PlaceValueKnob(in ColorOrbFrame frame, float scale, float dissolve)
        {
            if (valueKnob == null)
                return;

            var value = frame.Hover == ColorOrbTarget.ValueArc ? frame.HoverValue : frame.Value;
            var angle = Mathf.Lerp(-ArcHalfAngle, ArcHalfAngle, value) * Mathf.Deg2Rad;
            valueKnob.transform.localPosition = new Vector3(Mathf.Cos(angle) * ArcSide, Mathf.Sin(angle), 0f) * ArcRadius;
            var swell = frame.Hover == ColorOrbTarget.ValueArc ? 1.5f : 1f;
            valueKnob.transform.localScale = Vector3.one * (settings.beadRadius * 1.1f * scale * swell);
            valueKnob.enabled = visible && dissolve < 0.9f;
            if (knobMaterial != null)
                knobMaterial.SetColor(SolidColorId, Color.HSVToRGB(frame.Hue, frame.Saturation, value));
        }

        // Beads orbit on a tilted ring; they slow to a stop while something
        // points at the orb, so a moving target is never what the player
        // has to hit.
        void PlaceBeads(in ColorOrbFrame frame, float scale, float spread, bool selecting, float dt)
        {
            if (beads == null || beads.Length == 0)
                return;

            beadSpin = Mathf.MoveTowards(beadSpin, selecting ? 0f : 1f, dt * 3f);
            beadAngle += settings.beadOrbitDegreesPerSecond * beadSpin * dt;
            var tilt = Quaternion.Euler(settings.beadOrbitTilt, 0f, 0f);
            var radius = settings.beadOrbitRadius * scale * spread;

            for (var i = 0; i < beads.Length; i++)
            {
                if (beads[i] == null)
                    continue;

                var angle = (beadAngle + i * 360f / beads.Length) * Mathf.Deg2Rad;
                var t = beads[i].transform;
                t.localPosition = tilt * new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * radius;
                var swell = frame.Hover == ColorOrbTarget.Bead && frame.HoverBead == i ? 1.45f : 1f;
                t.localScale = Vector3.one * (settings.beadRadius * 2f * scale * Mathf.Max(0.05f, spread) * swell);
                if (beadMaterials[i] != null)
                    beadMaterials[i].SetFloat(GlowId, swell > 1f ? 1.2f : 0.2f);
            }
        }

        // A bright point where the pointer meets the surface, in that color.
        void PlaceCursor(in ColorOrbFrame frame)
        {
            if (cursor == null)
                return;

            var on = visible && frame.Hover == ColorOrbTarget.Core;
            cursor.enabled = on;
            if (!on)
                return;

            cursor.transform.localPosition = frame.HoverLocalDirection.normalized * (CoreRadius + 0.004f);
            cursor.transform.localScale = Vector3.one * 0.007f;
            if (cursorMaterial != null)
                cursorMaterial.SetColor(SolidColorId, frame.HoverColor);
        }

        void PlaceBeam(in ColorOrbFrame frame)
        {
            if (beam == null)
                return;

            beam.enabled = visible && frame.BeamOn;
            if (!beam.enabled)
                return;

            beam.SetPosition(0, frame.BeamFrom);
            beam.SetPosition(1, frame.BeamTo);
            var end = frame.Hover != ColorOrbTarget.None ? frame.HoverColor : Color.white;
            beam.startColor = new Color(1f, 1f, 1f, 0.05f);
            beam.endColor = new Color(end.r, end.g, end.b, 0.8f);
        }

        // Through the shared voices when they exist (own pitch per sound), at the orb.
        void Play(AudioClip clip, float volume, float pitch)
        {
            if (clip == null)
                return;

            var sfx = SprayCanSfx.Instance;
            if (sfx != null)
                sfx.PlayAt(clip, SfxCategory.Feedback, volume, transform.position, pitch);
            else if (audioSource != null)
                audioSource.PlayOneShot(clip, volume * AudioBus.Gain(SfxCategory.Feedback));
        }

        static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            var u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        static float EaseOutCubic(float t)
        {
            var u = 1f - t;
            return 1f - u * u * u;
        }
    }
}
