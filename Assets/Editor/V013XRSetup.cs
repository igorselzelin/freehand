#if FREEHAND_MR
using System.Text;
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Meta;

/// <summary>
/// V0.13: the OpenXR features the MR foundation needs, switched on in the
/// Android OpenXR settings - and nothing else. Idempotent; run it from the
/// menu (or through V04SceneBuilder) after a package or settings change.
///
///   Meta Quest: Session            AR Foundation session on Quest
///   Meta Quest: Camera (Passthrough)   the passthrough layer (camera image support OFF:
///                                  no camera-frame access, so no CAMERA permission)
///   Meta Quest: Planes             walls and other surfaces from the Quest's Space Setup
///
/// Anchors, bounding boxes, meshing, raycasts and occlusion stay off: they
/// add permissions, manifest entries and per-frame cost that this version
/// does not use. Passthrough needs Android API 32 or newer (the package
/// rejects the build otherwise); every Quest runs Android 12L or later.
/// </summary>
public static class V013XRSetup
{
    [MenuItem("Freehand/V0.13/Configure MR OpenXR Features")]
    static void Run() => Debug.Log(Configure());

    public static string Configure()
    {
        var log = new StringBuilder();
        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);

        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
            return "ERRO: sem OpenXRSettings para Android.";

        Enable<ARSessionFeature>(settings, log, "Session");
        var camera = Enable<ARCameraFeature>(settings, log, "Camera (Passthrough)");
        if (camera != null && camera.cameraImageSupportEnabled)
        {
            camera.cameraImageSupportEnabled = false;
            EditorUtility.SetDirty(camera);
            log.AppendLine("  camera image support desligado");
        }

        Enable<ARPlaneFeature>(settings, log, "Planes");

        // Passthrough is composited by the alpha of the eye buffer: no HDR colour buffer (R11G11B10 has no
        // alpha) and no post-processing (it would write opaque alpha) while it is on. The URP asset is left
        // as it was - the virtual room keeps its HDR and post effects; MRModeController switches both off
        // on the camera only while mixed reality is on.

        // The CPU/GPU frame times the engine measures, for the [MR-PERF] log line.
        if (!PlayerSettings.enableFrameTimingStats)
        {
            PlayerSettings.enableFrameTimingStats = true;
            log.AppendLine("  Frame Timing Stats ligado (medicao de CPU/GPU no logcat)");
        }

        if (PlayerSettings.Android.minSdkVersion < AndroidSdkVersions.AndroidApiLevel32)
        {
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            log.AppendLine("  minSdkVersion -> 32");
        }

        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();

        // PlayerSettings reach ProjectSettings.asset only through Save Project (SaveAssets leaves them in memory).
        EditorApplication.ExecuteMenuItem("File/Save Project");
        log.AppendLine($"minSdk={PlayerSettings.Android.minSdkVersion}");
        return log.ToString();
    }

    static T Enable<T>(OpenXRSettings settings, StringBuilder log, string label) where T : OpenXRFeature
    {
        var feature = settings.GetFeature<T>();
        if (feature == null)
        {
            log.AppendLine($"  FALHA: feature {label} nao encontrada");
            return null;
        }

        if (!feature.enabled)
        {
            feature.enabled = true;
            EditorUtility.SetDirty(feature);
            log.AppendLine($"  {label}: ligada");
        }
        else
        {
            log.AppendLine($"  {label}: ja ligada");
        }

        return feature;
    }
}
#else
using UnityEditor;

/// <summary>
/// V0.15.1: built without the MR packages (FREEHAND_MR off) - there are no Meta OpenXR features to switch on.
/// The startup settings the MR work had changed are put back as they were before it, so the build starts the way
/// V0.12 did: Frame Timing Stats off (it was only for the [MR-PERF] log line). minSdk stays 32 (harmless).
/// </summary>
public static class V013XRSetup
{
    public static string Configure()
    {
        if (PlayerSettings.enableFrameTimingStats)
            PlayerSettings.enableFrameTimingStats = false;
        return "V0.15.1: sem pacotes de MR - nenhuma feature Meta OpenXR; Frame Timing Stats desligado.";
    }
}
#endif
