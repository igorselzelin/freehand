using System.Collections.Generic;
using System.Text;
using Freehand.Spray;
using Freehand.Tools;
using Freehand.VR;
using UnityEditor;
using UnityEngine;

// Geometric/structural checks for the physical cap system and the spray
// origin. Everything here is measured on the actual scene objects (world
// poses, mesh triangles), not read back from the builder's own intentions -
// so it also catches a scene edited by hand after the build.
//
// Used by V04SceneBuilder.ValidateScene and runnable on its own from the
// menu at any time (Edit Mode or Play Mode).
public static class CapSystemValidator
{
    public readonly struct Result
    {
        public readonly bool Ok;
        public readonly bool IsInfo;
        public readonly string Label;
        public readonly string Detail;

        public Result(bool ok, bool isInfo, string label, string detail)
        {
            Ok = ok;
            IsInfo = isInfo;
            Label = label;
            Detail = detail;
        }
    }

    const float MaxOriginMismatchMeters = 0.001f;
    const float MaxOrificeSurfaceDistanceMeters = 0.002f;

    [MenuItem("Freehand/V0.8/2 - Validate Caps + Spray Origin")]
    static void RunFromMenu()
    {
        var sprayCan = GameObject.Find("SprayCan");
        if (sprayCan == null)
        {
            Debug.LogError("[CapSystemValidator] SprayCan nao encontrada na cena aberta.");
            return;
        }

        var report = new StringBuilder("[CapSystemValidator]\n");
        var failures = 0;
        foreach (var r in Validate(sprayCan))
        {
            var tag = r.IsInfo ? "INFO " : (r.Ok ? "OK   " : "FALHA");
            report.AppendLine($"  {tag} {r.Label}: {r.Detail}");
            if (!r.Ok && !r.IsInfo)
                failures++;
        }

        report.Insert(0, failures == 0 ? "Sem falhas.\n" : $"{failures} falha(s).\n");
        if (failures == 0)
            Debug.Log(report.ToString(), sprayCan);
        else
            Debug.LogWarning(report.ToString(), sprayCan);
    }

    public static List<Result> Validate(GameObject sprayCan)
    {
        var results = new List<Result>();
        void Check(bool ok, string label, string detail) => results.Add(new Result(ok, false, label, detail));
        void Info(string label, string detail) => results.Add(new Result(true, true, label, detail));

        var root = sprayCan.transform;
        Check(Approximately(root.localScale, Vector3.one), "Lata: escala da raiz",
            $"{root.localScale} (esperado 1,1,1 - todos os filhos em metros reais, sem cisalhar o cap)");

        var socketTransform = root.Find("CapSocket");
        var socket = socketTransform != null ? socketTransform.GetComponent<CapSocket>() : null;
        var pivot = root.Find("CapSocket/NozzlePivot");
        var origin = root.Find("CapSocket/NozzlePivot/SprayOrigin");
        var grabPoint = root.Find("GrabPoint");

        Check(socket != null && pivot != null && origin != null && grabPoint != null, "Hierarquia da lata",
            $"CapSocket{Mark(socket)} / NozzlePivot{Mark(pivot)} / SprayOrigin{Mark(origin)} / GrabPoint{Mark(grabPoint)}");
        if (socket == null || pivot == null || origin == null || grabPoint == null)
            return results;

        // Grip: GrabPoint is the fist's axis in the OpenXR grip convention
        // (see VRGrabInteractable) - +Z along the can's axis, -Y where the
        // fingers point, which must be where the spray leaves.
        var fistToCanAxis = Vector3.Angle(grabPoint.forward, root.up);
        Check(fistToCanAxis < 1f, "GrabPoint.forward == eixo da lata",
            $"{fistToCanAxis:F2} graus (o punho fecha em volta do corpo, nao ao lado dele)");

        var fingersToSpray = Vector3.Angle(-grabPoint.up, origin.forward);
        Check(fingersToSpray < 1f, "-GrabPoint.up == SprayOrigin.forward",
            $"{fingersToSpray:F2} graus (o spray sai para onde os dedos apontam)");

        var sprayToCanAxis = Vector3.Angle(origin.forward, root.up);
        Check(Mathf.Abs(sprayToCanAxis - 90f) < 1f, "Spray perpendicular ao eixo da lata",
            $"{sprayToCanAxis:F1} graus (esperado 90: sai de lado pelo orificio - 0 seria 'para cima', o bug historico)");

        Check(Quaternion.Angle(origin.localRotation, Quaternion.identity) < 0.1f, "SprayOrigin sem rotacao local",
            $"{origin.localRotation.eulerAngles} (forward = +Z do NozzlePivot = frente da lata)");

        // Particles must be born at the origin, pointing the same way.
        var emitter = sprayCan.GetComponent<SprayEmitter>();
        Check(emitter != null && emitter.SprayOrigin == origin, "SprayEmitter.sprayOrigin",
            emitter == null ? "SprayEmitter ausente" : $"aponta para '{(emitter.SprayOrigin != null ? emitter.SprayOrigin.name : "NULO")}'");

        if (emitter != null)
        {
            var particlesRoot = new SerializedObject(emitter).FindProperty("sprayParticlesRoot")?.objectReferenceValue as Transform;
            var offset = particlesRoot != null ? origin.InverseTransformPoint(particlesRoot.position).magnitude : float.MaxValue;
            var angle = particlesRoot != null ? Quaternion.Angle(origin.rotation, particlesRoot.rotation) : 180f;
            Check(particlesRoot != null && particlesRoot.IsChildOf(origin) && offset < 0.001f && angle < 0.5f,
                "Particulas nascem no SprayOrigin",
                particlesRoot == null ? "sprayParticlesRoot nao atribuido" : $"offset {offset * 1000f:F2}mm, rotacao {angle:F2} graus");

            var emitterCapManager = new SerializedObject(emitter).FindProperty("capManager")?.objectReferenceValue;
            Check(emitterCapManager != null, "SprayEmitter.capManager", emitterCapManager != null ? "conectado (sem cap = sem spray)" : "NULO");

            ValidateParticleMaterial(new SerializedObject(emitter).FindProperty("particleMaterialAsset")?.objectReferenceValue as Material, Check);
        }

        ValidateHold(sprayCan, pivot, grabPoint, socket, Check, Info);

        Check(sprayCan.GetComponent<CapManager>() != null, "CapManager", "na lata");

        var animator = sprayCan.GetComponent<NozzleAnimator>();
        if (animator != null)
        {
            var so = new SerializedObject(animator);
            Check(so.FindProperty("nozzlePivot")?.objectReferenceValue == pivot, "NozzleAnimator.nozzlePivot", "deve ser CapSocket/NozzlePivot");
            Check(so.FindProperty("capManager")?.objectReferenceValue != null, "NozzleAnimator.capManager", "conectado (sem cap = sem pressao)");
        }
        else
        {
            Check(false, "NozzleAnimator", "ausente");
        }

        // The cap seated at startup.
        var initialCap = new SerializedObject(socket).FindProperty("initialCap")?.objectReferenceValue as PhysicalCap;
        Check(initialCap != null, "Cap inicial no socket", initialCap != null ? initialCap.name : "NULO - a lata comecaria sem cap");

        if (initialCap != null)
        {
            var seated = initialCap.transform.parent == pivot
                && initialCap.transform.localPosition.magnitude < 0.0001f
                && Quaternion.Angle(initialCap.transform.localRotation, Quaternion.identity) < 0.1f;
            Check(seated, "Cap inicial encaixado", $"pai '{(initialCap.transform.parent != null ? initialCap.transform.parent.name : "-")}', pos {initialCap.transform.localPosition * 1000f}mm");

            if (initialCap.Orifice != null)
            {
                var mismatch = Vector3.Distance(initialCap.Orifice.position, origin.position);
                var mismatchAngle = Vector3.Angle(initialCap.Orifice.forward, origin.forward);
                Check(mismatch < MaxOriginMismatchMeters && mismatchAngle < 0.5f, "SprayOrigin no orificio do cap",
                    $"distancia {mismatch * 1000f:F2}mm, angulo {mismatchAngle:F2} graus");
            }

            var body = FindDeep(root.Find("Visual"), "sm_SprayBottle1");
            var bodyRenderer = body != null ? body.GetComponent<Renderer>() : null;
            if (bodyRenderer != null)
            {
                var above = origin.position.y - bodyRenderer.bounds.max.y;
                Check(above > 0f, "SprayOrigin fora do corpo da lata", $"{above * 1000f:F1}mm acima do topo do corpo");
            }
        }

        // Every physical cap.
        var caps = Object.FindObjectsByType<PhysicalCap>(FindObjectsInactive.Include);
        Info("Caps fisicos na cena", $"{caps.Length}");

        var rack = Object.FindAnyObjectByType<CapRack>(FindObjectsInactive.Include);
        var rackSlots = new HashSet<Transform>();
        if (rack != null)
        {
            var slotsProperty = new SerializedObject(rack).FindProperty("slots");
            for (var i = 0; slotsProperty != null && i < slotsProperty.arraySize; i++)
            {
                if (slotsProperty.GetArrayElementAtIndex(i).objectReferenceValue is Transform slot)
                    rackSlots.Add(slot);
            }
        }

        // Every can (V0.12: the workshop has its own) keeps one cap seated;
        // the rack must hold all the others, with a peg free for any of them.
        var cans = Object.FindObjectsByType<CapSocket>(FindObjectsInactive.Include).Length;
        var spare = caps.Length - cans + 1;
        Check(rack != null && rackSlots.Count >= spare, "CapRack fisico",
            rack == null ? "ausente" : $"{rackSlots.Count} pino(s) para {caps.Length} cap(s) em {cans} lata(s)");

        var slotUse = new Dictionary<Transform, int>();
        foreach (var cap in caps)
            ValidateCap(cap, pivot, rackSlots, slotUse, Check, Info);

        foreach (var pair in slotUse)
            Check(pair.Value <= 1, $"Pino '{pair.Key.name}'", $"{pair.Value} cap(s) - maximo 1");

        Check(sprayCan.GetComponent<SprayCanVisual>() == null, "SprayCanVisual (placeholder) removido", "nao pode sobrescrever o modelo real");

        return results;
    }

    // Must be the persistent Particles/Unlit asset built by
    // V04SceneBuilder.BuildSprayParticleMaterial: a material created at
    // runtime lost its transparent variant to build stripping and drew every
    // particle as an opaque square on Quest.
    static void ValidateParticleMaterial(Material material, System.Action<bool, string, string> check)
    {
        if (material == null)
        {
            check(false, "Particulas: material", "SprayEmitter.particleMaterialAsset NULO - particulas sem material no device");
            return;
        }

        var path = AssetDatabase.GetAssetPath(material);
        check(path.EndsWith(".mat"), "Particulas: material persistente", string.IsNullOrEmpty(path) ? "NAO e um asset (criado em runtime ou embutido na cena)" : path);

        var shaderName = material.shader != null ? material.shader.name : "NULO";
        check(shaderName == "Universal Render Pipeline/Particles/Unlit", "Particulas: shader", shaderName);

        var keyword = material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT");
        var renderType = material.GetTag("RenderType", false);
        check(keyword && renderType == "Transparent" && material.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent, "Particulas: transparencia",
            $"_SURFACE_TYPE_TRANSPARENT {(keyword ? "ligado" : "DESLIGADO")}, RenderType '{renderType}', fila {material.renderQueue}");

        var texture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
        var texturePath = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
        var importer = !string.IsNullOrEmpty(texturePath) ? AssetImporter.GetAtPath(texturePath) as TextureImporter : null;
        check(importer != null && importer.alphaSource != TextureImporterAlphaSource.None && importer.wrapMode == TextureWrapMode.Clamp, "Particulas: textura (_BaseMap)",
            texture == null ? "NULA - cada particula seria um quadrado cheio" : importer == null ? $"{texture.name} (nao e um asset importado)" : $"{texturePath}, alpha {importer.alphaSource}, wrap {importer.wrapMode}");

        var soft = material.HasProperty("_SoftParticlesEnabled") && material.GetFloat("_SoftParticlesEnabled") > 0f;
        check(!soft, "Particulas: soft particles", soft ? "LIGADO - exige depth texture (passe extra no Quest)" : "desligado");
    }

    // What the hand needs from the can: the radius the fist closes around,
    // where the index fingertip rests (the seated cap's top), and no
    // calibration tool able to rotate the can away from the derived pose.
    static void ValidateHold(GameObject sprayCan, Transform pivot, Transform grabPoint, CapSocket socket,
        System.Action<bool, string, string> check, System.Action<string, string> info)
    {
        var grab = sprayCan.GetComponent<VRGrabInteractable>();
        if (grab == null)
        {
            check(false, "VRGrabInteractable da lata", "ausente");
            return;
        }

        var so = new SerializedObject(grab);
        check(so.FindProperty("grabPoint").objectReferenceValue == grabPoint, "VRGrabInteractable.grabPoint", "deve ser SprayCan/GrabPoint");

        var attach = so.FindProperty("attachRotationEuler").vector3Value;
        check(attach == Vector3.zero, "VRGrabInteractable.attachRotationEuler", $"{attach} (esperado zero - a pose vem do GrabPoint)");

        var body = FindDeep(sprayCan.transform.Find("Visual"), "sm_SprayBottle1");
        var bodyRenderer = body != null ? body.GetComponent<Renderer>() : null;
        var bodyRadius = bodyRenderer != null ? Mathf.Max(bodyRenderer.bounds.extents.x, bodyRenderer.bounds.extents.z) : 0f;
        var radiusMatches = Application.isPlaying || bodyRenderer == null || Mathf.Abs(grab.GripRadius - bodyRadius) < 0.003f;
        check(grab.GripRadius > 0f && radiusMatches, "VRGrabInteractable.gripRadius",
            $"{grab.GripRadius * 1000f:F1}mm (raio do corpo {bodyRadius * 1000f:F1}mm) - os dedos fecham ate tocar esse cilindro");

        // What the fingers close around: the can's measured shape.
        var profile = grab.GripProfile;
        if (profile == null || profile.Length == 0)
        {
            check(false, "Perfil de pegada da lata", "vazio - os dedos fechariam em volta de um cilindro infinito");
        }
        else
        {
            var maxRadius = 0f;
            var top = float.MinValue;
            var ordered = true;
            for (var i = 0; i < profile.Length; i++)
            {
                maxRadius = Mathf.Max(maxRadius, profile[i].radius);
                top = Mathf.Max(top, profile[i].top);
                ordered &= profile[i].top > profile[i].bottom && profile[i].radius > 0f && (i == 0 || profile[i].bottom >= profile[i - 1].bottom);
            }

            var restHeight = so.FindProperty("fingerRest").objectReferenceValue is Transform rest
                ? Vector3.Dot(rest.position - grabPoint.position, grabPoint.forward) : float.NaN;
            var radiusOk = Application.isPlaying || Mathf.Abs(maxRadius - grab.GripRadius) < 0.002f;
            var topOk = float.IsNaN(restHeight) || Application.isPlaying || (restHeight <= top + 0.002f && restHeight > top - 0.03f);
            check(ordered && radiusOk && topOk, "Perfil de pegada da lata",
                $"{profile.Length} secoes, raio max {maxRadius * 1000f:F1}mm (gripRadius {grab.GripRadius * 1000f:F1}), topo {top * 1000f:F1}mm (apoio do indicador {restHeight * 1000f:F1}mm, no espaco do GrabPoint)");
        }

        var fingerRest = so.FindProperty("fingerRest").objectReferenceValue as Transform;
        var socketSo = new SerializedObject(socket);
        var socketRest = socketSo.FindProperty("fingerRest").objectReferenceValue as Transform;
        check(fingerRest != null && fingerRest.parent == pivot && socketRest == fingerRest, "IndexRest (apoio do indicador)",
            fingerRest == null ? "VRGrabInteractable.fingerRest NULO" : $"'{fingerRest.name}' sob NozzlePivot {(fingerRest.parent == pivot ? "sim" : "NAO")}, CapSocket.fingerRest {(socketRest == fingerRest ? "o mesmo" : "DIFERENTE ou NULO")}");

        if (fingerRest != null)
        {
            var initialCap = socketSo.FindProperty("initialCap")?.objectReferenceValue as PhysicalCap;
            if (initialCap != null && !Application.isPlaying)
            {
                var pad = initialCap.FingerPad;
                var offset = pad != null ? Vector3.Distance(fingerRest.position, pad.position) : float.MaxValue;
                check(offset < 0.001f, "IndexRest no FingerPad do cap", pad == null ? "cap sem FingerPad" : $"{offset * 1000f:F2}mm");
            }

            var below = Vector3.Dot(fingerRest.position - grabPoint.position, sprayCan.transform.up);
            info("Eixo do punho abaixo do apoio do indicador", $"{below * 1000f:F0}mm");
        }

        var calibrator = sprayCan.GetComponent<GripCalibrator>();
        check(calibrator == null || !calibrator.enabled, "GripCalibrator",
            calibrator == null ? "ausente" : calibrator.enabled ? "ATIVO - o botao Y giraria a lata para fora da pose derivada" : "presente, desativado");
    }

    static void ValidateCap(PhysicalCap cap, Transform pivot, HashSet<Transform> rackSlots, Dictionary<Transform, int> slotUse,
        System.Action<bool, string, string> check, System.Action<string, string> info)
    {
        var label = $"Cap '{cap.name}'";
        var parent = cap.transform.parent;
        // Seated on this can, or on another can's socket (the workshop's).
        var installed = parent == pivot || (parent != null && parent.GetComponentInParent<CapSocket>() != null && parent.name == pivot.name);
        var parked = parent != null && rackSlots.Contains(parent);
        check(installed || parked, $"{label}: posicao", installed ? "instalado na lata" : parked ? $"no pino '{parent.name}'" : $"SOLTO sob '{(parent != null ? parent.name : "raiz da cena")}'");

        if (parked)
            slotUse[parent] = slotUse.TryGetValue(parent, out var n) ? n + 1 : 1;

        var grab = cap.GetComponent<VRGrabInteractable>();
        var keepPose = grab != null && new SerializedObject(grab).FindProperty("keepWorldPoseOnGrab").boolValue;
        check(grab != null && keepPose, $"{label}: pegavel", grab == null ? "sem VRGrabInteractable" : keepPose ? "sim (segue a mao da pose atual)" : "keepWorldPoseOnGrab desligado - pularia para a mao");

        var body = cap.GetComponent<Rigidbody>();
        var collider = cap.GetComponent<Collider>();
        check(body != null && collider != null, $"{label}: fisica", $"Rigidbody{Mark(body)} Collider{Mark(collider)}");

        // Carried by its parent: an interpolated kinematic body writes stale
        // poses back and drifts off the nozzle / peg with every movement.
        if (body != null && (installed || parked))
            check(body.isKinematic && body.interpolation == RigidbodyInterpolation.None, $"{label}: preso ao pai sem interpolacao",
                $"kinematic={body.isKinematic}, interpolation={body.interpolation}");

        check(!string.IsNullOrEmpty(cap.Spray?.displayName), $"{label}: dados de spray", cap.Spray != null ? cap.Spray.displayName : "NULO");

        if (cap.Orifice == null)
        {
            check(false, $"{label}: orificio", "Transform 'orifice' nao atribuido");
            return;
        }

        var forwardInCap = cap.Orifice.localRotation * Vector3.forward;
        check(Vector3.Angle(forwardInCap, Vector3.forward) < 0.5f, $"{label}: orificio aponta +Z do cap", $"{Vector3.Angle(forwardInCap, Vector3.forward):F2} graus");

        // The finger scoop faces the hand, the orifice faces away from it:
        // on opposite sides of the actuator's axis.
        if (cap.FingerPad == null)
        {
            check(false, $"{label}: apoio do dedo", "FingerPad nao atribuido");
        }
        else
        {
            var pad = cap.FingerPad.localPosition;
            var hole = cap.Orifice.localPosition;
            var opposite = new Vector2(pad.x, pad.z).normalized.y < -0.5f && hole.z > 0f;
            check(opposite, $"{label}: apoio do dedo oposto ao orificio",
                $"FingerPad em ({pad.x * 1000f:F1}, {pad.y * 1000f:F1}, {pad.z * 1000f:F1})mm, orificio em z={hole.z * 1000f:F1}mm (cap: +Z = spray)");
        }

        var filter = cap.GetComponent<MeshFilter>();
        var mesh = filter != null ? filter.sharedMesh : null;
        if (mesh == null || !mesh.isReadable)
        {
            check(false, $"{label}: orificio na superficie", mesh == null ? "sem mesh" : "mesh nao legivel");
        }
        else
        {
            var local = cap.Orifice.localPosition;
            var distance = DistanceToMesh(mesh, local) * cap.transform.lossyScale.x;
            var outward = new Vector3(local.x - mesh.bounds.center.x, 0f, local.z - mesh.bounds.center.z);
            var pointsOut = Vector3.Dot(outward, forwardInCap) > 0f;
            check(distance < MaxOrificeSurfaceDistanceMeters && pointsOut, $"{label}: orificio na superficie",
                $"{distance * 1000f:F2}mm da malha, {(pointsOut ? "apontando para fora" : "APONTANDO PARA DENTRO")}");
        }

        if (cap.UsesPlaceholderModel)
            info($"{label}: modelo", "PLACEHOLDER (malha do Standard, tingida) - substituir pelo modelo real e reposicionar 'Orifice'");
    }

    static float DistanceToMesh(Mesh mesh, Vector3 point)
    {
        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        var best = float.MaxValue;
        for (var i = 0; i < triangles.Length; i += 3)
        {
            var closest = ClosestPointOnTriangle(point, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]);
            best = Mathf.Min(best, (closest - point).sqrMagnitude);
        }

        return Mathf.Sqrt(best);
    }

    // Ericson, Real-Time Collision Detection, 5.1.5.
    static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        var bp = p - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

        var va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        var denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    static bool Approximately(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 0.000001f;

    static string Mark(Object o) => o != null ? " OK" : " AUSENTE";

    static Transform FindDeep(Transform root, string name)
    {
        if (root == null)
            return null;
        if (root.name == name)
            return root;

        for (var i = 0; i < root.childCount; i++)
        {
            var found = FindDeep(root.GetChild(i), name);
            if (found != null)
                return found;
        }

        return null;
    }
}
