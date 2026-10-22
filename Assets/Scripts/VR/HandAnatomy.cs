using UnityEngine;

namespace Freehand.VR
{
    /// <summary>
    /// Anatomical frame of one rigged hand, measured from its own bones in
    /// the bind pose - never from assumed bone axes (Blender/FBX bone rolls
    /// differ per finger and are mirrored between hands).
    ///
    ///   palmFacing    the side the palm faces / the fingers curl toward,
    ///                 taken from the fingers' own rest curl in the bind pose
    ///   knuckleAxis   pinky knuckle -> index knuckle
    ///   fingerForward wrist -> knuckles, orthogonal to the other two
    ///   gripAxis      the axis of a cylinder held in a power grip
    ///
    /// A gripped cylinder does not lie along the knuckle line: it crosses the
    /// palm diagonally, from the hypothenar eminence (the middle of the 5th
    /// metacarpal) to the base of the index finger. That diagonal is measured
    /// from the rig's own bones (palm_pinky / f_pinky.01 / f_index.01) - on
    /// fps-hands.fbx it runs ~21 degrees from the knuckle line, toward the
    /// fingers at the index end. It is what lets the index finger rise over
    /// the top of a held can while the other fingers wrap its body.
    ///
    /// GripRotation is this hand's version of the OpenXR "grip" pose
    /// orientation (Unity axes): +Z along the tube a closed fist forms
    /// (little finger -> thumb side) - the grip axis above - and +X the palm
    /// normal (away from the palm for the left hand, into it for the right -
    /// +X points to the user's right for both), +Y = Z x X. Aligning this
    /// frame to the tracked controller's grip pose is what puts the virtual
    /// hand where the real hand is: the controller handle is exactly such a
    /// gripped cylinder.
    ///
    /// Everything is stored in the hand bone's rotation frame, in world
    /// meters (hand scale excluded), so it stays valid while the hand moves.
    /// </summary>
    public readonly struct HandAnatomy
    {
        public readonly Vector3 PalmFacing;
        public readonly Vector3 KnuckleAxis;
        public readonly Vector3 FingerForward;
        public readonly Vector3 GripAxis;
        public readonly Quaternion GripRotation;
        public readonly Vector3 PalmReference;
        public readonly float HandLength;
        public readonly float PalmHalfThickness;

        HandAnatomy(Vector3 palmFacing, Vector3 knuckleAxis, Vector3 fingerForward, Vector3 gripAxis, Quaternion gripRotation, Vector3 palmReference, float handLength, float palmHalfThickness)
        {
            PalmFacing = palmFacing;
            KnuckleAxis = knuckleAxis;
            FingerForward = fingerForward;
            GripAxis = gripAxis;
            GripRotation = gripRotation;
            PalmReference = palmReference;
            HandLength = handLength;
            PalmHalfThickness = palmHalfThickness;
        }

        /// <summary>Angle, degrees, between the grip axis and the knuckle line.</summary>
        public float GripObliquityDegrees => Vector3.Angle(GripAxis, KnuckleAxis);

        /// <summary>
        /// Axis of a cylinder of <paramref name="radius"/> held in this fist (hand-rotation frame, meters):
        /// resting on the palm along the grip diagonal. With the controller handle's radius this is where
        /// the controller's grip position sits in the real hand.
        /// </summary>
        public Vector3 GripCenter(float radius) => PalmReference + PalmFacing * (PalmHalfThickness + radius);

        /// <summary>
        /// Measures a hand from its bones. Finger chains are given as
        /// [index, middle, ring, pinky], each as its three segment bones plus
        /// the fingertip ("_end") bone; the pinky's first segment must hang
        /// from its metacarpal bone (the hypothenar end of the grip axis).
        /// Must be called while the bones are still in their bind pose.
        /// </summary>
        public static bool TryMeasure(Transform hand, FingerBones[] fingers, Transform[] fingerTips, bool isLeftHand, out HandAnatomy anatomy)
        {
            anatomy = default;
            if (hand == null || fingers == null || fingerTips == null || fingers.Length != 4 || fingerTips.Length != 4)
                return false;

            var inverse = Quaternion.Inverse(hand.rotation);
            var wrist = hand.position;

            // The palm side is where the fingers already curl in the bind
            // pose: the bend of each finger, taken perpendicular to its
            // proximal segment, points into the palm.
            var palmFacing = Vector3.zero;
            var knuckleCenter = Vector3.zero;
            for (var i = 0; i < 4; i++)
            {
                var f = fingers[i];
                if (f.segment1 == null || f.segment2 == null || fingerTips[i] == null)
                    return false;

                var proximal = (f.segment2.position - f.segment1.position).normalized;
                var distal = (fingerTips[i].position - f.segment2.position).normalized;
                palmFacing += Vector3.ProjectOnPlane(distal, proximal).normalized;
                knuckleCenter += f.segment1.position;
            }

            knuckleCenter /= 4f;
            palmFacing = palmFacing.normalized;

            var knuckleAxis = Vector3.ProjectOnPlane(fingers[0].segment1.position - fingers[3].segment1.position, palmFacing).normalized;
            var fingerForward = Vector3.Cross(palmFacing, knuckleAxis).normalized;
            if (Vector3.Dot(fingerForward, knuckleCenter - wrist) < 0f)
                fingerForward = -fingerForward;

            if (palmFacing.sqrMagnitude < 0.5f || knuckleAxis.sqrMagnitude < 0.5f)
                return false;

            // Grip diagonal: hypothenar (middle of the 5th metacarpal) ->
            // index knuckle, in the palm plane.
            var pinkyMetacarpal = fingers[3].segment1.parent;
            if (pinkyMetacarpal == null || pinkyMetacarpal == hand)
                return false;

            var hypothenar = (pinkyMetacarpal.position + fingers[3].segment1.position) * 0.5f;
            var indexKnuckle = fingers[0].segment1.position;
            var gripAxis = Vector3.ProjectOnPlane(indexKnuckle - hypothenar, palmFacing).normalized;
            if (gripAxis.sqrMagnitude < 0.5f || Vector3.Dot(gripAxis, knuckleAxis) < 0.5f)
                return false;

            // OpenXR grip frame, in Unity axes.
            var x = isLeftHand ? palmFacing : -palmFacing;
            var z = gripAxis;
            var y = Vector3.Cross(z, x);
            var gripWorld = Quaternion.LookRotation(z, y);

            var handLength = Vector3.Distance(wrist, fingerTips[1].position);

            // A held cylinder rests on the palm along the diagonal; the bones
            // run through the middle of the hand, half its thickness inside.
            var palmReference = (hypothenar + indexKnuckle) * 0.5f;

            anatomy = new HandAnatomy(
                inverse * palmFacing,
                inverse * knuckleAxis,
                inverse * fingerForward,
                inverse * gripAxis,
                inverse * gripWorld,
                inverse * (palmReference - wrist),
                handLength,
                0.065f * handLength);
            return true;
        }
    }
}
