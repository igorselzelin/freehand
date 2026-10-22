using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>
    /// Every sound of the game, synthesized once at startup (no audio asset
    /// files exist in the project). Each group has several variations so the
    /// same action never plays the same waveform twice in a row, and
    /// intensity classes (light / medium / hard) that differ in character,
    /// not only in volume.
    ///
    /// The generators are physical in spirit - a struck can rings at its body
    /// modes, a ball rattles inside it, aerosol is band-limited noise with a
    /// slow turbulence - so a recorded sample can replace any entry later
    /// without touching the code that plays it.
    /// </summary>
    public static class SfxBank
    {
        public const int ShakeLevels = 3;
        public const int ShakeVariants = 5;     // 0-1 ping (axial), 2-3 knock (lateral), 4 swirl (circular)
        public const int SurfaceCount = 5;

        // ----- spray -----
        public static AudioClip SprayAiry, SprayFull, SolventAiry, SolventFull;
        public static AudioClip[] SprayStart, SolventStart, SprayTail, SolventTail;
        public static AudioClip[] PaintContact;   // by SurfaceSound

        // ----- can -----
        public static AudioClip[][] Rattle;       // [level][variant]
        public static AudioClip[][][] CanImpact;  // [surface][level][variant]
        public static AudioClip[][] CapDrop;      // [level][variant]
        public static AudioClip[] GrabSoft, GrabHard, Release, NozzlePress, NozzleRelease;

        // ----- cap -----
        public static AudioClip[] CapTouch, CapTension, CapPop, CapClick, CapRack;

        // ----- feedback -----
        public static AudioClip SwatchTick;
        public static AudioClip OrbSummon, OrbDismiss, OrbHover, OrbConfirm;
        public static AudioClip[] Undo;           // [0] undo, [1] restore (undo of a Clear All)
        public static AudioClip ClearAll, ClearHold;

        // ----- environment -----
        public static AudioClip[] Gust, DistantClank, DistantRumble, PipeTicks;

        static bool sprayBuilt, canBuilt, capBuilt, feedbackBuilt, envBuilt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => sprayBuilt = canBuilt = capBuilt = feedbackBuilt = envBuilt = false;

        /// <summary>A random entry that is not the one used last time.</summary>
        public static AudioClip Choose(AudioClip[] clips, ref int last)
        {
            if (clips == null || clips.Length == 0)
                return null;
            var index = Random.Range(0, clips.Length);
            if (index == last && clips.Length > 1)
                index = (index + 1 + Random.Range(0, clips.Length - 1)) % clips.Length;
            last = index;
            return clips[index];
        }

        // ================= spray =================

        public static void EnsureSpray()
        {
            if (sprayBuilt)
                return;
            sprayBuilt = true;

            SprayAiry = SprayLoop("SprayAiry", 101, 0);
            SprayFull = SprayLoop("SprayFull", 102, 1);
            SolventAiry = SprayLoop("SolventAiry", 103, 2);
            SolventFull = SprayLoop("SolventFull", 104, 3);

            SprayStart = new[] { ValveBurst("SprayStart0", 111, false), ValveBurst("SprayStart1", 112, false) };
            SolventStart = new[] { ValveBurst("SolventStart0", 113, true), ValveBurst("SolventStart1", 114, true) };
            SprayTail = new[] { Tail("SprayTail0", 121, false), Tail("SprayTail1", 122, false) };
            SolventTail = new[] { Tail("SolventTail0", 123, true), Tail("SolventTail1", 124, true) };

            PaintContact = new AudioClip[SurfaceCount];
            for (var s = 0; s < SurfaceCount; s++)
                PaintContact[s] = ContactLoop("PaintContact" + (SurfaceSound)s, 130 + s, (SurfaceSound)s);
        }

        // kind: 0 airy, 1 full, 2 solvent airy, 3 solvent full. A steady
        // aerosol jet is noise, but a perfectly steady noise reads as a
        // machine: two slow, unrelated modulations give it turbulence, and a
        // 2 s take with a crossfaded seam hides the repeat.
        static AudioClip SprayLoop(string name, int seed, int kind)
        {
            const float seconds = 2.4f;
            const float overlap = 0.2f;
            var rng = new System.Random(seed);
            var length = Mathf.RoundToInt(seconds * Dsp.Rate);
            var take = new float[length + Mathf.RoundToInt(overlap * Dsp.Rate)];

            float hp, lp, bodyHz, bodyMix, flutterHz, flutterDepth;
            switch (kind)
            {
                case 0: hp = 2600f; lp = 9500f; bodyHz = 0f; bodyMix = 0f; flutterHz = 0f; flutterDepth = 0f; break;
                case 1: hp = 900f; lp = 8500f; bodyHz = 1600f; bodyMix = 0.7f; flutterHz = 0f; flutterDepth = 0f; break;
                case 2: hp = 1100f; lp = 4800f; bodyHz = 0f; bodyMix = 0f; flutterHz = 12f; flutterDepth = 0.14f; break;
                default: hp = 450f; lp = 4200f; bodyHz = 1100f; bodyMix = 0.6f; flutterHz = 9f; flutterDepth = 0.18f; break;
            }

            var high1 = Biquad.HighPass(hp);
            var high2 = Biquad.HighPass(hp);
            var low1 = Biquad.LowPass(lp);
            var low2 = Biquad.LowPass(lp);
            var body = Biquad.BandPass(Mathf.Max(bodyHz, 100f), 0.8f);
            var phaseA = (float)rng.NextDouble() * 6.28f;
            var phaseB = (float)rng.NextDouble() * 6.28f;
            var phaseF = (float)rng.NextDouble() * 6.28f;

            var turbulence = 1f;
            for (var i = 0; i < take.Length; i++)
            {
                var n = Dsp.White(rng);
                var x = low2.Process(low1.Process(high2.Process(high1.Process(n))));
                if (bodyMix > 0f)
                    x += body.Process(n) * bodyMix;

                // The modulations are all below 15 Hz: control rate is plenty.
                if ((i & 15) == 0)
                {
                    var t = i / (float)Dsp.Rate;
                    turbulence = 1f + 0.07f * Mathf.Sin(2f * Mathf.PI * 1.37f * t + phaseA) + 0.05f * Mathf.Sin(2f * Mathf.PI * 3.11f * t + phaseB);
                    if (flutterHz > 0f)
                        turbulence *= 1f + flutterDepth * Mathf.Sin(2f * Mathf.PI * flutterHz * t + phaseF + 2f * Mathf.Sin(2f * Mathf.PI * 0.7f * t));
                }

                take[i] = x * turbulence;
            }

            var loop = Dsp.Loop(take, length);
            Dsp.Rms(loop, 0.12f, 0.8f);
            return Dsp.Clip(name, loop, loop: true);
        }

        // The valve opening: a puff of air through the nozzle, with a small
        // plastic tick of the actuator under it.
        static AudioClip ValveBurst(string name, int seed, bool solvent)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.14f);
            if (solvent)
            {
                Dsp.Burst(b, 0f, 1f, 0.03f, 700f, 4500f, rng, 3f);
                Dsp.Tone(b, 0f, 0.15f, 210f, 0.02f, rng);
            }
            else
            {
                Dsp.Burst(b, 0f, 1f, 0.026f, 1800f, 9000f, rng, 2.5f);
                Dsp.Tone(b, 0f, 0.25f, 300f, 0.018f, rng, 180f);
                Dsp.Modes(b, 0f, 0.12f, new[] { 2100f, 3300f }, new[] { 0.004f, 0.003f }, new[] { 1f, 0.5f }, rng);
            }

            Dsp.Peak(b, 0.85f);
            return Dsp.Clip(name, b);
        }

        // The jet dying away: the same hiss, losing its highs as the
        // pressure drops.
        static AudioClip Tail(string name, int seed, bool solvent)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(solvent ? 0.34f : 0.26f);
            var high = Biquad.HighPass(solvent ? 500f : 900f);
            var low1 = Biquad.LowPass(9000f);
            var low2 = Biquad.LowPass(9000f);
            var tau = solvent ? 0.09f : 0.065f;
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                if ((i & 31) == 0)
                {
                    var cutoff = Mathf.Lerp(solvent ? 4200f : 9000f, 1600f, 1f - Mathf.Exp(-t / 0.09f));
                    low1.SetLowPass(cutoff);
                    low2.SetLowPass(cutoff);
                }

                b[i] = low2.Process(low1.Process(high.Process(Dsp.White(rng)))) * Mathf.Exp(-t / tau) * Mathf.Min(1f, t / 0.002f);
            }

            Dsp.Peak(b, 0.6f);
            return Dsp.Clip(name, b);
        }

        // Paint landing on the wall: a faint granular layer under the hiss,
        // different grain per material. Poisson ticks over a quiet floor.
        static AudioClip ContactLoop(string name, int seed, SurfaceSound surface)
        {
            const float seconds = 1.8f;
            const float overlap = 0.15f;
            var rng = new System.Random(seed);
            var length = Mathf.RoundToInt(seconds * Dsp.Rate);
            var take = new float[length + Mathf.RoundToInt(overlap * Dsp.Rate)];

            float density, grainHz, grainDecay, floorHp, floorLp, floorGain, grainGain;
            switch (surface)
            {
                case SurfaceSound.Brick: density = 110f; grainHz = 1900f; grainDecay = 0.0035f; floorHp = 600f; floorLp = 4500f; floorGain = 0.5f; grainGain = 1f; break;
                case SurfaceSound.Metal: density = 70f; grainHz = 4200f; grainDecay = 0.007f; floorHp = 3000f; floorLp = 10000f; floorGain = 0.25f; grainGain = 1f; break;
                case SurfaceSound.Wood: density = 90f; grainHz = 1100f; grainDecay = 0.005f; floorHp = 500f; floorLp = 3200f; floorGain = 0.45f; grainGain = 0.9f; break;
                case SurfaceSound.Plastic: density = 100f; grainHz = 2300f; grainDecay = 0.003f; floorHp = 700f; floorLp = 3600f; floorGain = 0.5f; grainGain = 0.6f; break;
                default: density = 140f; grainHz = 3000f; grainDecay = 0.003f; floorHp = 1200f; floorLp = 6000f; floorGain = 0.5f; grainGain = 1f; break;
            }

            var high = Biquad.HighPass(floorHp);
            var low = Biquad.LowPass(floorLp);
            for (var i = 0; i < take.Length; i++)
                take[i] = low.Process(high.Process(Dsp.White(rng))) * floorGain;

            var grains = Mathf.RoundToInt(density * seconds);
            for (var g = 0; g < grains; g++)
            {
                var start = (float)(rng.NextDouble() * (take.Length - Dsp.Rate * 0.05f)) / Dsp.Rate;
                var f = grainHz * (0.7f + 0.8f * (float)rng.NextDouble());
                Dsp.Tone(take, start, grainGain * (0.3f + 0.7f * (float)rng.NextDouble()), f, grainDecay, rng);
            }

            var loop = Dsp.Loop(take, length);
            Dsp.Rms(loop, 0.1f, 0.8f);
            return Dsp.Clip(name, loop, loop: true);
        }

        // ================= can =================

        public static void EnsureCan()
        {
            if (canBuilt)
                return;
            canBuilt = true;

            Rattle = new AudioClip[ShakeLevels][];
            for (var level = 0; level < ShakeLevels; level++)
            {
                Rattle[level] = new AudioClip[ShakeVariants];
                for (var v = 0; v < ShakeVariants; v++)
                    Rattle[level][v] = RattleHit(level, v);
            }

            CanImpact = new AudioClip[SurfaceCount][][];
            for (var s = 0; s < SurfaceCount; s++)
            {
                CanImpact[s] = new AudioClip[3][];
                for (var level = 0; level < 3; level++)
                {
                    CanImpact[s][level] = new AudioClip[2];
                    for (var v = 0; v < 2; v++)
                        CanImpact[s][level][v] = CanHit((SurfaceSound)s, level, v);
                }
            }

            CapDrop = new AudioClip[2][];
            for (var level = 0; level < 2; level++)
            {
                CapDrop[level] = new AudioClip[3];
                for (var v = 0; v < 3; v++)
                    CapDrop[level][v] = CapBounce(level, v);
            }

            GrabSoft = new[] { Grab("GrabSoft0", 201, false), Grab("GrabSoft1", 202, false) };
            GrabHard = new[] { Grab("GrabHard0", 203, true), Grab("GrabHard1", 204, true) };
            Release = new[] { Slip("Release0", 205), Slip("Release1", 206) };
            NozzlePress = new[] { ActuatorClick("NozzlePress0", 207, 1900f), ActuatorClick("NozzlePress1", 208, 2100f) };
            NozzleRelease = new[] { ActuatorClick("NozzleRelease0", 209, 1500f), ActuatorClick("NozzleRelease1", 210, 1650f) };
        }

        // The mixing ball striking the inside of the can. Light: one tick.
        // Medium: the ball and a second bounce, plus the can's body answering.
        // Hard: a cascade of bounces and the liquid slapping. Variants 0-1
        // are the ball hitting the domes (ringing, axial shaking), 2-3 the
        // side wall (duller, lateral shaking), 4 the swirl of circular motion.
        static AudioClip RattleHit(int level, int variant)
        {
            var rng = new System.Random(300 + level * 17 + variant * 5);
            var length = level == 0 ? 0.1f : level == 1 ? 0.18f : 0.32f;
            var b = Dsp.Buffer(length);
            var axial = variant < 2;
            var f0 = (axial ? 1250f : 760f) + variant * 210f + 120f * (float)rng.NextDouble();
            var ringTau = axial ? 0.016f : 0.009f;
            var partials = new[] { 1f, 2.31f, 3.9f };
            var gains = new[] { 1f, axial ? 0.5f : 0.3f, axial ? 0.25f : 0.1f };
            var decays = new[] { ringTau, ringTau * 0.65f, ringTau * 0.4f };

            void Hit(float at, float amp, float pitch)
            {
                var freqs = new[] { f0 * pitch * partials[0], f0 * pitch * partials[1], f0 * pitch * partials[2] };
                Dsp.Modes(b, at, amp, freqs, decays, gains, rng);
                Dsp.Burst(b, at, 0.5f * amp, 0.0015f, axial ? 3500f : 1500f, 9000f, rng, 0.2f);
            }

            if (variant == 4)
            {
                // Swirl: liquid and a loose ball going round.
                var noise = Biquad.BandPass(900f, 0.7f);
                for (var i = 0; i < b.Length; i++)
                {
                    var t = i / (float)Dsp.Rate;
                    var env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / length));
                    noise.SetBandPass(Mathf.Lerp(600f, 1500f, t / length), 0.7f);
                    b[i] += noise.Process(Dsp.White(rng)) * env * env * 0.6f;
                }

                Hit(0.004f, 0.5f, 1f);
                if (level > 0)
                    Hit(0.04f, 0.35f, 0.9f);
            }
            else if (level == 0)
            {
                Hit(0f, 0.8f, 1f);
            }
            else if (level == 1)
            {
                Hit(0f, 1f, 1f);
                Hit(0.013f + 0.004f * (float)rng.NextDouble(), 0.55f, 0.93f);
                Dsp.Modes(b, 0f, 0.35f, new[] { 410f, 760f }, new[] { 0.05f, 0.03f }, new[] { 1f, 0.5f }, rng);
            }
            else
            {
                Hit(0f, 1f, 1f);
                Hit(0.012f + 0.004f * (float)rng.NextDouble(), 0.75f, 0.94f);
                Hit(0.034f + 0.008f * (float)rng.NextDouble(), 0.55f, 1.05f);
                Hit(0.062f + 0.012f * (float)rng.NextDouble(), 0.35f, 0.9f);
                Dsp.Modes(b, 0f, 0.6f, new[] { 360f, 690f, 1150f }, new[] { 0.09f, 0.055f, 0.03f }, new[] { 1f, 0.55f, 0.3f }, rng);
                Dsp.Burst(b, 0.02f, 0.3f, 0.07f, 800f, 3000f, rng, 8f);
            }

            Dsp.Peak(b, 0.9f);
            return Dsp.Clip("Rattle" + level + "_" + variant, b);
        }

        // An aluminium can with liquid in it, dropped. The can rings at its
        // body modes; the surface adds its own transient and thud.
        static AudioClip CanHit(SurfaceSound surface, int level, int variant)
        {
            var rng = new System.Random(500 + (int)surface * 31 + level * 7 + variant);
            var length = level == 0 ? 0.15f : level == 1 ? 0.24f : 0.36f;
            var b = Dsp.Buffer(length);
            var pitch = 1f + 0.05f * (variant == 0 ? -1f : 1f) + 0.02f * Dsp.White(rng);

            var freqs = new[] { 320f, 560f, 930f, 1550f, 2400f };
            var decays = new[] { 0.085f, 0.06f, 0.045f, 0.03f, 0.02f };
            var gains = new[] { 1f, 0.7f, 0.5f, 0.35f, 0.2f };
            var bodyScale = 1f;
            switch (surface)
            {
                case SurfaceSound.Metal:
                    bodyScale = 1.1f;
                    Dsp.Burst(b, 0f, 0.9f, 0.0025f, 3000f, 11000f, rng, 0.2f);
                    Dsp.Modes(b, 0f, 0.35f, new[] { 1300f, 3050f, 4700f }, new[] { 0.16f, 0.1f, 0.06f }, new[] { 1f, 0.6f, 0.3f }, rng, 0.02f);
                    break;
                case SurfaceSound.Brick:
                    bodyScale = 0.8f;
                    Dsp.Burst(b, 0f, 0.6f, 0.002f, 1400f, 7000f, rng, 0.3f);
                    Dsp.Tone(b, 0f, 0.6f, 120f, 0.06f, rng);
                    break;
                case SurfaceSound.Wood:
                    bodyScale = 0.6f;
                    for (var m = 3; m < gains.Length; m++)
                        gains[m] *= 0.35f;
                    Dsp.Burst(b, 0f, 0.5f, 0.003f, 700f, 3500f, rng, 0.5f);
                    Dsp.Tone(b, 0f, 0.8f, 170f, 0.08f, rng);
                    break;
                case SurfaceSound.Plastic:
                    bodyScale = 0.7f;
                    for (var m = 2; m < gains.Length; m++)
                        gains[m] *= 0.5f;
                    Dsp.Burst(b, 0f, 0.5f, 0.0025f, 500f, 2500f, rng, 0.5f);
                    Dsp.Tone(b, 0f, 0.6f, 200f, 0.07f, rng);
                    break;
                default:
                    Dsp.Burst(b, 0f, 0.8f, 0.0015f, 2200f, 9000f, rng, 0.2f);
                    Dsp.Tone(b, 0f, 0.5f, 140f, 0.05f, rng);
                    break;
            }

            var tauScale = level == 2 ? 1.3f : 1f;
            var scaled = new float[decays.Length];
            for (var m = 0; m < decays.Length; m++)
                scaled[m] = decays[m] * tauScale;
            Dsp.Modes(b, 0f, 0.8f * bodyScale, freqs, scaled, gains, rng, 0.012f, pitch);

            // The first bounce's echo on harder drops.
            if (level >= 1)
                Dsp.Echoes(b, 0.028f + 0.01f * (float)rng.NextDouble(), level == 2 ? 0.35f : 0.2f);

            Dsp.Peak(b, 0.9f);
            return Dsp.Clip("CanHit_" + surface + level + "_" + variant, b);
        }

        // A small plastic cap on the floor: a short bright tick.
        static AudioClip CapBounce(int level, int variant)
        {
            var rng = new System.Random(700 + level * 9 + variant);
            var b = Dsp.Buffer(level == 0 ? 0.07f : 0.13f);
            var f = 1800f + 400f * variant + 150f * Dsp.White(rng);
            Dsp.Modes(b, 0f, 1f, new[] { f, f * 1.7f, f * 2.6f }, new[] { 0.014f, 0.01f, 0.007f }, new[] { 1f, 0.5f, 0.3f }, rng);
            Dsp.Burst(b, 0f, 0.5f, 0.001f, 2500f, 9000f, rng, 0.2f);
            if (level > 0)
            {
                Dsp.Modes(b, 0.032f, 0.45f, new[] { f * 0.94f, f * 1.6f }, new[] { 0.012f, 0.008f }, new[] { 1f, 0.4f }, rng);
                Dsp.Modes(b, 0.065f, 0.2f, new[] { f * 1.05f }, new[] { 0.01f }, new[] { 1f }, rng);
            }

            Dsp.Peak(b, 0.8f);
            return Dsp.Clip("CapBounce" + level + "_" + variant, b);
        }

        // A hand closing on painted metal. Soft: skin and a muted knock.
        // Hard: the fingers arrive with some speed - a clearer tap and ring.
        static AudioClip Grab(string name, int seed, bool hard)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(hard ? 0.12f : 0.09f);
            Dsp.Burst(b, 0f, hard ? 0.8f : 0.6f, hard ? 0.012f : 0.02f, 300f, hard ? 3600f : 1800f, rng, hard ? 1f : 4f);
            Dsp.Modes(b, 0f, hard ? 0.6f : 0.35f, new[] { 420f, 880f }, new[] { 0.025f, 0.015f }, new[] { 1f, 0.45f }, rng);
            if (hard)
                Dsp.Modes(b, 0.004f, 0.3f, new[] { 1650f, 2900f }, new[] { 0.03f, 0.02f }, new[] { 1f, 0.4f }, rng);
            Dsp.Peak(b, hard ? 0.8f : 0.55f);
            return Dsp.Clip(name, b);
        }

        // Fingers sliding off: barely there.
        static AudioClip Slip(string name, int seed)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.08f);
            Dsp.Burst(b, 0f, 1f, 0.02f, 400f, 2400f, rng, 8f);
            Dsp.Peak(b, 0.35f);
            return Dsp.Clip(name, b);
        }

        // The spray button's plastic tick.
        static AudioClip ActuatorClick(string name, int seed, float hz)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.045f);
            Dsp.Modes(b, 0f, 1f, new[] { hz, hz * 1.9f }, new[] { 0.006f, 0.004f }, new[] { 1f, 0.4f }, rng);
            Dsp.Burst(b, 0f, 0.4f, 0.001f, 2500f, 8000f, rng, 0.2f);
            Dsp.Peak(b, 0.7f);
            return Dsp.Clip(name, b);
        }

        // ================= cap =================

        public static void EnsureCap()
        {
            if (capBuilt)
                return;
            capBuilt = true;

            CapTouch = new[] { PlasticTick("CapTouch0", 801, 2300f), PlasticTick("CapTouch1", 802, 2600f), PlasticTick("CapTouch2", 803, 2000f) };
            CapTension = new[] { Creak("CapTension0", 811, 2100f, 3000f), Creak("CapTension1", 812, 2400f, 3400f) };
            CapPop = new[] { Pop("CapPop0", 821, 1f), Pop("CapPop1", 822, 1.08f) };
            CapClick = new[] { Click("CapClick0", 831, 1f), Click("CapClick1", 832, 1.06f) };
            CapRack = new[] { RackTick("CapRack0", 841, 1f), RackTick("CapRack1", 842, 1.1f) };
        }

        // Fingertips meeting the plastic.
        static AudioClip PlasticTick(string name, int seed, float hz)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.05f);
            Dsp.Modes(b, 0f, 1f, new[] { hz, hz * 1.55f, hz * 2.4f }, new[] { 0.008f, 0.006f, 0.004f }, new[] { 1f, 0.5f, 0.25f }, rng);
            Dsp.Burst(b, 0f, 0.3f, 0.0012f, 1500f, 7000f, rng, 0.3f);
            Dsp.Peak(b, 0.5f);
            return Dsp.Clip(name, b);
        }

        // Plastic gripping the stem as it is pulled: a short rising squeak.
        static AudioClip Creak(string name, int seed, float fromHz, float toHz)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.085f);
            var band = Biquad.BandPass(fromHz, 4f);
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)b.Length;
                if ((i & 15) == 0)
                    band.SetBandPass(Mathf.Lerp(fromHz, toHz, t), 4f);
                var env = Mathf.Sin(Mathf.PI * t);
                b[i] = band.Process(Dsp.White(rng)) * env * env * (0.7f + 0.3f * Mathf.Sin(t * 60f));
            }

            Dsp.Peak(b, 0.35f);
            return Dsp.Clip(name, b);
        }

        // The cap leaving the stem: friction letting go in a small pop, a
        // scrape of plastic, the cap's own tick.
        static AudioClip Pop(string name, int seed, float pitch)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.16f);
            Dsp.Tone(b, 0f, 0.9f, 720f * pitch, 0.016f, rng, 280f);
            Dsp.Burst(b, 0f, 0.5f, 0.018f, 1200f, 6000f, rng, 0.8f);
            Dsp.Modes(b, 0.006f, 0.55f, new[] { 2200f * pitch, 3500f * pitch, 5100f * pitch }, new[] { 0.01f, 0.007f, 0.005f }, new[] { 1f, 0.5f, 0.3f }, rng);
            Dsp.Burst(b, 0.03f, 0.25f, 0.025f, 1500f, 5000f, rng, 4f);
            Dsp.Peak(b, 0.85f);
            return Dsp.Clip(name, b);
        }

        // The cap seating: the cap's snap first, then the deeper seat of the
        // stem a few ms later - a two-part "kch-k".
        static AudioClip Click(string name, int seed, float pitch)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.12f);
            Dsp.Modes(b, 0f, 0.9f, new[] { 2700f * pitch, 4200f * pitch }, new[] { 0.009f, 0.006f }, new[] { 1f, 0.5f }, rng);
            Dsp.Burst(b, 0f, 0.45f, 0.0015f, 2000f, 9000f, rng, 0.2f);
            Dsp.Modes(b, 0.017f, 0.8f, new[] { 920f * pitch, 1700f * pitch }, new[] { 0.02f, 0.012f }, new[] { 1f, 0.4f }, rng);
            Dsp.Tone(b, 0.017f, 0.4f, 330f, 0.03f, rng);
            Dsp.Peak(b, 0.9f);
            return Dsp.Clip(name, b);
        }

        // A cap on its peg: a softer, lower tick.
        static AudioClip RackTick(string name, int seed, float pitch)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(0.09f);
            Dsp.Modes(b, 0f, 0.8f, new[] { 1250f * pitch, 1950f * pitch }, new[] { 0.016f, 0.01f }, new[] { 1f, 0.5f }, rng);
            Dsp.Tone(b, 0f, 0.3f, 380f, 0.02f, rng);
            Dsp.Burst(b, 0f, 0.3f, 0.002f, 1000f, 5000f, rng, 0.4f);
            Dsp.Peak(b, 0.7f);
            return Dsp.Clip(name, b);
        }

        // ================= feedback =================

        public static void EnsureFeedback()
        {
            if (feedbackBuilt)
                return;
            feedbackBuilt = true;

            SwatchTick = SwatchTickClip();
            OrbSummon = OrbSummonClip();
            OrbDismiss = OrbDismissClip();
            OrbHover = OrbHoverClip();
            OrbConfirm = OrbConfirmClip();
            Undo = new[] { UndoClip(false), UndoClip(true) };
            ClearAll = ClearAllClip();
            ClearHold = ClearHoldLoop();
        }

        // A can touching its holder: metal on wire.
        static AudioClip SwatchTickClip()
        {
            var rng = new System.Random(901);
            var b = Dsp.Buffer(0.08f);
            Dsp.Modes(b, 0f, 1f, new[] { 1500f, 2700f, 4100f }, new[] { 0.014f, 0.01f, 0.006f }, new[] { 1f, 0.5f, 0.3f }, rng);
            Dsp.Tone(b, 0f, 0.3f, 400f, 0.02f, rng);
            Dsp.Peak(b, 0.6f);
            return Dsp.Clip("SwatchTick", b);
        }

        // The orb's sounds share one language: glassy, bell-like partials
        // (an inharmonic 2.76 partial on a sine), airy sparkle, and a short
        // echo for the sense of a small luminous space.
        static void Bell(float[] b, float at, float amp, float hz, float decay, System.Random rng)
        {
            Dsp.Modes(b, at, amp, new[] { hz, hz * 2.76f, hz * 5.4f }, new[] { decay, decay * 0.45f, decay * 0.2f }, new[] { 1f, 0.28f, 0.1f }, rng, 0.002f);
        }

        static AudioClip OrbSummonClip()
        {
            var rng = new System.Random(911);
            var b = Dsp.Buffer(0.7f);
            var notes = new[] { 392f, 587f, 784f, 1175f };
            for (var n = 0; n < notes.Length; n++)
                Bell(b, 0.045f * n, 0.55f - 0.08f * n, notes[n], 0.2f + 0.04f * n, rng);

            // Sparkle: noise rising out of nothing.
            var high = Biquad.HighPass(4500f);
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                var env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.38f));
                b[i] += high.Process(Dsp.White(rng)) * env * env * 0.22f;
            }

            Dsp.Echoes(b, 0.047f, 0.35f, 0.103f, 0.18f);
            Dsp.Peak(b, 0.8f);
            return Dsp.Clip("OrbSummon", b);
        }

        static AudioClip OrbDismissClip()
        {
            var rng = new System.Random(912);
            var b = Dsp.Buffer(0.5f);
            Dsp.Tone(b, 0f, 0.45f, 1180f, 0.1f, rng, 520f);
            Bell(b, 0f, 0.35f, 880f, 0.1f, rng);
            Bell(b, 0.05f, 0.25f, 587f, 0.1f, rng);
            var low = Biquad.LowPass(7000f);
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                if ((i & 31) == 0)
                    low.SetLowPass(Mathf.Lerp(7000f, 600f, Mathf.Clamp01(t / 0.35f)));
                b[i] += low.Process(Dsp.White(rng)) * Mathf.Exp(-t / 0.11f) * 0.25f;
            }

            Dsp.Echoes(b, 0.05f, 0.3f, 0.11f, 0.15f);
            Dsp.Peak(b, 0.7f);
            return Dsp.Clip("OrbDismiss", b);
        }

        // Pointer moving onto a new target: a small glassy tick, played at a
        // pitch that follows the target.
        static AudioClip OrbHoverClip()
        {
            var rng = new System.Random(913);
            var b = Dsp.Buffer(0.12f);
            Dsp.Modes(b, 0f, 1f, new[] { 1760f, 3500f }, new[] { 0.028f, 0.012f }, new[] { 1f, 0.3f }, rng, 0.001f);
            Dsp.Burst(b, 0f, 0.15f, 0.001f, 4000f, 9000f, rng, 0.2f);
            Dsp.Echoes(b, 0.034f, 0.22f);
            Dsp.Peak(b, 0.5f);
            return Dsp.Clip("OrbHover", b);
        }

        // A colour taken: a rising arpeggio that blooms, with a soft low
        // body so it feels like the can received something.
        static AudioClip OrbConfirmClip()
        {
            var rng = new System.Random(914);
            var b = Dsp.Buffer(0.85f);
            var notes = new[] { 523f, 659f, 784f, 1047f };
            for (var n = 0; n < notes.Length; n++)
                Bell(b, 0.035f * n, 0.6f, notes[n], 0.28f, rng);
            Dsp.Tone(b, 0f, 0.25f, 196f, 0.12f, rng);
            Dsp.Burst(b, 0.1f, 0.12f, 0.12f, 5000f, 10000f, rng, 20f);
            Dsp.Echoes(b, 0.06f, 0.32f, 0.13f, 0.2f, 0.21f, 0.1f);
            Dsp.Peak(b, 0.85f);
            return Dsp.Clip("OrbConfirm", b);
        }

        // Undo: a quick wash of noise falling in pitch, with two soft tones
        // stepping down - a reversal. Restore (undo of a Clear All): the
        // same gesture rising, with a bloom of bells - things coming back.
        static AudioClip UndoClip(bool restore)
        {
            var rng = new System.Random(restore ? 922 : 921);
            var length = restore ? 0.55f : 0.22f;
            var b = Dsp.Buffer(length);
            var band = Biquad.BandPass(2000f, 0.9f);
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                var u = Mathf.Clamp01(t / (length * 0.8f));
                if ((i & 31) == 0)
                    band.SetBandPass(restore ? Mathf.Lerp(700f, 4200f, u) : Mathf.Lerp(4200f, 700f, u), 0.9f);
                var env = restore ? Mathf.Sin(Mathf.PI * Mathf.Pow(u, 0.7f)) : Mathf.Exp(-t / 0.06f);
                b[i] += band.Process(Dsp.White(rng)) * env * 0.7f;
            }

            if (restore)
            {
                Bell(b, 0.18f, 0.5f, 659f, 0.2f, rng);
                Bell(b, 0.25f, 0.45f, 988f, 0.22f, rng);
                Dsp.Echoes(b, 0.06f, 0.25f);
            }
            else
            {
                Dsp.Tone(b, 0f, 0.4f, 620f, 0.045f, rng, 390f);
                Dsp.Tone(b, 0.035f, 0.3f, 420f, 0.04f, rng, 290f);
            }

            Dsp.Peak(b, restore ? 0.75f : 0.65f);
            return Dsp.Clip(restore ? "UndoRestore" : "Undo", b);
        }

        // Clear All: a solvent wash sweeping over the wall - a soft low
        // push, the body of the wipe, then a shimmer of what is left.
        static AudioClip ClearAllClip()
        {
            var rng = new System.Random(931);
            var b = Dsp.Buffer(0.95f);
            var band = Biquad.BandPass(800f, 0.6f);
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                var u = Mathf.Clamp01(t / 0.8f);
                if ((i & 31) == 0)
                    band.SetBandPass(Mathf.Lerp(3800f, 500f, u * u), 0.6f);
                var env = Mathf.Sin(Mathf.PI * Mathf.Pow(u, 0.55f));
                b[i] += band.Process(Dsp.White(rng)) * env * 0.9f;
            }

            Dsp.Tone(b, 0f, 0.5f, 110f, 0.09f, rng, 70f);
            Bell(b, 0.42f, 0.28f, 1319f, 0.25f, rng);
            Bell(b, 0.5f, 0.22f, 1760f, 0.25f, rng);
            Dsp.Echoes(b, 0.08f, 0.22f, 0.17f, 0.1f);
            Dsp.Peak(b, 0.8f);
            return Dsp.Clip("ClearAll", b);
        }

        // Held B: a soft rising wash while the hold builds (pitch and level
        // follow the progress at play time).
        static AudioClip ClearHoldLoop()
        {
            const float seconds = 1.2f;
            const float overlap = 0.15f;
            var rng = new System.Random(932);
            var length = Mathf.RoundToInt(seconds * Dsp.Rate);
            var take = new float[length + Mathf.RoundToInt(overlap * Dsp.Rate)];
            var band = Biquad.BandPass(1700f, 1.1f);
            for (var i = 0; i < take.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                var shimmer = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 5f * t);
                take[i] = band.Process(Dsp.White(rng)) * shimmer + 0.15f * Mathf.Sin(2f * Mathf.PI * 440f * t) * shimmer;
            }

            var loop = Dsp.Loop(take, length);
            Dsp.Rms(loop, 0.1f, 0.7f);
            return Dsp.Clip("ClearHold", loop, loop: true);
        }

        // ================= environment =================

        public static void EnsureEnvironment()
        {
            if (envBuilt)
                return;
            envBuilt = true;

            Gust = new[] { GustClip(1001, 5.5f), GustClip(1002, 6.5f), GustClip(1003, 4.5f) };
            DistantClank = new[] { ClankClip(1011), ClankClip(1012), ClankClip(1013) };
            DistantRumble = new[] { RumbleClip(1021, 7f), RumbleClip(1022, 5.5f) };
            PipeTicks = new[] { PipeClip(1031), PipeClip(1032) };
        }

        // A gust finding its way through the building: a swell of low
        // noise whose colour opens and closes, with a thin whistle where it
        // squeezes through. It rises and falls - it is not a bed.
        static AudioClip GustClip(int seed, float seconds)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(seconds);
            var low = Biquad.LowPass(500f);
            var low2 = Biquad.LowPass(500f);
            var whistle = Biquad.BandPass(700f, 6f);
            var phase = (float)rng.NextDouble() * 6.28f;
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                var u = t / seconds;
                var sine = Mathf.Sin(Mathf.PI * u);
                var swell = sine * sine;
                var open = swell * (0.8f + 0.2f * Mathf.Sin(2f * Mathf.PI * 0.9f * t + phase));
                if ((i & 31) == 0)
                {
                    var cutoff = Mathf.Lerp(260f, 1100f, open);
                    low.SetLowPass(cutoff);
                    low2.SetLowPass(cutoff);
                    whistle.SetBandPass(Mathf.Lerp(500f, 900f, open) + 40f * Mathf.Sin(2f * Mathf.PI * 0.6f * t + phase), 6f);
                }

                var n = Dsp.White(rng);
                b[i] = (low2.Process(low.Process(n)) + 0.35f * whistle.Process(n) * open) * swell;
            }

            Dsp.Rms(b, 0.1f, 0.5f);
            return Dsp.Clip("Gust" + seed, b);
        }

        // Metal struck somewhere far off in the building.
        static AudioClip ClankClip(int seed)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(2.2f);
            var f = 150f + 70f * (float)rng.NextDouble();
            Dsp.Modes(b, 0f, 1f, new[] { f, f * 2.32f, f * 3.76f, f * 6.4f, f * 9.1f }, new[] { 0.7f, 0.5f, 0.35f, 0.22f, 0.12f }, new[] { 1f, 0.8f, 0.6f, 0.35f, 0.18f }, rng, 0.015f);
            Dsp.Burst(b, 0f, 0.5f, 0.004f, 300f, 2500f, rng, 0.5f);
            var low = Biquad.LowPass(1700f);
            for (var i = 0; i < b.Length; i++)
                b[i] = low.Process(b[i]);
            Dsp.Echoes(b, 0.19f, 0.3f, 0.43f, 0.15f);
            Dsp.Peak(b, 0.5f);
            return Dsp.Clip("DistantClank" + seed, b);
        }

        // A train somewhere under the station: a low swell with a faint
        // rhythm of joints.
        static AudioClip RumbleClip(int seed, float seconds)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(seconds);
            var low1 = Biquad.LowPass(75f);
            var low2 = Biquad.LowPass(140f);
            for (var i = 0; i < b.Length; i++)
            {
                var t = i / (float)Dsp.Rate;
                var sine = Mathf.Sin(Mathf.PI * t / seconds);
                var swell = sine * Mathf.Sqrt(sine);
                var n = Dsp.White(rng);
                b[i] = (low1.Process(n) * 1.6f + low2.Process(n) * 0.6f) * swell;
            }

            var jointHz = 6.5f + 2f * (float)rng.NextDouble();
            for (var t = 0.4f; t < seconds - 0.5f; t += 1f / jointHz)
            {
                var swell = Mathf.Pow(Mathf.Sin(Mathf.PI * t / seconds), 1.6f);
                Dsp.Burst(b, t, 0.1f * swell, 0.02f, 80f, 500f, rng, 2f);
            }

            Dsp.Rms(b, 0.1f, 0.5f);
            return Dsp.Clip("DistantRumble" + seed, b);
        }

        // A warm pipe ticking as it cools: irregular, damped ticks, far off.
        static AudioClip PipeClip(int seed)
        {
            var rng = new System.Random(seed);
            var b = Dsp.Buffer(2.4f);
            var count = 4 + rng.Next(3);
            var t = 0.05f;
            for (var n = 0; n < count; n++)
            {
                var f = 900f + 300f * (float)rng.NextDouble();
                Dsp.Modes(b, t, 0.9f - 0.1f * n, new[] { f, f * 1.6f }, new[] { 0.03f, 0.02f }, new[] { 1f, 0.5f }, rng);
                t += 0.18f + 0.35f * (float)rng.NextDouble();
            }

            var low = Biquad.LowPass(3800f);
            for (var i = 0; i < b.Length; i++)
                b[i] = low.Process(b[i]);
            Dsp.Peak(b, 0.45f);
            return Dsp.Clip("PipeTicks" + seed, b);
        }
    }
}
