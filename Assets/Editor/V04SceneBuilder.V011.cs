using Freehand.Interaction;
using Freehand.Spray;
using Freehand.VR;
using UnityEditor;
using UnityEngine;

// V0.11: item inspection - the can and the caps can be shown off in the hand.
// Idempotent: components are found or added; caps are rebuilt by the builder
// every run, so theirs are added after.
public static partial class V04SceneBuilder
{
    static void BuildV011(GameObject sprayCan, GameObject left, GameObject right, InputActionAssetRef inputActions)
    {
        var canInspection = GetOrAdd<ItemInspection>(sprayCan);
        SetInt(canInspection, "kind", (int)ItemInspection.Kind.SprayCan);
        EditorUtility.SetDirty(canInspection);

        // V0.14: the can's freestyle (runs of in-hand moves, tosses, passes). Added here, before the workshop
        // copies the can (V0.12), so both cans have it.
        var freestyle = GetOrAdd<CanFreestyle>(sprayCan);
        SetField(freestyle, "leftGrabber", left != null ? left.GetComponent<VRGrabber>() : null);
        SetField(freestyle, "rightGrabber", right != null ? right.GetComponent<VRGrabber>() : null);
        EditorUtility.SetDirty(freestyle);

        var caps = 0;
        foreach (var cap in Object.FindObjectsByType<PhysicalCap>(FindObjectsInactive.Include))
        {
            var inspection = GetOrAdd<ItemInspection>(cap.gameObject);
            SetInt(inspection, "kind", (int)ItemInspection.Kind.Cap);
            EditorUtility.SetDirty(inspection);
            caps++;
        }

        // One reader for both hands, next to the other interaction roots.
        var holder = GameObject.Find("Interaction") ?? new GameObject("Interaction");
        var input = GetOrAdd<InspectionInput>(holder);
        SetField(input, "inputActions", inputActions.Asset);
        SetField(input, "leftGrabber", left != null ? left.GetComponent<VRGrabber>() : null);
        SetField(input, "rightGrabber", right != null ? right.GetComponent<VRGrabber>() : null);
        EditorUtility.SetDirty(input);

        log.AppendLine($"V0.11 Inspecao: ItemInspection na lata (Flip, CapCheck, ShakeTest, Fidget) e em {caps} cap(s) (Skinny/Standard/Fat); InspectionInput: A/X da mao que segura o objeto (a mao vazia continua abrindo o Color Orb).");
    }
}
