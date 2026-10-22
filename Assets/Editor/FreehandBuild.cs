using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// One-shot build, run manually via the menu item below. Does not touch any
// scene, script, material or setting - only reads the already-active
// Android/Meta Quest player settings (confirmed active in this Editor
// session) and calls the standard BuildPipeline.BuildPlayer.
public static class FreehandBuild
{
    const string OutputPath = "Builds/freehand-quest-v0.3-models.apk";

    [MenuItem("Freehand/Build Quest APK (v0.3 models, Run Once)")]
    public static void Build()
    {
        var log = new StringBuilder();

        log.AppendLine("Active build target: " + EditorUserBuildSettings.activeBuildTarget);
        log.AppendLine("PlayerSettings.applicationIdentifier (Android): " + PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android));
        log.AppendLine("Scenes in EditorBuildSettings:");
        foreach (var s in EditorBuildSettings.scenes)
            log.AppendLine("  " + s.path + " enabled=" + s.enabled);

        string fullOutDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds");
        Directory.CreateDirectory(fullOutDir);

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = OutputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        log.AppendLine();
        log.AppendLine("Result: " + summary.result);
        log.AppendLine("TotalSize (bytes): " + summary.totalSize);
        log.AppendLine("TotalTime: " + summary.totalTime);
        log.AppendLine("TotalErrors: " + summary.totalErrors);
        log.AppendLine("TotalWarnings: " + summary.totalWarnings);
        log.AppendLine("OutputPath: " + summary.outputPath);

        foreach (var step in report.steps)
        {
            foreach (var msg in step.messages)
            {
                if (msg.type == LogType.Error || msg.type == LogType.Exception || msg.type == LogType.Warning)
                    log.AppendLine("[" + msg.type + "][" + step.name + "] " + msg.content);
            }
        }

        string outPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "FreehandBuild.log");
        File.WriteAllText(outPath, log.ToString());
        Debug.Log("[FreehandBuild] Result=" + summary.result + " size=" + summary.totalSize + " bytes. Log: " + outPath);
    }
}
