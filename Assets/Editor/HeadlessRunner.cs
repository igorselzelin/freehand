using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless entry points for routine validation and builds (no window, no focus):
//   Unity.exe -batchmode -nographics -projectPath <p> -logFile <log> -executeMethod HeadlessRunner.<Method>
// Always started by tools/headless.ps1, which first checks that no other Unity process or lock holds the project.
// Results are written to Logs/headless/<name>.txt; the process exit code is 0 only if every check passed.
public static class HeadlessRunner
{
    static readonly string OutDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "headless");
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    // A batchmode Editor starts on an empty scene: everything here works on the game's scene.
    static void OpenGameScene() => EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

    // ---- scene builder + validators (edit mode, no play) ----
    public static void BuildAndValidate()
    {
        Directory.CreateDirectory(OutDir);
        var sb = new StringBuilder();
        var failed = 0;
        try
        {
            OpenGameScene();
            V04SceneBuilder.Build();
            var log = Path.Combine(Directory.GetCurrentDirectory(), "V04SceneBuilder.log");
            foreach (var line in File.ReadAllLines(log))
            {
                if (line.Contains("FALHA") || line.Contains("ERRO"))
                {
                    failed++;
                    sb.AppendLine(line.Trim());
                }
            }

            var can = GameObject.Find("SprayCan");
            foreach (var r in CapSystemValidator.Validate(can))
            {
                if (!r.Ok && !r.IsInfo)
                {
                    failed++;
                    sb.AppendLine("FALHA " + r.Label + ": " + r.Detail);
                }
            }
        }
        catch (Exception e)
        {
            failed++;
            sb.AppendLine("EXCEPTION " + e);
        }

        sb.AppendLine(failed == 0 ? "BUILD/VALIDATE OK" : $"{failed} problema(s)");
        File.WriteAllText(Path.Combine(OutDir, "build_validate.txt"), sb.ToString());
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    // ---- Android player build ----
    public static void BuildApk()
    {
        Directory.CreateDirectory(OutDir);
        var path = CommandLineValue("-apkPath") ?? "Builds/freehand-quest-headless.apk";
        if (File.Exists(path))
        {
            // Never overwrite a release APK by accident.
            File.WriteAllText(Path.Combine(OutDir, "build_apk.txt"), $"RECUSADO: {path} ja existe");
            EditorApplication.Exit(3);
            return;
        }

        // -releaseVersion 0.14.0 : the release version (not -version: that is the Unity editor flag that prints its version and quits); the Android version code goes up by one (the project's convention).
        var version = CommandLineValue("-releaseVersion");
        if (!string.IsNullOrEmpty(version) && version != PlayerSettings.bundleVersion)
        {
            PlayerSettings.bundleVersion = version;
            PlayerSettings.Android.bundleVersionCode += 1;
            AssetDatabase.SaveAssets();
            // A batchmode Editor does not write PlayerSettings back to disk (the active build profile keeps the old
            // version): the build gets the new one, but the files must be updated too, or the next build reuses
            // the version code. Logged so it is never silent.
            // A batchmode Editor does not write PlayerSettings back to disk: the files are updated here, so the
            // next build starts from this version (and never reuses its version code).
            PersistVersion(PlayerSettings.bundleVersion, PlayerSettings.Android.bundleVersionCode);
        }

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = path,
            target = BuildTarget.Android,
            // -development: a diagnostic build (debuggable, full engine logging) - never a release.
            options = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-development") >= 0 ? BuildOptions.Development : BuildOptions.None,
        });
        File.WriteAllText(Path.Combine(OutDir, "build_apk.txt"), $"{report.summary.result} {report.summary.totalSize / (1024f * 1024f):F1} MB errors {report.summary.totalErrors} version {PlayerSettings.bundleVersion} code {PlayerSettings.Android.bundleVersionCode} (build profile on disk: {PersistedVersion()}) -> {path}");
        EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }

    // The version lines of the active Meta Quest build profile and of ProjectSettings, rewritten in place.
    static void PersistVersion(string version, int code)
    {
        var root = Directory.GetCurrentDirectory();
        var files = new[]
        {
            (Path.Combine(root, "Assets/Settings/Build Profiles/Meta Quest.asset"), @"(\|   bundleVersion: )[^']*", @"(\|   AndroidBundleVersionCode: )\d+"),
            (Path.Combine(root, "ProjectSettings/ProjectSettings.asset"), @"(?m)(^  bundleVersion: )[^\r\n]*", @"(?m)(^  AndroidBundleVersionCode: )\d+"),
        };

        foreach (var (path, versionPattern, codePattern) in files)
        {
            if (!File.Exists(path))
                continue;
            var text = File.ReadAllText(path);
            text = System.Text.RegularExpressions.Regex.Replace(text, versionPattern, "${1}" + version);
            text = System.Text.RegularExpressions.Regex.Replace(text, codePattern, "${1}" + code);
            File.WriteAllText(path, text);
        }
    }

    static string PersistedVersion()
    {
        var text = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/Settings/Build Profiles/Meta Quest.asset"));
        var v = System.Text.RegularExpressions.Regex.Match(text, @"\|   bundleVersion: ([^']*)");
        var c = System.Text.RegularExpressions.Regex.Match(text, @"\|   AndroidBundleVersionCode: (\d+)");
        return $"{(v.Success ? v.Groups[1].Value : "?")} / {(c.Success ? c.Groups[1].Value : "?")}";
    }

    // ---- play-mode tests: the same coroutines the bridge drives, stepped here (no window needed) ----
    // -tests Name1,Name2   (static IEnumerator Name(StringBuilder) in FreehandPlayTests, or any class given as Class.Name)
    // Tests that render to a screenshot need a GPU and are skipped by name: they belong to the "needs a graphical session" group.
    static IEnumerator current;
    static StringBuilder testLog;
    static readonly Queue<string> pending = new Queue<string>();
    static string running;
    static double waitUntil;
    static Func<bool> waitCondition;
    static int failures;
    static StringBuilder all;
    static readonly Stack<IEnumerator> nested = new Stack<IEnumerator>();

    public static void RunTests()
    {
        Directory.CreateDirectory(OutDir);
        all = new StringBuilder();
        foreach (var name in (CommandLineValue("-tests") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            pending.Enqueue(name.Trim());
        if (pending.Count == 0)
        {
            File.WriteAllText(Path.Combine(OutDir, "tests.txt"), "nenhum teste pedido (-tests A,B,...)");
            EditorApplication.Exit(1);
            return;
        }

        OpenGameScene();
        deadline = EditorApplication.timeSinceStartup + 60 * 30;
        EditorApplication.update += Tick;
        startNext = true;
    }

    static bool startNext;
    static double deadline;
    static double testDeadline;

    // Starts the next test only once Play Mode is fully left (entering while still exiting is ignored).
    static void Next()
    {
        if (pending.Count == 0)
        {
            File.WriteAllText(Path.Combine(OutDir, "tests.txt"), all.ToString() + (failures == 0 ? "TODOS OK" : $"{failures} teste(s) com falha"));
            EditorApplication.update -= Tick;
            EditorApplication.Exit(failures == 0 ? 0 : 1);
            return;
        }

        running = pending.Dequeue();
        testDeadline = EditorApplication.timeSinceStartup + 300;
        testLog = new StringBuilder();
        current = Wrap(running);
        waitUntil = 0;
        waitCondition = null;
        nested.Clear();
        EditorApplication.EnterPlaymode();
    }

    static IEnumerator Wrap(string name)
    {
        while (!EditorApplication.isPlaying || Time.frameCount < 5)
            yield return null;
        var method = typeof(FreehandPlayTests).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        if (method == null)
        {
            testLog.AppendLine("  FALHA teste nao encontrado: " + name);
            yield break;
        }

        var routine = (IEnumerator)method.Invoke(null, new object[] { testLog });
        while (routine.MoveNext())
            yield return routine.Current;
    }

    static void Tick()
    {
        if (EditorApplication.timeSinceStartup > deadline)
        {
            all.AppendLine("TIMEOUT GERAL");
            failures++;
            pending.Clear();
            current = null;
            startNext = false;
            if (!EditorApplication.isPlaying)
                Next();
            else
                EditorApplication.ExitPlaymode();
            return;
        }

        if (current == null)
        {
            if (startNext && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                startNext = false;
                Next();
            }

            return;
        }

        if (EditorApplication.timeSinceStartup > testDeadline)
        {
            testLog.AppendLine("  FALHA TIMEOUT do teste");
            Finish();
            return;
        }

        try
        {
            if (waitCondition != null)
            {
                if (!waitCondition())
                    return;
                waitCondition = null;
            }

            if (EditorApplication.timeSinceStartup < waitUntil)
                return;

            while (nested.Count > 0 && !nested.Peek().MoveNext())
                nested.Pop();
            if (nested.Count == 0 && !current.MoveNext())
            {
                Finish();
                return;
            }

            var value = nested.Count > 0 ? nested.Peek().Current : current.Current;
            if (value is IEnumerator inner)
            {
                nested.Push(inner);
                return;
            }

            if (value is float seconds)
                waitUntil = EditorApplication.timeSinceStartup + seconds;
            else if (value is Func<bool> condition)
                waitCondition = condition;
        }
        catch (Exception e)
        {
            testLog.AppendLine("EXCEPTION " + e);
            Finish();
        }
    }

    static void Finish()
    {
        current = null;
        var text = testLog.ToString();
        if (text.Contains("FALHA") || text.Contains("EXCEPTION"))
            failures++;
        all.AppendLine("=== " + running + " ===");
        all.Append(text);
        EditorApplication.ExitPlaymode();
        startNext = true;
    }

    static string CommandLineValue(string key)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key)
                return args[i + 1];
        }

        return null;
    }
}
