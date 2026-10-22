using Freehand.Spray;
using Freehand.VR;
using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>
    /// The sound of the ball rattling inside the can. It is not "shaking
    /// -> play"; it listens to the can's actual motion.
    ///
    /// The ball keeps going when the can is stopped or reversed, so it strikes
    /// the inside at the instant the can accelerates hard. This reads the
    /// can's linear acceleration and its angular acceleration (turned into
    /// an equivalent at a hand-sized lever arm), adds them into one "energy",
    /// and every local peak of that energy above a threshold is one strike.
    /// Strikes are therefore as frequent as the hand really reverses - no
    /// timer, no loop - and a floor interval only stops pose jitter from
    /// machine-gunning.
    ///
    /// Three classes of strike differ in character (SfxBank.Rattle): a tick,
    /// a ball-and-body knock, a cascade with the liquid slapping. Along the
    /// can's axis the ball rings off the domes; across it, it knocks on the
    /// wall; a mostly-rotational motion swirls it. Nothing plays while the
    /// can is not in a hand (a thrown can's sounds are its impacts) or while
    /// it is spraying (the jet takes the foreground).
    /// </summary>
    public class CanMotionAudio : MonoBehaviour
    {
        [Header("Energy thresholds (m/s^2 equivalent)")]
        [SerializeField] float lightThreshold = 7f;
        [SerializeField] float mediumThreshold = 22f;
        [SerializeField] float hardThreshold = 48f;

        [Tooltip("Meters: how much a rotation counts against a straight movement (a wrist twist swings the can's end about this far).")]
        [SerializeField] float leverArm = 0.1f;

        [Header("Strike spacing (s)")]
        [SerializeField] float lightInterval = 0.12f;
        [SerializeField] float mediumInterval = 0.085f;
        [SerializeField] float hardInterval = 0.065f;

        VRGrabInteractable grab;
        SprayEmitter emitter;
        SprayCanSfx sfx;

        Vector3 previousPosition;
        Quaternion previousRotation;
        Vector3 velocity, previousVelocity;
        Vector3 angularVelocity, previousAngularVelocity;
        Vector3 acceleration, angularAcceleration;
        float energy0, energy1, energy2;
        float lastStrike = -10f;
        float ignoreUntil;
        bool wasGrabbed;
        readonly int[] lastVariant = new int[SfxBank.ShakeLevels] { -1, -1, -1 };

        /// <summary>The current energy of the can's motion (m/s^2 equivalent), for tuning and validation.</summary>
        public float Energy => energy1;

        void Awake()
        {
            SfxBank.EnsureCan();
            grab = GetComponent<VRGrabInteractable>();
            emitter = GetComponent<SprayEmitter>();
            previousPosition = transform.position;
            previousRotation = transform.rotation;
        }

        void Update()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            var position = transform.position;
            var rotation = transform.rotation;
            var instantVelocity = (position - previousPosition) / dt;

            var delta = rotation * Quaternion.Inverse(previousRotation);
            delta.ToAngleAxis(out var degrees, out var axis);
            if (degrees > 180f)
                degrees -= 360f;
            var instantAngular = float.IsNaN(axis.x) || float.IsInfinity(axis.x) ? Vector3.zero : axis * (degrees * Mathf.Deg2Rad / dt);
            previousPosition = position;
            previousRotation = rotation;

            var grabbed = grab != null && grab.IsGrabbed;
            if (grabbed && !wasGrabbed)
            {
                // Taking hold moves the can to the hand: not a shake.
                ignoreUntil = Time.time + 0.25f;
                velocity = previousVelocity = instantVelocity;
                angularVelocity = previousAngularVelocity = instantAngular;
                acceleration = angularAcceleration = Vector3.zero;
                energy0 = energy1 = energy2 = 0f;
            }

            wasGrabbed = grabbed;

            // Low-pass the velocities, differentiate, low-pass the result:
            // tracking jitter must not read as acceleration.
            var k = 1f - Mathf.Exp(-dt / 0.02f);
            velocity = Vector3.Lerp(velocity, instantVelocity, k);
            angularVelocity = Vector3.Lerp(angularVelocity, instantAngular, k);
            var rawAcceleration = (velocity - previousVelocity) / dt;
            var rawAngular = (angularVelocity - previousAngularVelocity) / dt;
            previousVelocity = velocity;
            previousAngularVelocity = angularVelocity;

            var k2 = 1f - Mathf.Exp(-dt / 0.025f);
            acceleration = Vector3.Lerp(acceleration, rawAcceleration, k2);
            angularAcceleration = Vector3.Lerp(angularAcceleration, rawAngular, k2);

            if (!grabbed || Time.time < ignoreUntil)
            {
                energy0 = energy1 = energy2 = 0f;
                return;
            }

            var linear = acceleration.magnitude;
            var angular = leverArm * angularAcceleration.magnitude;
            energy2 = energy1;
            energy1 = energy0;
            energy0 = linear + angular;

            // A strike is a local peak of the energy above the light threshold.
            if (energy1 > energy2 && energy1 >= energy0 && energy1 >= lightThreshold)
                Strike(energy1, linear, angular);
        }

        void Strike(float energy, float linear, float angular)
        {
            if (emitter != null && emitter.IsSpraying)
                return;

            var level = energy >= hardThreshold ? 2 : energy >= mediumThreshold ? 1 : 0;
            var interval = level == 2 ? hardInterval : level == 1 ? mediumInterval : lightInterval;
            if (Time.time - lastStrike < interval)
                return;

            // This can's own voices (there may be more than one can).
            if (sfx == null)
                sfx = GetComponentInChildren<SprayCanSfx>(true);
            if (sfx == null)
                sfx = SprayCanSfx.Instance;
            if (sfx == null)
                return;
            lastStrike = Time.time;

            // How strong, within its class.
            var low = level == 0 ? lightThreshold : level == 1 ? mediumThreshold : hardThreshold;
            var high = level == 0 ? mediumThreshold : level == 1 ? hardThreshold : hardThreshold * 1.8f;
            var within = Mathf.InverseLerp(low, high, energy);
            var volume = level == 0 ? Mathf.Lerp(0.22f, 0.38f, within) : level == 1 ? Mathf.Lerp(0.38f, 0.58f, within) : Mathf.Lerp(0.5f, 0.82f, within);

            // What kind of motion: along the can's axis, across it, or turning.
            var axial = linear > 0.01f ? Mathf.Abs(Vector3.Dot(acceleration / linear, transform.up)) : 0f;
            var circular = energy > 0.01f ? angular / energy : 0f;

            int variant;
            if (circular > 0.55f && level >= 1)
                variant = 4;
            else if (axial > 0.6f)
                variant = Random.Range(0, 2);
            else
                variant = Random.Range(2, 4);

            // Never the same clip twice in a row within a class - but stay in
            // the character the motion asked for (0<->1 axial, 2<->3 lateral).
            if (variant == lastVariant[level])
                variant = variant == 4 ? 3 : (variant ^ 1);
            lastVariant[level] = variant;

            var pitch = Mathf.Lerp(1.08f, 0.94f, Mathf.InverseLerp(lightThreshold, hardThreshold * 1.5f, energy)) * (1f + Random.Range(-0.03f, 0.03f)) * (1f + 0.04f * axial);
            sfx.Play(SfxBank.Rattle[level][variant], SfxCategory.Secondary, volume, pitch);
        }
    }
}
