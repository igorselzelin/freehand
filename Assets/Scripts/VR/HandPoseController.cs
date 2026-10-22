using Freehand.Spray;
using UnityEngine;

namespace Freehand.VR
{
    /// <summary>
    /// Approximates hand poses (Open / GrabCan / Spray) on a hand mesh that has
    /// no rig, no skin and no bones (confirmed in HandMeshCleanup: the imported
    /// GLB's two hand copies were combined into one static Mesh asset - there
    /// is no armature to drive and no separate finger geometry to move
    /// independently).
    ///
    /// LIMITATION (documented per the V0.6 brief rather than worked around):
    /// with a single fused mesh, per-finger curl is not possible without a new
    /// rigged asset. What IS possible, and what this does, is treat the whole
    /// hand as one rigid piece and blend its local position/rotation between a
    /// few hand-authored offsets - open, wrapped around the can, and a small
    /// extra tilt while spraying. That reads as "the hand's relationship to
    /// the can changed" even though no finger visibly curls. Cheap (pure
    /// Transform lerp, no per-vertex work, no new GameObjects at runtime).
    ///
    /// Targets the "Hand" transform BuildHand creates (the direct parent of
    /// the mesh), not HandVisual itself - HandVisual carries the import-time
    /// scale/orientation fix and must not be touched here.
    /// </summary>
    public class HandPoseController : MonoBehaviour
    {
        [SerializeField] Transform poseTarget;
        [SerializeField] bool isLeftHand;
        [SerializeField] VRGrabInteractable canGrab;
        [SerializeField] SprayEmitter sprayEmitter;

        [Header("Pose offsets (hand-local space)")]
        [Tooltip("Extra rotation, degrees, applied while the can is held - approximates fingers wrapping around the body.")]
        [SerializeField] Vector3 grabRotationEuler = new Vector3(-12f, 0f, 6f);
        [SerializeField] Vector3 grabPositionOffset = new Vector3(0f, 0f, 0.004f);

        [Tooltip("Additional rotation on top of the grab pose, scaled by spray Intensity (0-1) - approximates the index finger pressing the nozzle.")]
        [SerializeField] Vector3 sprayExtraRotationEuler = new Vector3(-8f, 0f, 0f);

        [SerializeField] float blendSpeed = 14f;

        Vector3 restPosition;
        Quaternion restRotation;
        Vector3 currentPositionOffset;
        Quaternion currentRotationOffset = Quaternion.identity;

        void Awake()
        {
            if (poseTarget == null)
                poseTarget = transform;

            restPosition = poseTarget.localPosition;
            restRotation = poseTarget.localRotation;
        }

        void LateUpdate()
        {
            if (poseTarget == null)
                return;

            var isHoldingCan = canGrab != null && canGrab.IsGrabbed && canGrab.HeldByLeftHand == isLeftHand;

            // RawTriggerValue (0-1, unremapped), not Intensity - the same
            // signal NozzleAnimator reads for the cap's press, so the finger
            // curl and the cap going down are locked to the exact same
            // number instead of two independently-remapped ones drifting
            // out of sync with each other.
            var triggerValue = isHoldingCan && sprayEmitter != null ? sprayEmitter.RawTriggerValue : 0f;

            var targetPositionOffset = isHoldingCan ? grabPositionOffset : Vector3.zero;
            var targetRotationEuler = isHoldingCan ? grabRotationEuler + sprayExtraRotationEuler * triggerValue : Vector3.zero;
            var targetRotationOffset = Quaternion.Euler(targetRotationEuler);

            var t = 1f - Mathf.Exp(-blendSpeed * Time.deltaTime);
            currentPositionOffset = Vector3.Lerp(currentPositionOffset, targetPositionOffset, t);
            currentRotationOffset = Quaternion.Slerp(currentRotationOffset, targetRotationOffset, t);

            poseTarget.localPosition = restPosition + currentPositionOffset;
            poseTarget.localRotation = restRotation * currentRotationOffset;
        }
    }
}
