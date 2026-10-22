using Freehand.Audio;
using Freehand.Spray;
using UnityEditor;
using UnityEngine;

// Sound polish: what the scene needs for the audio to answer the physics.
// Idempotent, like the rest of the builder: everything is found or added by type.
public static partial class V04SceneBuilder
{
    static void BuildAudio(GameObject sprayCan, GameObject room, SprayCanSfx sfx)
    {
        // The spray's levels moved with the new design; the scene carried the old ones.
        var manager = Object.FindFirstObjectByType<AudioManager>(FindObjectsInactive.Include);
        if (manager != null)
        {
            SetFloat(manager, "baseVolume", 0.95f);
            SetFloat(manager, "pitchVariation", 0.03f);
            SetFloat(manager, "minDistance", 1f);
            SetFloat(manager, "maxDistance", 12f);
            EditorUtility.SetDirty(manager);
        }

        var ambience = Object.FindFirstObjectByType<EnvironmentAmbience>(FindObjectsInactive.Include);
        if (ambience != null)
        {
            SetFloat(ambience, "volume", 0.7f);
            EditorUtility.SetDirty(ambience);
        }

        // The voice pool has no use for a source of its own.
        if (sfx != null)
        {
            var legacy = sfx.GetComponent<AudioSource>();
            if (legacy != null)
                Object.DestroyImmediate(legacy, true);
            EditorUtility.SetDirty(sfx);
        }

        GetOrAdd<CanMotionAudio>(sprayCan);
        var canImpact = GetOrAdd<ImpactAudio>(sprayCan);
        SetInt(canImpact, "kind", (int)ImpactAudio.Kind.Can);

        var caps = 0;
        foreach (var cap in Object.FindObjectsByType<PhysicalCap>(FindObjectsInactive.Include))
        {
            var impact = GetOrAdd<ImpactAudio>(cap.gameObject);
            SetInt(impact, "kind", (int)ImpactAudio.Kind.Cap);
            caps++;
        }

        var surfaces = AssignAudioSurfaces(room);
        log.AppendLine($"Audio: CanMotionAudio + ImpactAudio na lata, ImpactAudio em {caps} cap(s), {surfaces} superficie(s) sonoras (concreto/tijolo/metal/madeira/plastico).");
    }

    // What things are made of, for how a can rings on them and how paint grains on them.
    static int AssignAudioSurfaces(GameObject room)
    {
        if (room == null)
            return 0;

        var count = 0;
        foreach (var t in room.GetComponentsInChildren<Transform>(true))
        {
            SurfaceSound? kind = null;
            var paint = t.GetComponent<Freehand.Paint.PaintSurface>();
            if (paint != null)
            {
                // PaintSurface.materialKind: Flat, Concrete, Brick, Metal.
                var so = new SerializedObject(paint);
                var material = so.FindProperty("materialKind");
                var index = material != null ? material.enumValueIndex : 0;
                kind = index == 2 ? SurfaceSound.Brick : index == 3 ? SurfaceSound.Metal : SurfaceSound.Concrete;
            }
            else
            {
                switch (t.name)
                {
                    case "Floor":
                    case "Ceiling":
                    case "WallBack":
                        kind = SurfaceSound.Concrete;
                        break;
                    case "TrashBin":
                    case "MetalDoor":
                    case "ToolTable":
                    case "ColorPalette":
                    case "CapRack":
                    case "Pipes":
                        kind = SurfaceSound.Metal;
                        break;
                    case "Pallet":
                        kind = SurfaceSound.Wood;
                        break;
                    case "Crates":
                        kind = SurfaceSound.Plastic;
                        break;
                }
            }

            if (kind == null)
                continue;

            var surface = t.GetComponent<AudioSurface>() ?? t.gameObject.AddComponent<AudioSurface>();
            var surfaceSo = new SerializedObject(surface);
            surfaceSo.FindProperty("sound").enumValueIndex = (int)kind.Value;
            surfaceSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(surface);
            count++;
        }

        return count;
    }
}
