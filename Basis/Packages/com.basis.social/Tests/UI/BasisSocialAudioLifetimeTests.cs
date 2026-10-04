#if STEAMAUDIO_ENABLED
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using SteamAudio;
using UnityEngine;

namespace Basis.Social.Tests
{
    // Real native handles: a freed source becomes zero, so these tests exercise
    // destruction ordering without dereferencing a dangling pointer/crashing Unity.
    public sealed class BasisSocialAudioLifetimeTests
    {
        GameObject owner;
        SteamAudioManager manager;
        SteamAudioManager previous;
        Context context;
        Simulator simulator;
        Source source;
        bool previousDirectPipeline;

        [SetUp]
        public void SetUp()
        {
            previous = SteamAudioManager.Singleton;
            previousDirectPipeline = SteamAudioManager.UseThreadedDirectPipeline;
            owner = new GameObject("Audio lifetime regression");
            manager = owner.AddComponent<SteamAudioManager>();
            SteamAudioManager.Singleton = manager;
            context = new Context();
            var settings = new SimulationSettings
            {
                flags = SimulationFlags.Direct,
                sceneType = SceneType.Default,
                maxNumOcclusionSamples = 1,
                samplingRate = 48000,
                frameSize = 1024,
                numThreads = 1
            };
            simulator = new Simulator(context, settings);
            source = new Source(simulator, settings);
            Set("mSimulator", simulator);
        }

        [TearDown]
        public void TearDown()
        {
            if (manager != null)
            {
                Set("mDirectInFlight", false);
                Set("mReflectionsInFlight", false);
                Invoke("DrainPendingSourceReleases");
                Invoke("DrainPendingProbeChanges");
            }
            source?.Release();
            simulator?.Release();
            context?.Release();
            SteamAudioManager.Singleton = previous;
            SteamAudioManager.UseThreadedDirectPipeline = previousDirectPipeline;
            if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
        }

        [TestCase("mDirectInFlight")]
        [TestCase("mReflectionsInFlight")]
        public void DestroyWaitsForEveryWorkerAndDiscardsPartiallyStagedPointers(string worker)
        {
            IntPtr handle = source.Get();
            var snapshot = new[] { handle, handle };
            Set("mReflSnapHandles", snapshot);
            Set("mReflSnapCount", 1); // Second entry belongs to an unfinished next slice.
            Set("mReflInputCursor", 2);
            Set("mReflInputsStaged", true);
            Set(worker, true);
            Assert.That(SteamAudioManager.TryDeferSourceRelease(source), Is.True);
            Invoke("DrainPendingSourceReleases");
            Assert.That(source.Get(), Is.EqualTo(handle), "An active worker still owns this native source.");
            Assert.That(snapshot[1], Is.EqualTo(handle));
            Set(worker, false);
            Invoke("DrainPendingSourceReleases");
            Assert.That(source.Get(), Is.EqualTo(IntPtr.Zero));
            Assert.That(snapshot, Is.All.EqualTo(IntPtr.Zero));
            Assert.That(Get<int>("mReflInputCursor"), Is.Zero);
            Assert.That(Get<bool>("mReflInputsStaged"), Is.False);
        }

        [Test]
        public void ReflectionsStillOwnSourceWhenDirectPipelineIsDisabled()
        {
            SteamAudioManager.UseThreadedDirectPipeline = false;
            Set("mReflectionsInFlight", true);
            Assert.That(SteamAudioManager.TryDeferSourceRelease(source), Is.True);
            Invoke("DrainPendingSourceReleases");
            Assert.That(source.Get(), Is.Not.EqualTo(IntPtr.Zero));
            Set("mReflectionsInFlight", false);
            Invoke("DrainPendingSourceReleases");
            Assert.That(source.Get(), Is.EqualTo(IntPtr.Zero));
        }

        [TestCase("mDirectInFlight")]
        [TestCase("mReflectionsInFlight")]
        public void ProbeSceneUnloadRetainsQueuedBatchUntilBothWorkersFinish(string worker)
        {
            var batch = new ProbeBatch(context);
            batch.Commit();
            Set(worker, true);
            simulator.AddProbeBatch(batch);
            simulator.RemoveProbeBatch(batch);
            batch.Release(); // Scene OnDestroy may run before either native worker completes.
            var pending = Get<ICollection>("mPendingProbeChanges");
            Assert.That(pending.Count, Is.EqualTo(2));
            Invoke("DrainPendingProbeChanges");
            Assert.That(pending.Count, Is.EqualTo(2));
            Set(worker, false);
            Set("mReflSnapHandles", new[] { new IntPtr(123) });
            Invoke("DrainPendingProbeChanges");
            Assert.That(pending.Count, Is.Zero);
            Assert.That(Get<IntPtr[]>("mReflSnapHandles")[0], Is.EqualTo(IntPtr.Zero));
            simulator.Commit();
        }

        static FieldInfo Field(string name) => typeof(SteamAudioManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        void Set(string name, object value) => Field(name).SetValue(manager, value);
        T Get<T>(string name) => (T)Field(name).GetValue(manager);
        void Invoke(string name) => typeof(SteamAudioManager).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
    }
}
#endif
