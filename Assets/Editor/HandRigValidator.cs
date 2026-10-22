using System.Collections.Generic;
using System.Text;
using Freehand.VR;
using UnityEditor;
using UnityEngine;

// Checks the rigged hands (fps-hands.fbx) the way HandRig and
// HandBonePoseController will use them: each arm wired to its own side's
// bones along the real chains, the bones that actually deform the mesh,
// every fingertip present, and each hand measurable from its own bones
// (HandAnatomy) with the right handedness - the same preconditions both
// components check at startup, verified here without entering Play Mode.
//
// Used by V04SceneBuilder.ValidateScene and runnable on its own from the
// menu. In Edit Mode the anatomy is measured from the scene's bind pose; in
// Play Mode the components' own startup results are reported instead.
public static class HandRigValidator
{
    static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "pinky" };

    // Adult hands, wrist crease to middle fingertip.
    const float MinHandLengthMeters = 0.15f;
    const float MaxHandLengthMeters = 0.21f;

    // In the bind pose hand.X sits exactly on forearm.X_end (measured in the FBX).
    const float MaxWristGapMeters = 0.005f;

    [MenuItem("Freehand/V0.8/3 - Validate Hand Rig")]
    static void RunFromMenu()
    {
        var hands = GameObject.Find("Hands");
        var report = new StringBuilder("[HandRigValidator]\n");
        var failures = 0;
        foreach (var r in Validate(hands))
        {
            var tag = r.IsInfo ? "INFO " : (r.Ok ? "OK   " : "FALHA");
            report.AppendLine($"  {tag} {r.Label}: {r.Detail}");
            if (!r.Ok && !r.IsInfo)
                failures++;
        }

        report.Insert(0, failures == 0 ? "Sem falhas.\n" : $"{failures} falha(s).\n");
        if (failures == 0)
            Debug.Log(report.ToString(), hands);
        else
            Debug.LogWarning(report.ToString(), hands);
    }

    public static List<CapSystemValidator.Result> Validate(GameObject hands)
    {
        var results = new List<CapSystemValidator.Result>();
        void Check(bool ok, string label, string detail) => results.Add(new CapSystemValidator.Result(ok, false, label, detail));
        void Info(string label, string detail) => results.Add(new CapSystemValidator.Result(true, true, label, detail));

        var rig = hands != null ? hands.GetComponent<HandRig>() : null;
        Check(rig != null, "HandRig", hands == null ? "objeto 'Hands' ausente" : rig != null ? "presente em 'Hands'" : "ausente em 'Hands'");
        if (rig == null)
            return results;

        var so = new SerializedObject(rig);
        var head = so.FindProperty("head")?.objectReferenceValue as Transform;
        var headIsCamera = head != null && head.GetComponent<Camera>() != null;
        Check(headIsCamera, "HandRig.head", head == null ? "NULO - os ombros nao acompanham o jogador" : headIsCamera ? $"'{head.name}' (camera)" : $"'{head.name}' NAO e uma camera");

        var skin = hands.GetComponentInChildren<SkinnedMeshRenderer>(true);
        var skinBones = new HashSet<Transform>(skin != null ? skin.bones : new Transform[0]);
        Check(skin != null, "Hands: SkinnedMeshRenderer", skin == null ? "ausente" : $"{skin.bones.Length} bones, rootBone '{(skin.rootBone != null ? skin.rootBone.name : "NULO")}'");

        // The hand mesh blends up to 4 bones per vertex; a 2-bone quality
        // level (Android's default was) creases the palm heel and thumb root.
        var qualityAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
        if (qualityAssets.Length > 0)
        {
            var levels = new SerializedObject(qualityAssets[0]).FindProperty("m_QualitySettings");
            var weak = new List<string>();
            for (var i = 0; i < levels.arraySize; i++)
            {
                var level = levels.GetArrayElementAtIndex(i);
                if (level.FindPropertyRelative("skinWeights").intValue < (int)SkinWeights.FourBones)
                    weak.Add($"{level.FindPropertyRelative("name").stringValue} ({level.FindPropertyRelative("skinWeights").intValue})");
            }

            Check(weak.Count == 0, "Qualidade: ossos por vertice", weak.Count == 0 ? "4 em todos os niveis (inclusive os do Android/Quest)" : $"menos de 4 em: {string.Join(", ", weak)} - a mao deforma no Quest");
        }

        var leftHand = ValidateArm(rig, so, true, skinBones, Check, Info);
        var rightHand = ValidateArm(rig, so, false, skinBones, Check, Info);

        // A swapped pair would put the left hand on the right in the rig's
        // own frame (it faces +Z, like the player).
        if (leftHand != null && rightHand != null)
        {
            var leftX = rig.transform.InverseTransformPoint(leftHand.position).x;
            var rightX = rig.transform.InverseTransformPoint(rightHand.position).x;
            Check(Application.isPlaying || leftX < rightX, "Maos: esquerda a esquerda",
                Application.isPlaying ? "n/a em Play Mode (maos seguem os controles)" : $"x esquerda {leftX:F3} < x direita {rightX:F3} no frame do rig");
        }

        return results;
    }

    // Returns the arm's hand bone when it is wired, for the cross-arm check.
    static Transform ValidateArm(HandRig rig, SerializedObject so, bool isLeft, HashSet<Transform> skinBones,
        System.Action<bool, string, string> check, System.Action<string, string> info)
    {
        var s = isLeft ? "L" : "R";
        var arm = isLeft ? "left" : "right";
        var label = $"HandRig.{arm}";

        Transform Field(string name) => so.FindProperty($"{arm}.{name}")?.objectReferenceValue as Transform;

        var isLeftFlag = so.FindProperty($"{arm}.isLeft")?.boolValue;
        check(isLeftFlag == isLeft, $"{label}.isLeft", $"{isLeftFlag?.ToString() ?? "campo ausente"} (esperado {isLeft})");

        var controller = Field("controller");
        var controllerOk = controller != null && controller.name.Contains(isLeft ? "Left" : "Right");
        check(controllerOk, $"{label}.controller", controller == null ? "NULO" : $"'{controller.name}'");

        var grabber = so.FindProperty($"{arm}.grabber")?.objectReferenceValue as VRGrabber;
        if (grabber == null)
        {
            check(false, $"{label}.grabber", "NULO - dedos nao sabem o que a mao segura, objetos nao seguem a mao renderizada");
        }
        else
        {
            var grabberSo = new SerializedObject(grabber);
            var grabberLeft = grabberSo.FindProperty("isLeftHand").boolValue;
            var grabberRig = grabberSo.FindProperty("handRig").objectReferenceValue;
            var sameObject = controller != null && grabber.gameObject == controller.gameObject;
            check(sameObject && grabberLeft == isLeft && grabberRig == rig, $"{label}.grabber",
                $"'{grabber.name}' (no controller {(sameObject ? "sim" : "NAO")}, isLeftHand={grabberLeft}, handRig {(grabberRig == rig ? "conectado" : "NAO conectado")})");
        }

        var upperArm = Field("upperArm");
        var forearm = Field("forearm");
        var forearmEnd = Field("forearmEnd");
        var hand = Field("hand");
        var chainOk = Named(upperArm, $"upper_arm.{s}") && upperArm.parent != null && upperArm.parent.name == $"deltoid.{s}"
            && Named(forearm, $"forearm.{s}") && forearm.parent == upperArm
            && Named(forearmEnd, $"forearm.{s}_end") && forearmEnd.parent == forearm
            && Named(hand, $"hand.{s}") && hand.parent != null && hand.parent.name == $"forearm.{s}.001";
        check(chainOk, $"{label}: cadeia do braco",
            $"{Name(upperArm)} -> {Name(forearm)} -> {Name(forearmEnd)} | {Name(hand != null ? hand.parent : null)} -> {Name(hand)}");

        if (!chainOk)
            return null;

        // Which bones move the skin: the arm's two segments and the hand.
        var deforming = skinBones.Contains(upperArm) && skinBones.Contains(forearm) && skinBones.Contains(hand);
        check(deforming, $"{label}: bones que deformam a malha",
            deforming ? "upper_arm, forearm e hand estao no SkinnedMeshRenderer" : "algum dos bones do braco NAO esta no SkinnedMeshRenderer - mover ele nao move a pele");

        var upperLength = Vector3.Distance(upperArm.position, forearm.position);
        var foreLength = Vector3.Distance(forearm.position, forearmEnd.position);
        check(upperLength > 0.05f && foreLength > 0.05f, $"{label}: comprimentos", $"braco {upperLength:F3}m, antebraco {foreLength:F3}m");

        if (!Application.isPlaying)
        {
            var gap = Vector3.Distance(forearmEnd.position, hand.position);
            check(gap < MaxWristGapMeters, $"{label}: punho continuo", $"forearm_end -> hand {gap * 1000f:F1}mm (pose de bind)");
        }

        var pose = so.FindProperty($"{arm}.pose")?.objectReferenceValue as HandBonePoseController;
        check(pose != null && pose.transform == hand, $"{label}.pose",
            pose == null ? "NULO" : pose.transform == hand ? $"HandBonePoseController em {hand.name}" : $"em '{pose.name}', NAO em {hand.name}");

        if (pose != null && pose.transform == hand)
            ValidatePose(pose, hand, isLeft, grabber, label, check, info);

        if (Application.isPlaying)
            check(rig.IsReady(isLeft), $"{label}: IK ativo", rig.IsReady(isLeft) ? "sim (HandRig mediu o braco no Start)" : "NAO - ver erro do HandRig no Console");

        return hand;
    }

    static void ValidatePose(HandBonePoseController pose, Transform hand, bool isLeft, VRGrabber grabber, string label,
        System.Action<bool, string, string> check, System.Action<string, string> info)
    {
        var s = isLeft ? "L" : "R";
        var so = new SerializedObject(pose);

        var poseLeft = so.FindProperty("isLeftHand").boolValue;
        var poseGrabber = so.FindProperty("grabber").objectReferenceValue;
        check(poseLeft == isLeft && poseGrabber == grabber, $"{label}.pose: lado e grabber",
            $"isLeftHand={poseLeft}, grabber {(poseGrabber == grabber && grabber != null ? "o mesmo do braco" : poseGrabber == null ? "NULO" : "DIFERENTE do braco")}");

        // Every finger along its own chain under this hand, tip included.
        var fingers = new FingerBones[5];
        var tips = new Transform[5];
        var tipsProperty = so.FindProperty("tips");
        var problems = new List<string>();
        for (var f = 0; f < 5; f++)
        {
            var prefix = f == 0 ? "thumb" : $"f_{FingerNames[f]}";
            var segment1 = so.FindProperty($"{FingerNames[f]}.segment1").objectReferenceValue as Transform;
            var segment2 = so.FindProperty($"{FingerNames[f]}.segment2").objectReferenceValue as Transform;
            var segment3 = so.FindProperty($"{FingerNames[f]}.segment3").objectReferenceValue as Transform;
            var tip = tipsProperty != null && f < tipsProperty.arraySize ? tipsProperty.GetArrayElementAtIndex(f).objectReferenceValue as Transform : null;
            fingers[f] = new FingerBones { segment1 = segment1, segment2 = segment2, segment3 = segment3 };
            tips[f] = tip;

            var ok = Named(segment1, $"{prefix}.01.{s}") && segment1.IsChildOf(hand)
                && Named(segment2, $"{prefix}.02.{s}") && segment2.parent == segment1
                && Named(segment3, $"{prefix}.03.{s}") && segment3.parent == segment2
                && Named(tip, $"{prefix}.03.{s}_end") && tip.parent == segment3;
            if (!ok)
                problems.Add($"{FingerNames[f]} ({Name(segment1)}/{Name(segment2)}/{Name(segment3)}/{Name(tip)})");
        }

        check(problems.Count == 0 && tipsProperty != null && tipsProperty.arraySize == 5, $"{label}.pose: dedos e pontas",
            problems.Count == 0 ? "15 segmentos + 5 pontas (_end), cada um filho do anterior, sob a mao certa" : $"errado: {string.Join("; ", problems)}");

        if (problems.Count > 0)
            return;

        if (Application.isPlaying)
        {
            check(pose.HasAnatomy, $"{label}.pose: anatomia", pose.HasAnatomy ? "medida no Awake" : "NAO medida - ver erro do HandBonePoseController no Console");
            return;
        }

        // The exact measurement HandBonePoseController.Awake makes.
        var measured = HandAnatomy.TryMeasure(hand, new[] { fingers[1], fingers[2], fingers[3], fingers[4] },
            new[] { tips[1], tips[2], tips[3], tips[4] }, isLeft, out var anatomy);
        check(measured, $"{label}.pose: anatomia (HandAnatomy.TryMeasure)", measured ? "medida a partir dos bones" : "FALHOU");
        if (!measured)
            return;

        // Handedness from the bones alone: palm normal x (pinky -> index)
        // points toward the fingers for a left hand, away for a right one.
        var chirality = Vector3.Dot(Vector3.Cross(anatomy.PalmFacing, anatomy.KnuckleAxis), anatomy.FingerForward);
        check(isLeft ? chirality > 0.5f : chirality < -0.5f, $"{label}.pose: mao {(isLeft ? "esquerda" : "direita")} de fato",
            $"quiralidade {chirality:F2} (esquerda > 0, direita < 0) - lados nao trocados");

        // OpenXR grip frame: fingers point along its -Y, the fist tube runs
        // along +Z from the little finger toward the thumb.
        var fingersAlongGripMinusY = Vector3.Dot(anatomy.GripRotation * Vector3.down, anatomy.FingerForward);
        var thumbSide = anatomy.GripRotation * Vector3.forward;
        var thumbOffset = Vector3.Dot(Quaternion.Inverse(hand.rotation) * (tips[0].position - hand.position) - anatomy.PalmReference, thumbSide);
        check(fingersAlongGripMinusY > 0.7f && thumbOffset > 0f, $"{label}.pose: frame de pegada (OpenXR grip)",
            $"dedos . -Y = {fingersAlongGripMinusY:F2} (>0.7), polegar do lado +Z ({thumbOffset * 1000f:F0}mm)");

        check(anatomy.HandLength > MinHandLengthMeters && anatomy.HandLength < MaxHandLengthMeters, $"{label}.pose: tamanho da mao",
            $"{anatomy.HandLength:F3}m pulso -> ponta do dedo medio (adulto: {MinHandLengthMeters:F2}-{MaxHandLengthMeters:F2}m)");

        // Power grip: the held cylinder crosses the palm from the hypothenar
        // to the index knuckle, oblique to the knuckle line.
        var obliquity = anatomy.GripObliquityDegrees;
        check(obliquity > 10f && obliquity < 35f, $"{label}.pose: eixo da pegada (diagonal da palma)",
            $"{obliquity:F1} graus da linha dos nos, a partir de {Name(fingers[4].segment1 != null ? fingers[4].segment1.parent : null)} (hipotenar) ate {Name(fingers[1].segment1)}");
    }

    static bool Named(Transform t, string name) => t != null && t.name == name;

    static string Name(Transform t) => t != null ? t.name : "NULO";
}
