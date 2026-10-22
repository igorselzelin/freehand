using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>Second-order filter (RBJ biquad). Coefficients can be changed while it runs, for sweeps.</summary>
    struct Biquad
    {
        float b0, b1, b2, a1, a2, z1, z2;

        public static Biquad LowPass(float fc, float q = 0.707f)
        {
            var f = new Biquad();
            f.SetLowPass(fc, q);
            return f;
        }

        public static Biquad HighPass(float fc, float q = 0.707f)
        {
            var f = new Biquad();
            f.SetHighPass(fc, q);
            return f;
        }

        public static Biquad BandPass(float fc, float q = 1f)
        {
            var f = new Biquad();
            f.SetBandPass(fc, q);
            return f;
        }

        public void SetLowPass(float fc, float q = 0.707f)
        {
            Prepare(fc, q, out var cos, out var alpha);
            var a0 = 1f + alpha;
            b0 = (1f - cos) * 0.5f / a0;
            b1 = (1f - cos) / a0;
            b2 = b0;
            a1 = -2f * cos / a0;
            a2 = (1f - alpha) / a0;
        }

        public void SetHighPass(float fc, float q = 0.707f)
        {
            Prepare(fc, q, out var cos, out var alpha);
            var a0 = 1f + alpha;
            b0 = (1f + cos) * 0.5f / a0;
            b1 = -(1f + cos) / a0;
            b2 = b0;
            a1 = -2f * cos / a0;
            a2 = (1f - alpha) / a0;
        }

        /// <summary>Constant 0 dB peak gain.</summary>
        public void SetBandPass(float fc, float q = 1f)
        {
            Prepare(fc, q, out var cos, out var alpha);
            var a0 = 1f + alpha;
            b0 = alpha / a0;
            b1 = 0f;
            b2 = -alpha / a0;
            a1 = -2f * cos / a0;
            a2 = (1f - alpha) / a0;
        }

        static void Prepare(float fc, float q, out float cos, out float alpha)
        {
            fc = Mathf.Clamp(fc, 20f, Dsp.Rate * 0.45f);
            var w = 2f * Mathf.PI * fc / Dsp.Rate;
            cos = Mathf.Cos(w);
            alpha = Mathf.Sin(w) / (2f * q);
        }

        public float Process(float x)
        {
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }

    /// <summary>
    /// Small synthesis toolbox for the sound bank: filtered noise, decaying
    /// resonances (modal synthesis - how a struck can, cap or pipe rings),
    /// loops with a crossfaded seam. Everything is deterministic (seeded) and
    /// runs once at startup.
    /// </summary>
    static class Dsp
    {
        public const int Rate = 32000;

        public static float[] Buffer(float seconds) => new float[Mathf.Max(1, Mathf.CeilToInt(seconds * Rate))];

        public static float White(System.Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0);

        /// <summary>A few ms of fade at both ends so nothing starts or ends on a click.</summary>
        public static void Taper(float[] b, float inMs = 0.6f, float outMs = 5f)
        {
            var fadeIn = Mathf.Min(b.Length / 2, Mathf.CeilToInt(inMs * 0.001f * Rate));
            var fadeOut = Mathf.Min(b.Length / 2, Mathf.CeilToInt(outMs * 0.001f * Rate));
            for (var i = 0; i < fadeIn; i++)
                b[i] *= i / (float)fadeIn;
            for (var i = 0; i < fadeOut; i++)
                b[b.Length - 1 - i] *= i / (float)fadeOut;
        }

        public static void Peak(float[] b, float target)
        {
            var peak = 0f;
            foreach (var s in b)
                peak = Mathf.Max(peak, Mathf.Abs(s));
            if (peak < 1e-6f)
                return;
            var gain = target / peak;
            for (var i = 0; i < b.Length; i++)
                b[i] *= gain;
        }

        /// <summary>Scales to an RMS target (loops: steady level), never past `peakLimit`.</summary>
        public static void Rms(float[] b, float rmsTarget, float peakLimit)
        {
            double sum = 0;
            var peak = 0f;
            foreach (var s in b)
            {
                sum += s * s;
                peak = Mathf.Max(peak, Mathf.Abs(s));
            }

            var rms = Mathf.Sqrt((float)(sum / b.Length));
            if (rms < 1e-6f)
                return;
            var gain = Mathf.Min(rmsTarget / rms, peakLimit / Mathf.Max(peak, 1e-6f));
            for (var i = 0; i < b.Length; i++)
                b[i] *= gain;
        }

        /// <summary>Adds decaying sinusoids (a struck body ringing) from `start`.</summary>
        public static void Modes(float[] b, float startSeconds, float amp, float[] freqs, float[] decays, float[] gains, System.Random rng, float detune = 0.012f, float pitch = 1f)
        {
            var start = Mathf.RoundToInt(startSeconds * Rate);
            var attackSamples = 0.0006f * Rate;
            for (var m = 0; m < freqs.Length; m++)
            {
                var f = freqs[m] * pitch * (1f + detune * White(rng));
                var tau = decays[m];
                var length = Mathf.Min(b.Length - start, Mathf.CeilToInt(tau * 7f * Rate));
                var phase = (float)rng.NextDouble() * 0.5f;
                var g = amp * gains[m];

                // A damped sinusoid y[n] = g r^n sin(w n + phase) by its
                // two-term recurrence: no exp/sin per sample.
                var r = Mathf.Exp(-1f / (tau * Rate));
                var w = 2f * Mathf.PI * f / Rate;
                var c = 2f * r * Mathf.Cos(w);
                var r2 = r * r;
                var y0 = g * Mathf.Sin(phase);
                var ym1 = g * Mathf.Sin(phase - w) / r;
                for (var i = 0; i < length; i++)
                {
                    b[start + i] += y0 * (i < attackSamples ? i / attackSamples : 1f);
                    var next = c * y0 - r2 * ym1;
                    ym1 = y0;
                    y0 = next;
                }
            }
        }

        /// <summary>One decaying resonance: a thud, a puff.</summary>
        public static void Tone(float[] b, float startSeconds, float amp, float freq, float decay, System.Random rng, float endFreq = 0f)
        {
            var start = Mathf.RoundToInt(startSeconds * Rate);
            var length = Mathf.Min(b.Length - start, Mathf.CeilToInt(decay * 7f * Rate));
            if (endFreq <= 0f)
            {
                // No sweep: a plain damped sinusoid (cheap recurrence).
                Modes(b, startSeconds, amp, new[] { freq }, new[] { decay }, new[] { 1f }, rng, 0f);
                return;
            }

            var phase = 0f;
            for (var i = 0; i < length; i++)
            {
                var t = i / (float)Rate;
                var f = endFreq > 0f ? Mathf.Lerp(freq, endFreq, 1f - Mathf.Exp(-t / decay)) : freq;
                phase += 2f * Mathf.PI * f / Rate;
                b[start + i] += amp * Mathf.Min(1f, t / 0.0008f) * Mathf.Exp(-t / decay) * Mathf.Sin(phase);
            }
        }

        /// <summary>A burst of band-limited noise with a fast attack and exponential decay: a transient, a puff, a scrape.</summary>
        public static void Burst(float[] b, float startSeconds, float amp, float tau, float hp, float lp, System.Random rng, float attackMs = 0.4f)
        {
            var start = Mathf.RoundToInt(startSeconds * Rate);
            var length = Mathf.Min(b.Length - start, Mathf.CeilToInt(tau * 7f * Rate));
            var high = Biquad.HighPass(hp);
            var low = Biquad.LowPass(lp);
            var decay = Mathf.Exp(-1f / (tau * Rate));
            var envelope = amp;
            var attackSamples = Mathf.Max(1f, attackMs * 0.001f * Rate);
            for (var i = 0; i < length; i++)
            {
                var x = low.Process(high.Process(White(rng)));
                b[start + i] += envelope * (i < attackSamples ? i / attackSamples : 1f) * x;
                envelope *= decay;
            }
        }

        /// <summary>Feed-forward echoes (small room, distance).</summary>
        public static void Echoes(float[] b, params float[] delayGainPairs)
        {
            var copy = (float[])b.Clone();
            for (var p = 0; p + 1 < delayGainPairs.Length; p += 2)
            {
                var delay = Mathf.RoundToInt(delayGainPairs[p] * Rate);
                var gain = delayGainPairs[p + 1];
                for (var i = delay; i < b.Length; i++)
                    b[i] += copy[i - delay] * gain;
            }
        }

        /// <summary>Seamless loop: the last `overlapSeconds` of an over-long take fade into its start.</summary>
        public static float[] Loop(float[] take, int length)
        {
            var overlap = take.Length - length;
            var loop = new float[length];
            for (var i = 0; i < length; i++)
                loop[i] = take[i];
            for (var i = 0; i < overlap && i < length; i++)
            {
                var angle = 0.5f * Mathf.PI * i / overlap;
                loop[i] = take[i] * Mathf.Sin(angle) + take[length + i] * Mathf.Cos(angle);
            }

            return loop;
        }

        public static AudioClip Clip(string name, float[] b, int rate = Rate, bool loop = false)
        {
            if (!loop)
                Taper(b);
            var clip = AudioClip.Create(name, b.Length, 1, rate, false);
            clip.SetData(b, 0);
            return clip;
        }
    }
}
