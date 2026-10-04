using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Basis.Social.Editor
{
    /// <summary>Repeatable desktop playtest builds; no endpoint or credential is embedded.</summary>
    public static class BasisSocialPlaytestBuild
    {
        internal const string DemoScenePath = "Packages/com.basis.examples/Scenes/DemoScene.unity";
        internal static bool IsBuilding { get; private set; }

        public static void Build()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before building.");
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            bool windows = target == BuildTarget.StandaloneWindows64;
            if (!windows && target != BuildTarget.StandaloneOSX)
                throw new InvalidOperationException("Only macOS and Windows x64 playtest builds are supported.");
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-buildOutput");
            if (index < 0 || index + 1 >= args.Length || !Path.IsPathRooted(args[index + 1]))
                throw new InvalidOperationException("Pass an absolute -buildOutput path.");
            string output = Path.GetFullPath(args[index + 1]);
            if (!output.EndsWith(windows ? ".exe" : ".app", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output extension does not match the target.");
            if (File.Exists(output) || Directory.Exists(output))
                throw new InvalidOperationException("Use a fresh output path; existing builds are preserved.");
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new InvalidOperationException("No enabled startup scene.");
            // Include an upstream playable example only in these explicit playtest builds.
            // No prefab, addressable catalog or saved build-settings asset is modified.
            const string demoScene = DemoScenePath;
            if (!File.Exists(demoScene)) throw new InvalidOperationException("Basis example world is missing.");
            if (!scenes.Contains(demoScene)) scenes = scenes.Append(demoScene).ToArray();

            var namedTarget = NamedBuildTarget.Standalone;
            string product = PlayerSettings.productName;
            string identifier = PlayerSettings.GetApplicationIdentifier(namedTarget);
            string version = PlayerSettings.bundleVersion;
            var backend = PlayerSettings.GetScriptingBackend(namedTarget);
            int architecture = PlayerSettings.GetArchitecture(namedTarget);
            var preference = BasisBuildScriptingBackendPreference.Current;
            var subtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
            try
            {
                PlayerSettings.productName = "Basis Social Playtest";
                PlayerSettings.SetApplicationIdentifier(namedTarget, "org.basisvr.social.playtest");
                PlayerSettings.SetScriptingBackend(namedTarget, windows ? ScriptingImplementation.Mono2x : ScriptingImplementation.IL2CPP);
                BasisBuildScriptingBackendPreference.Current = windows
                    ? BasisBuildScriptingBackendPreference.Mode.Mono
                    : BasisBuildScriptingBackendPreference.Mode.IL2CPP;
                if (!windows) PlayerSettings.SetArchitecture(namedTarget, 2); // Universal x64 + Apple Silicon.
                EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                IsBuilding = true;
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes, locationPathName = output, target = target,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = BuildOptions.Development
                });
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "build-result.json"), JsonUtility.ToJson(new Result
                {
                    result = report.summary.result.ToString(), platform = target.ToString(),
                    backend = windows ? "Mono" : "IL2CPP", errors = report.summary.totalErrors,
                    warnings = report.summary.totalWarnings, bytes = report.summary.totalSize
                }, true));
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Desktop playtest build failed; inspect its build log.");
            }
            finally
            {
                IsBuilding = false;
                PlayerSettings.productName = product;
                PlayerSettings.SetApplicationIdentifier(namedTarget, identifier);
                PlayerSettings.bundleVersion = version;
                PlayerSettings.SetScriptingBackend(namedTarget, backend);
                PlayerSettings.SetArchitecture(namedTarget, architecture);
                BasisBuildScriptingBackendPreference.Current = preference;
                EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
                AssetDatabase.SaveAssets();
            }
        }

        [Serializable] private sealed class Result
        {
            public string result, platform, backend;
            public int errors, warnings;
            public ulong bytes;
        }
    }
}
