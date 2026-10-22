using Freehand.VR;
using UnityEngine;

namespace Freehand.Audio
{
    /// <summary>
    /// Sound of a loose object landing: the can or a cap. It answers the
    /// physics' own collision events - how fast the two bodies met, what the
    /// other one is made of (AudioSurface), and which part of the can took
    /// the blow (an end or the side) - so a drop is a tap and a throw is a
    /// hit, on concrete and on metal alike.
    ///
    /// Only while loose: a held object is moved by the hand, and its
    /// contacts are not impacts. The contact point is where it plays.
    /// </summary>
    public class ImpactAudio : MonoBehaviour
    {
        public enum Kind { Can, Cap }

        [SerializeField] Kind kind = Kind.Can;

        [Tooltip("m/s: slower contacts (settling, sliding) make no sound.")]
        [SerializeField] float minSpeed = 0.35f;

        [Tooltip("Seconds between two impacts of this object.")]
        [SerializeField] float cooldown = 0.07f;

        [Tooltip("m/s: below this is a light touch, above the next a hard blow.")]
        [SerializeField] float mediumSpeed = 1.2f;
        [SerializeField] float hardSpeed = 3f;

        VRGrabInteractable grab;
        float lastImpact = -10f;
        int lastVariant = -1;

        // How a surface changes the pitch of a small cap's tick.
        static readonly float[] CapSurfacePitch = { 1f, 0.9f, 1.15f, 0.8f, 0.85f };

        void Awake()
        {
            SfxBank.EnsureCan();
            grab = GetComponent<VRGrabInteractable>();
        }

        void OnCollisionEnter(Collision collision)
        {
            var sfx = SprayCanSfx.Instance;
            if (sfx == null || (grab != null && grab.IsGrabbed))
                return;

            var now = Time.time;
            if (now - lastImpact < cooldown)
                return;

            var speed = collision.relativeVelocity.magnitude;
            if (speed < minSpeed)
                return;
            lastImpact = now;

            var contact = collision.GetContact(0);
            var surface = AudioSurface.Of(collision.collider);
            var level = speed >= hardSpeed ? 2 : speed >= mediumSpeed ? 1 : 0;
            var within = level == 0 ? Mathf.InverseLerp(minSpeed, mediumSpeed, speed)
                : level == 1 ? Mathf.InverseLerp(mediumSpeed, hardSpeed, speed)
                : Mathf.InverseLerp(hardSpeed, hardSpeed * 2.5f, speed);
            var variant = Random.Range(0, 2);
            if (variant == lastVariant)
                variant = 1 - variant;
            lastVariant = variant;

            if (kind == Kind.Cap)
            {
                var capLevel = Mathf.Min(level, 1);
                var capVolume = Mathf.Lerp(0.25f, 0.7f, (level + within) / 3f);
                sfx.PlayAt(SfxBank.CapDrop[capLevel][Random.Range(0, 3)], SfxCategory.Core, capVolume, contact.point, CapSurfacePitch[(int)surface] * (1f + Random.Range(-0.05f, 0.05f)));
                return;
            }

            // The blow's direction against the can's axis: an end (axial) rings higher.
            var axial = Mathf.Abs(Vector3.Dot(contact.normal, transform.up));
            var volume = level == 0 ? Mathf.Lerp(0.2f, 0.45f, within) : level == 1 ? Mathf.Lerp(0.45f, 0.75f, within) : Mathf.Lerp(0.75f, 1f, within);
            var pitch = (1f + 0.08f * axial) * (1f + Random.Range(-0.04f, 0.04f));
            sfx.PlayAt(SfxBank.CanImpact[(int)surface][level][variant], SfxCategory.Core, volume, contact.point, pitch);

            // The ball inside answers a hard blow a moment later.
            if (level >= 1)
                sfx.PlayAt(SfxBank.Rattle[level == 2 ? 2 : 1][axial > 0.6f ? Random.Range(0, 2) : Random.Range(2, 4)], SfxCategory.Secondary, volume * 0.5f, transform.position, 1f, 0.025f);
        }
    }
}
