using Basis.Scripts.BasisSdk;
using Basis.Social.Editor;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialPlaytestSceneTests
    {
        private Scene scene;
        private BasisScene basis;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            var owner = new GameObject("playtest scene test");
            SceneManager.MoveGameObjectToScene(owner, scene);
            basis = owner.AddComponent<BasisScene>();
        }

        [TearDown]
        public void TearDown()
        {
            if (!scene.IsValid()) return;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
                    if (renderer.sharedMaterial != null && renderer.sharedMaterial.name.StartsWith("Playtest "))
                        Object.DestroyImmediate(renderer.sharedMaterial);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        [Test]
        public void OrdinarySceneProcessingDoesNotChangeScene()
        {
            new BasisSocialPlaytestScene().OnProcessScene(scene, null);
            Assert.That(scene.rootCount, Is.EqualTo(1));
            Assert.That(basis.SpawnPoint, Is.Null);
        }

        [Test]
        public void StageHasVisibleSolidFloorAndDeterministicSpawn()
        {
            BasisSocialPlaytestScene.Decorate(scene);
            Assert.That(scene.rootCount, Is.EqualTo(2));
            Assert.That(basis.SpawnPoint.position, Is.EqualTo(new Vector3(-3, 0.05f, -5)));
            int floors = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
                {
                    Assert.That(renderer.sharedMaterial, Is.Not.Null);
                    Assert.That(renderer.gameObject.scene, Is.EqualTo(scene));
                    Assert.That(renderer.GetComponent<BoxCollider>(), Is.Not.Null);
                    if (renderer.name.StartsWith("Floor ")) floors++;
                }
            Assert.That(floors, Is.EqualTo(36));
        }

        [Test]
        public void CameraSettingsAreReadyForBasisSceneFactory()
        {
            BasisSocialPlaytestScene.Decorate(scene);
            Assert.That(basis.MainCamera, Is.Not.Null);
            Assert.That(basis.MainCamera.enabled, Is.False);
            Assert.That(basis.MainCamera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
            Assert.That(basis.MainCamera.farClipPlane, Is.GreaterThan(24));
        }

        [Test]
        public void DuplicateDecorationIsRejected()
        {
            BasisSocialPlaytestScene.Decorate(scene);
            Assert.Throws<System.InvalidOperationException>(() => BasisSocialPlaytestScene.Decorate(scene));
        }
    }
}
