using UnityEngine;
using UnityEngine.InputSystem;
using Freehand.VR;
using Freehand.Paint;

namespace Freehand.Spray
{
    /// <summary>
    /// Reads whichever hand currently holds the can, drives a single pooled
    /// ParticleSystem for the visual mist, raycasts for a paintable surface,
    /// and forwards paint requests to PaintManager. Never instantiates a
    /// GameObject per particle or per paint hit.
    ///
    /// SINGLE SOURCE OF TRUTH: every direction in this class comes from
    /// sprayOrigin.forward - raycast, particles, debug ray and gizmo. The
    /// particle system is required to live under sprayOrigin so it physically
    /// cannot drift from the raycast; Awake verifies that and complains if the
    /// scene is ever wired otherwise.
    ///
    /// Trigger actions are looked up by name inside the given asset (same
    /// pattern as XRInputBootstrap) instead of using an InputActionReference
    /// - see VRGrabber for why.
    /// </summary>
    public class SprayEmitter : MonoBehaviour
    {
        [SerializeField] Transform sprayOrigin;
        [SerializeField] Transform sprayParticlesRoot;
        [SerializeField] VRGrabInteractable canInteractable;
        [SerializeField] CapManager capManager;
        [SerializeField] SprayCanShake shake;
        [SerializeField] InputActionAsset inputActions;
        [SerializeField] string actionMapName = "XR";
        [SerializeField] string leftTriggerActionName = "Left Trigger";
        [SerializeField] string rightTriggerActionName = "Right Trigger";
        [SerializeField] float maxDistance = 3f;
        [SerializeField] LayerMask paintableMask = ~0;
        [SerializeField] float paintIntervalSeconds = 0.02f;

        [Tooltip("A release of the trigger shorter than this, seconds, stays in the same undoable stroke - an analog trigger hovering at the spray threshold must not split one gesture into many tiny strokes.")]
        [SerializeField] float strokeMergeSeconds = 0.25f;

        [Tooltip("Persistent URP 'Particles/Unlit' transparent material asset (built by V04SceneBuilder). Must be an asset referenced by the scene: a material assembled at runtime relies on a shader variant the Android build strips, which is what drew the spray as opaque squares on Quest.")]
        [SerializeField] Material particleMaterialAsset;

        public Color CurrentColor { get; set; } = Color.red;
        public bool IsSpraying { get; private set; }

        /// <summary>Cleaner mode: the spray removes paint instead of adding it. Set by the palette's cleaner and the color orb's cleaner.</summary>
        public bool EraseMode
        {
            get => eraseMode;
            set
            {
                if (eraseMode == value)
                    return;
                eraseMode = value;
                ApplyCurrentColorToParticles();
                EraseModeChanged?.Invoke(value);
            }
        }

        /// <summary>Raised when the cleaner is loaded or unloaded, whoever did it.</summary>
        public event System.Action<bool> EraseModeChanged;

        /// <summary>While true nothing sprays even with the trigger pressed - a tool using the trigger for something else right now (the color orb, while the can points at it).</summary>
        public bool Suppressed { get; set; }

        /// <summary>Held by an item inspection while it turns the can (the nozzle is not where the hand aims): no spray until it has turned back.</summary>
        public bool InspectionLock { get; set; }

        /// <summary>V0.14: a freestyle move is getting ready, flying the can or landing it - no spraying.</summary>
        public bool FreestyleLock { get; set; }

        /// <summary>How hard the trigger is pressed, remapped to 0-1 over the usable part of its travel.</summary>
        public float Intensity { get; private set; }

        /// <summary>Unremapped 0-1 trigger reading, before the spray start threshold - NozzleAnimator tracks this directly so the visual press and the spray threshold can never disagree about what the trigger is doing.</summary>
        public float RawTriggerValue { get; private set; }

        /// <summary>Trigger value where paint starts coming out - where the index finger must already be on the actuator (NozzleAnimator).</summary>
        public const float SprayStartThreshold = 0.15f;

        /// <summary>Distance to the surface being painted, or maxDistance when nothing is hit. Used by audio and by the cap's distance behaviour.</summary>
        public float LastHitDistance { get; private set; }

        /// <summary>What the last paint tick hit (null when it hit nothing) and where - for the paint's sound.</summary>
        public Collider LastHitCollider { get; private set; }
        public Vector3 LastHitPoint { get; private set; }

        /// <summary>The seated cap's behaviour, or null when the can has no cap on it.</summary>
        public SprayCap ActiveCap => capManager != null ? capManager.Current : fallbackCap;

        /// <summary>A can with its cap pulled off has nothing to press and no orifice to aim - it does not spray.</summary>
        public bool HasCap => ActiveCap != null;

        public Transform SprayOrigin => sprayOrigin;
        public float MaxDistance => maxDistance;
        public LayerMask PaintableMask => paintableMask;

        InputAction leftTriggerAction;
        InputAction rightTriggerAction;
        ParticleSystem sprayParticles;
        Material particleMaterial;
        SprayCap fallbackCap;
        float paintCooldown;
        float lastPaintTime = -1f;
        bool eraseMode;
        bool strokeOpen;
        float strokeIdle;

        void Awake()
        {
            fallbackCap = new SprayCap();
            LastHitDistance = maxDistance;

            ResolveActions();
            VerifyDirectionSourceOfTruth();
            SetUpParticles();

            if (capManager != null)
                capManager.CapChanged += OnCapChanged;

            ApplyCapToParticles(ActiveCap);
        }

        void OnDestroy()
        {
            if (capManager != null)
                capManager.CapChanged -= OnCapChanged;

            if (particleMaterial != null)
                Destroy(particleMaterial);
        }

        // The whole "spray goes one way, paint lands another" class of bug
        // comes from the particles and the raycast deriving their direction
        // from different transforms. Keeping the emitter under sprayOrigin
        // makes that impossible; this just makes a broken scene loud.
        void VerifyDirectionSourceOfTruth()
        {
            if (sprayOrigin == null)
            {
                Debug.LogError($"{nameof(SprayEmitter)}: sprayOrigin is not assigned - spray direction is undefined.", this);
                return;
            }

            if (sprayParticlesRoot != null && !sprayParticlesRoot.IsChildOf(sprayOrigin))
            {
                Debug.LogError(
                    $"{nameof(SprayEmitter)}: sprayParticlesRoot must be a child of sprayOrigin so particles and raycast share one direction.",
                    this);
                return;
            }

            // Being a child is not enough: an offset or rotated emitter still
            // shares the raycast's parent but visibly starts somewhere else
            // (an earlier scene had it 5cm ahead of the nozzle).
            if (sprayParticlesRoot != null && sprayParticlesRoot != sprayOrigin)
            {
                var offset = sprayOrigin.InverseTransformPoint(sprayParticlesRoot.position).magnitude;
                var angle = Quaternion.Angle(sprayOrigin.rotation, sprayParticlesRoot.rotation);
                if (offset > 0.001f || angle > 0.5f)
                    Debug.LogError($"{nameof(SprayEmitter)}: sprayParticlesRoot is offset {offset * 1000f:F1}mm / rotated {angle:F1} deg from sprayOrigin - particles would not leave the orifice.", this);
            }
        }

        void ResolveActions()
        {
            if (inputActions == null)
            {
                Debug.LogError($"{nameof(SprayEmitter)}: no InputActionAsset assigned.", this);
                return;
            }

            var map = inputActions.FindActionMap(actionMapName, throwIfNotFound: false);
            if (map == null)
            {
                Debug.LogError($"{nameof(SprayEmitter)}: action map '{actionMapName}' not found.", this);
                return;
            }

            leftTriggerAction = map.FindAction(leftTriggerActionName, throwIfNotFound: false);
            rightTriggerAction = map.FindAction(rightTriggerActionName, throwIfNotFound: false);
        }

        void SetUpParticles()
        {
            var root = sprayParticlesRoot != null ? sprayParticlesRoot : transform;
            sprayParticles = root.GetComponent<ParticleSystem>();
            if (sprayParticles == null)
                sprayParticles = root.gameObject.AddComponent<ParticleSystem>();

            var main = sprayParticles.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.38f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.0f, 3.2f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 220;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = sprayParticles.emission;
            emission.enabled = false;

            var shape = sprayParticles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            // Orifice-sized: the mist starts as a point at the hole and fans
            // out through the cone angle, instead of appearing as a 16mm-wide
            // disc in front of a ~1mm hole.
            shape.radius = 0.0015f;

            var colorOverLifetime = sprayParticles.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                // URP Particles/Unlit multiplies by the particle colour, so
                // this fade is now actually applied (URP/Unlit ignored it).
                new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0.55f, 0.4f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = gradient;

            var sizeOverLifetime = sprayParticles.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 2.4f));

            // Cheap per-particle turbulence: this is what turns "particles
            // flying in a perfect cone" into something that reads as a loose
            // aerosol mist. Strength/frequency are low so it stays visible
            // without adding real cost at ~220 max particles - strength itself
            // is retuned per cap (ApplyCapToParticles).
            var noise = sprayParticles.noise;
            noise.enabled = true;
            noise.strength = 0.15f;
            noise.frequency = 0.8f;
            noise.scrollSpeed = 0f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var psRenderer = sprayParticles.GetComponent<ParticleSystemRenderer>();
            if (psRenderer == null)
                return;

            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;

            // One instance per can, copied from the persistent asset: it keeps
            // the asset's keywords (so it uses the exact variant the build
            // compiled) and only its _BaseColor changes with the paint colour.
            // No fallback that builds a material from Shader.Find - that is
            // the path that rendered opaque squares on device.
            if (particleMaterialAsset == null)
            {
                Debug.LogError($"{nameof(SprayEmitter)}: particleMaterialAsset is not assigned - run the scene builder. Spray particles will not render correctly.", this);
                return;
            }

            particleMaterial = new Material(particleMaterialAsset) { name = particleMaterialAsset.name + " (instance)" };
            psRenderer.sharedMaterial = particleMaterial;
        }

        void OnCapChanged(SprayCap cap) => ApplyCapToParticles(cap);

        void ApplyCapToParticles(SprayCap cap)
        {
            if (sprayParticles == null || cap == null)
                return;

            var main = sprayParticles.main;
            // Denser near the nozzle, thinning with age: a wider size spread
            // than a plain min/max reads as more particles "catching air" at
            // different rates instead of one uniform blob.
            main.startSize = new ParticleSystem.MinMaxCurve(cap.particleStartSize * 0.45f, cap.particleStartSize * 1.15f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(cap.particleSpeedMin, cap.particleSpeedMax);

            var shape = sprayParticles.shape;
            shape.angle = cap.coneAngleDegrees;
            // Softens the cone's edge (a hard-edged cone looks like a laser,
            // not aerosol) and, at higher values, keeps a few particles
            // wandering outside the nominal cone entirely - the wide, loose
            // fan a Fat cap is supposed to have.
            shape.randomDirectionAmount = cap.spreadRandomness;

            var noise = sprayParticles.noise;
            noise.strength = Mathf.Lerp(0.06f, 0.35f, cap.spreadRandomness);

            var emission = sprayParticles.emission;
            emission.rateOverTime = cap.emissionRate;
        }

        // Distance-based feel (kept separate from ApplyCapToParticles, which
        // only runs on cap change): farther targets get a longer-lived, faster,
        // slightly bigger mist so it visibly has to travel and disperse more,
        // matching the paint side's own distance falloff in SprayCap.
        void ApplyDistanceFeelToParticles(float distanceMeters)
        {
            if (sprayParticles == null)
                return;

            var t = Mathf.Clamp01(distanceMeters / maxDistance);
            var main = sprayParticles.main;
            var cap = ActiveCap;
            var speedBoost = Mathf.Lerp(1f, 1.35f, t);
            main.startSpeed = new ParticleSystem.MinMaxCurve(cap.particleSpeedMin * speedBoost, cap.particleSpeedMax * speedBoost);

            // V0.14: the mist ends where the paint lands. Without this, close to a wall most of it flew on through
            // the surface (76% of live particles past the face at 15 cm, up to 0.8 m behind it) - hidden by an
            // opaque virtual wall, but a cloud through a real one in mixed reality, where nothing hides it. The
            // fastest particle now lives just long enough to reach the surface that was hit; slower ones thin out
            // before it. Nothing hit: the old open-air lifetimes.
            var shortest = Mathf.Lerp(0.16f, 0.3f, t);
            var longest = Mathf.Lerp(0.28f, 0.55f, t);
            if (LastHitCollider != null)
            {
                var reach = distanceMeters / Mathf.Max(0.1f, cap.particleSpeedMax * speedBoost);
                longest = Mathf.Max(0.03f, Mathf.Min(longest, reach * 1.05f));
                shortest = Mathf.Min(shortest, longest * 0.7f);
            }

            main.startLifetime = new ParticleSystem.MinMaxCurve(shortest, longest);
        }

        void Update()
        {
            var triggerValue = ReadActiveTrigger();
            RawTriggerValue = triggerValue;

            // The trigger is a real analog axis on Touch controllers, so use
            // it: a light press mists, a full press lays down solid paint.
            // Below the start threshold nothing comes out at all, which is what
            // makes feathering possible instead of a binary on/off.
            const float startThreshold = SprayStartThreshold;
            var shouldSpray = triggerValue > startThreshold && HasCap && !Suppressed && !InspectionLock && !FreestyleLock;
            Intensity = shouldSpray
                ? Mathf.Clamp01(Mathf.InverseLerp(startThreshold, 0.9f, triggerValue) * 0.75f + 0.25f)
                : 0f;

            if (shouldSpray != IsSpraying)
                SetSpraying(shouldSpray);

            if (IsSpraying)
            {
                UpdateSprayHit();
                UpdateEmissionIntensity();
                ApplyDistanceFeelToParticles(LastHitDistance);
            }
            else if (strokeOpen)
            {
                // Released for longer than a flick: the stroke is done.
                strokeIdle += Time.deltaTime;
                if (strokeIdle >= strokeMergeSeconds)
                {
                    strokeOpen = false;
                    PaintManager.EndStroke(this);
                }
            }

#if UNITY_EDITOR
            DrawDebugRay();
#endif
        }

        void UpdateEmissionIntensity()
        {
            if (sprayParticles == null)
                return;

            var emission = sprayParticles.emission;
            emission.rateOverTime = ActiveCap.emissionRate * Mathf.Lerp(0.35f, 1f, Intensity) * PressureMultiplier;

            // A light trigger press should look weak, not just sparse - a
            // half-pressed trigger emitting full-size, full-speed particles
            // at a lower rate still reads as "full spray, fewer particles."
            var main = sprayParticles.main;
            var cap = ActiveCap;
            var sizeScale = Mathf.Lerp(0.7f, 1f, Intensity);
            main.startSize = new ParticleSystem.MinMaxCurve(
                cap.particleStartSize * 0.45f * sizeScale,
                cap.particleStartSize * 1.15f * sizeScale);
        }

        // A freshly shaken can sprays a little harder for a few seconds. Not a
        // paint simulation - just enough that shaking feels like it does
        // something.
        float PressureMultiplier => shake != null ? shake.PressureMultiplier : 1f;

        void DrawDebugRay()
        {
            if (sprayOrigin == null)
                return;

            var color = IsSpraying ? Color.green : Color.cyan;
            Debug.DrawRay(sprayOrigin.position, sprayOrigin.forward * maxDistance, color);
        }

        float ReadActiveTrigger()
        {
            if (canInteractable == null || !canInteractable.IsGrabbed)
                return 0f;

            var action = canInteractable.HeldByLeftHand ? leftTriggerAction : rightTriggerAction;
            return action != null ? action.ReadValue<float>() : 0f;
        }

        void SetSpraying(bool spraying)
        {
            IsSpraying = spraying;

            // One trigger press = one undoable stroke; a release shorter
            // than strokeMergeSeconds continues it (Update ends it after).
            if (spraying)
            {
                // An undo or Clear All meanwhile closed the stroke: a new
                // one starts, it never paints into an older action.
                if (strokeOpen && PaintHistory.StrokeOpen)
                    PaintManager.BreakSegment(this);
                else
                    PaintManager.BeginStroke(this);
                strokeOpen = true;
                strokeIdle = 0f;
                lastPaintTime = -1f;
            }

            if (sprayParticles == null)
                return;

            if (spraying)
            {
                ApplyCurrentColorToParticles();
                ApplyCapToParticles(ActiveCap);
            }

            var emission = sprayParticles.emission;
            emission.enabled = spraying;

            if (spraying)
                sprayParticles.Play();
            else
                sprayParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        void ApplyCurrentColorToParticles()
        {
            if (particleMaterial == null)
                return;

            // Cleaner mode sprays a pale solvent mist so it is obvious at a
            // glance that this pass removes paint rather than adding it.
            particleMaterial.SetColor("_BaseColor", EraseMode ? new Color(0.85f, 0.9f, 0.95f) : CurrentColor);
        }

        /// <summary>Called by the color palette / color manager so a color change mid-session is reflected immediately.</summary>
        public void SetColor(Color color)
        {
            CurrentColor = color;
            ApplyCurrentColorToParticles();
        }

        void UpdateSprayHit()
        {
            if (sprayOrigin == null)
                return;

            paintCooldown -= Time.deltaTime;
            if (paintCooldown > 0f)
                return;

            paintCooldown = paintIntervalSeconds;

            // Paint per tick follows the time it covers, not the frame rate:
            // the same pass lays the same paint at 72, 90 or 120 Hz.
            var now = Time.time;
            var elapsed = lastPaintTime < 0f ? paintIntervalSeconds : Mathf.Clamp(now - lastPaintTime, paintIntervalSeconds * 0.5f, paintIntervalSeconds * 3f);
            lastPaintTime = now;
            var flow = elapsed / paintIntervalSeconds;

            var direction = sprayOrigin.forward;
            if (!Physics.Raycast(sprayOrigin.position, direction, out var hit, maxDistance, paintableMask))
            {
                LastHitDistance = maxDistance;
                LastHitCollider = null;
                return;
            }

            LastHitDistance = hit.distance;
            LastHitCollider = hit.collider;
            LastHitPoint = hit.point;

            var cap = ActiveCap;
            var stamp = new SprayStamp
            {
                color = CurrentColor,
                radiusMeters = cap.RadiusAtDistance(hit.distance),
                alpha = cap.AlphaAtDistance(hit.distance) * Intensity * PressureMultiplier * flow,
                stretch = 1f,
                stretchAngle = 0f,
                erase = EraseMode,
                capName = cap.displayName,
            };

            PaintManager.ApplyPaint(hit, direction, stamp, this);
        }
    }
}
