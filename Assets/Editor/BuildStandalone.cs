using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Tanks.Editor
{
    /// <summary>Headless standalone build for two-client localhost validation:
    /// Unity -batchmode -quit -executeMethod Tanks.Editor.BuildStandalone.Run
    /// The project has no hand-authored scenes (everything is code-driven from
    /// Bootstrap), so this creates an empty scene just to satisfy BuildPlayer.</summary>
    public static class BuildStandalone
    {
        public static void Run()
        {
            const string ScenePath = "Assets/Scenes/Empty.unity";
            if (!System.IO.File.Exists(ScenePath))
            {
                System.IO.Directory.CreateDirectory("Assets/Scenes");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/Tanks.app",
                target = BuildTarget.StandaloneOSX,
            };
            var result = BuildPipeline.BuildPlayer(options);
            Debug.Log($"BUILD RESULT: {result.summary.result} ({result.summary.totalErrors} errors)");
            EditorApplication.Exit(result.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
