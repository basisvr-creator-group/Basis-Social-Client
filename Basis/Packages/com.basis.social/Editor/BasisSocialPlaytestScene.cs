using System;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Basis.Social.Editor
{
    /// <summary>Decorates the build-owned example scene, never the saved upstream scene.</summary>
    public sealed class BasisSocialPlaytestScene : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || !BasisSocialPlaytestBuild.IsBuilding ||
                scene.path != BasisSocialPlaytestBuild.DemoScenePath) return;
            Decorate(scene);
        }

        public static void Decorate(Scene scene)
        {
            BasisScene basisScene = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                basisScene = root.GetComponentInChildren<BasisScene>(true);
                if (basisScene != null) break;
            }
            if (basisScene == null) throw new InvalidOperationException("The playtest world needs a BasisScene.");
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "Social playtest stage") throw new InvalidOperationException("The stage was already added.");

            var template = AssetDatabase.LoadAssetAtPath<Material>("Packages/com.basis.examples/Materials/Walk material.mat");
            if (template == null) throw new InvalidOperationException("The upstream stage material is missing.");
            var dark = CreateMaterial(template, "Playtest slate", new Color(0.12f, 0.15f, 0.22f));
            var light = CreateMaterial(template, "Playtest grid", new Color(0.20f, 0.24f, 0.32f));
            var cyan = CreateMaterial(template, "Playtest cyan", new Color(0.15f, 0.70f, 0.82f));
            var purple = CreateMaterial(template, "Playtest purple", new Color(0.48f, 0.32f, 0.85f));
            var stage = new GameObject("Social playtest stage");
            SceneManager.MoveGameObjectToScene(stage, scene);
            for (int x = 0; x < 6; x++)
                for (int z = 0; z < 6; z++)
                    Cube(stage.transform, $"Floor {x},{z}", new Vector3(-10 + x * 4, -0.025f, -10 + z * 4),
                        new Vector3(4, 0.05f, 4), (x + z) % 2 == 0 ? dark : light);
            Cube(stage.transform, "North", new Vector3(0, 0.3f, 12), new Vector3(24, 0.6f, 0.2f), cyan);
            Cube(stage.transform, "South", new Vector3(0, 0.3f, -12), new Vector3(24, 0.6f, 0.2f), purple);
            Cube(stage.transform, "East", new Vector3(12, 0.3f, 0), new Vector3(0.2f, 0.6f, 24), cyan);
            Cube(stage.transform, "West", new Vector3(-12, 0.3f, 0), new Vector3(0.2f, 0.6f, 24), purple);
            Cube(stage.transform, "Cyan distance marker", new Vector3(5, 1, 6), new Vector3(0.4f, 2, 0.4f), cyan);
            Cube(stage.transform, "Purple distance marker", new Vector3(-5, 1, 6), new Vector3(0.4f, 2, 0.4f), purple);

            var spawn = new GameObject("Social playtest spawn");
            spawn.transform.SetParent(stage.transform, false);
            spawn.transform.localPosition = new Vector3(-3, 0.05f, -5);
            basisScene.SpawnPoint = spawn.transform;
            var cameraObject = new GameObject("Social playtest camera settings");
            cameraObject.transform.SetParent(stage.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.10f, 0.12f, 0.18f);
            camera.nearClipPlane = 0.02f;
            camera.farClipPlane = 500;
            basisScene.MainCamera = camera;
        }

        private static Material CreateMaterial(Material template, string name, Color color)
        {
            var material = new Material(template) { name = name };
            material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.15f);
            return material;
        }

        private static void Cube(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = scale;
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
        }
    }
}
