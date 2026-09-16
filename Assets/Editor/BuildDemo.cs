/*
 * Copyright (c) 2026 jdseo921. All rights reserved.
 * This software and associated documentation files are proprietary and confidential.
 * Unauthorized copying, modification, or distribution of this file is strictly prohibited.
 * Written by jdseo921, jdseo0921@gmail.com
 */

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Command-line entry point for producing the Windows build that ships as a
/// GitHub release. Unity has no generic CLI build, so a static method is the
/// only way to drive one headlessly:
///
///   Unity.exe -quit -batchmode -projectPath "&lt;project&gt;" \
///             -executeMethod BuildDemo.PerformBuild -logFile "&lt;log&gt;"
///
/// Unity Hub must be running or the licensing client cannot refresh its token
/// and the editor exits 198 before reaching this.
///
/// Output goes to Builds/windows/, which .gitignore already excludes.
/// </summary>
public static class BuildDemo
{
    private const string OutputDirectory = "Builds/windows";

    [MenuItem("Tools/CP5030/Build Windows Demo")]
    public static void PerformBuild()
    {
        string[] scenes = GetEnabledScenes();
        if (scenes.Length == 0)
        {
            Fail("No enabled scenes in the build settings; nothing to build.");
            return;
        }

        // The executable takes its name from productName, so the build is named
        // by the project rather than by whatever the template left behind.
        string executable = PlayerSettings.productName + ".exe";
        string path = Path.Combine(OutputDirectory, executable);
        Directory.CreateDirectory(OutputDirectory);

        Debug.Log("BuildDemo: building " + path + " from " + scenes.Length + " scene(s).");

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = path,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
        {
            Fail("Build " + summary.result + " with " + summary.totalErrors + " error(s).");
            return;
        }

        Debug.Log("BuildDemo: succeeded - " + path + ", " +
                  (summary.totalSize / (1024 * 1024)) + " MB, " +
                  summary.totalTime.TotalSeconds.ToString("F1") + "s.");

        if (Application.isBatchMode)
        {
            EditorApplication.Exit(0);
        }
    }

    private static string[] GetEnabledScenes()
    {
        EditorBuildSettingsScene[] all = EditorBuildSettings.scenes;
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].enabled) { count++; }
        }

        string[] enabled = new string[count];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].enabled) { enabled[next++] = all[i].path; }
        }
        return enabled;
    }

    private static void Fail(string message)
    {
        Debug.LogError("BuildDemo: " + message);
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(1);
        }
    }
}
#endif
