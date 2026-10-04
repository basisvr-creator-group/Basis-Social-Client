// Isolated Editor harness only. Uses the project's compiled SDK and actual BEE writer.
// Not imported into the Social package or any player build.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class BasisCatalogFixtureBuilder
{
    private const string PayloadText = "Basis catalog integration fixture: harmless TextAsset";

    [Test]
    public async Task BuildAndReadRealBeeContainer()
    {
        string basis = Environment.GetEnvironmentVariable("BASIS_FIXTURE_SOURCE");
        string output = Environment.GetEnvironmentVariable("BASIS_FIXTURE_OUTPUT");
        Assert.That(Directory.Exists(basis), Is.True, "Set BASIS_FIXTURE_SOURCE to the Basis Unity project.");
        Assert.That(Directory.Exists(output), Is.True, "Set BASIS_FIXTURE_OUTPUT to a private fixture directory.");
        string assemblies = Path.Combine(basis, "Library/ScriptAssemblies");
        ResolveEventHandler resolve = (_, args) =>
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            string path = Path.Combine(assemblies, name);
            if (!File.Exists(path)) path = Directory.EnumerateFiles(Path.Combine(basis, "Packages"), name, SearchOption.AllDirectories).FirstOrDefault();
            return path != null && File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolve;
        try
        {
            const string assetPath = "Assets/stage6-fixture.txt";
            File.WriteAllText(assetPath, PayloadText);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var manifest = BuildPipeline.BuildAssetBundles(output,
                new[] { new AssetBundleBuild { assetBundleName = "stage6-fixture", assetNames = new[] { assetPath } } },
                BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression,
                BuildTarget.StandaloneOSX);
            Assert.That(manifest, Is.Not.Null);
            string bundlePath = Path.Combine(output, "stage6-fixture");
            Assert.That(BuildPipeline.GetCRCForAssetBundle(bundlePath, out uint crc), Is.True);
            Assembly sdk = Assembly.LoadFrom(Path.Combine(assemblies, "BasisSDK.dll"));
            Assembly editor = Assembly.LoadFrom(Path.Combine(assemblies, "BasisSDKEditor.dll"));
            string loaderPath = Environment.GetEnvironmentVariable("BASIS_FIXTURE_LOADER");
            Assert.That(File.Exists(loaderPath), Is.True, "Set BASIS_FIXTURE_LOADER to the current compiled loader.");
            Assembly loader = Assembly.LoadFrom(loaderPath);
            Type metaType = loader.GetType("BasisBEEExtensionMeta", true);
            object cached = Activator.CreateInstance(metaType);
            Set(cached, "CachedVersionTag", "\"old\"");
            MethodInfo cacheDecision = loader.GetType("BasisContentVersion", true).GetMethod("ShouldUseCache");
            string cacheUrl = "https://example.com/fixture-" + Guid.NewGuid() + ".bee";
            Assert.That(cacheDecision.Invoke(null, new[] { cached, "\"new\"", cacheUrl }), Is.EqualTo(false));
            var limited = Assert.Throws<TargetInvocationException>(() => cacheDecision.Invoke(null, new[] { cached, "\"new\"", cacheUrl }));
            Assert.That(limited.InnerException.GetType().Name, Is.EqualTo("RefreshDeferredException"), "A throttled refresh must fail explicitly instead of serving old bytes or amplifying downloads.");
            Assert.That(cacheDecision.Invoke(null, new[] { cached, "\"old\"", cacheUrl }), Is.EqualTo(true));
            // Verify the actual metadata-only path preserves the user's saved entry and does
            // not fetch bytes when the version refresh is throttled.
            object cachedRemote = metaType.GetField("StoredRemote").GetValue(cached);
            Set(cachedRemote, "RemoteBeeFileLocation", cacheUrl);
            object cachedLocal = metaType.GetField("StoredLocal").GetValue(cached);
            Set(cachedLocal, "DownloadedConnectorFileLocation", bundlePath);
            Set(cached, "DownloadedPlatform", "StandaloneOSX");
            Type loadHandler = loader.GetType("BasisLoadHandler", true);
            object entries = loadHandler.GetField("OnDiscData").GetValue(null);
            entries.GetType().GetProperty("Item").SetValue(entries, cached, new object[] { cacheUrl });
            object loadable = Activator.CreateInstance(sdk.GetType("BasisLoadableBundle", true));
            object remote = loadable.GetType().GetField("BasisRemoteBundleEncrypted").GetValue(loadable);
            Set(remote, "RemoteBeeFileLocation", cacheUrl); Set(remote, "RemoteVersionTag", "\"new\"");
            object wrapper = Activator.CreateInstance(loader.GetType("BasisTrackedBundleWrapper", true));
            Set(wrapper, "LoadableBundle", loadable);
            Task metadataTask = (Task)loader.GetType("BasisBeeManagement", true).GetMethod("HandleMetaOnlyLoad").Invoke(null, new[] { wrapper, null, (object)CancellationToken.None });
            await metadataTask;
            object metadataResult = metadataTask.GetType().GetProperty("Result").GetValue(metadataTask);
            Assert.That(metadataResult.GetType().GetField("Loaded").GetValue(metadataResult), Is.EqualTo(false));
            Assert.That(metadataResult.GetType().GetField("IsTransient").GetValue(metadataResult), Is.EqualTo(true));
            Assert.That(entries.GetType().GetProperty("Count").GetValue(entries), Is.EqualTo(1));
            Assert.That(File.Exists(bundlePath), Is.True);
            Type crypto = sdk.GetType("BasisEncryptionWrapper", true);
            object password = Activator.CreateInstance(crypto.GetNestedType("BasisPassword"));
            password.GetType().GetField("VP").SetValue(password, "stage6-public-fixture-only");
            MethodInfo encrypt = crypto.GetMethod("EncryptToBytesAsync");
            byte[] encrypted = await (Task<byte[]>)encrypt.Invoke(null, new object[] { "stage6", password, File.ReadAllBytes(bundlePath), null });
            Type generatedType = sdk.GetType("BasisBundleGenerated", true);
            object section = Activator.CreateInstance(generatedType);
            Set(section, "Platform", "StandaloneOSX"); Set(section, "AssetMode", "Gameobject");
            Set(section, "AssetToLoadName", assetPath); Set(section, "AssetBundleCRC", crc);
            Set(section, "EndByte", (long)encrypted.Length);
            Type connectorType = sdk.GetType("BasisBundleConnector", true);
            object connector = Activator.CreateInstance(connectorType);
            Set(connector, "UniqueVersion", "stage6-public-fixture-v1");
            Array sections = Array.CreateInstance(generatedType, 1); sections.SetValue(section, 0);
            Set(connector, "BasisBundleGenerated", sections);
            object description = Activator.CreateInstance(sdk.GetType("BasisBundleDescription", true));
            Set(description, "AssetBundleName", "Harmless catalog transport fixture");
            Set(connector, "BasisBundleDescription", description);
            // Dynamically loaded SDK types have no Unity serializer type tree in this isolated
            // project. Use a registered fixture DTO for the exact documented {Value:...} wire
            // shape and verify it using the actual SDK reader before packaging.
            byte[] serialized = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(new FixtureEnvelope
            {
                Value = new FixtureConnector
                {
                    UniqueVersion = "stage6-public-fixture-v1",
                    BasisBundleDescription = new FixtureDescription { AssetBundleName = "Harmless catalog transport fixture" },
                    BasisBundleGenerated = new[] { new FixtureSection { Platform = "StandaloneOSX", AssetMode = "Gameobject", AssetToLoadName = assetPath, AssetBundleCRC = crc, EndByte = encrypted.LongLength } }
                }
            }));
            object roundTrip = sdk.GetType("BasisSerialization", true).GetMethod("DeserializeValue").MakeGenericMethod(connectorType).Invoke(null, new object[] { serialized });
            Assert.That(connectorType.GetField("UniqueVersion").GetValue(roundTrip), Is.EqualTo("stage6-public-fixture-v1"));
            byte[] encryptedConnector = await (Task<byte[]>)encrypt.Invoke(null, new object[] { "stage6", password, serialized, null });
            string sectionPath = Path.Combine(output, "stage6-section.encrypted"); File.WriteAllBytes(sectionPath, encrypted);
            string beePath = Path.Combine(output, "stage6-native-macos.bee");
            await (Task)editor.GetType("BasisBundleBuild", true).GetMethod("CombineFiles").Invoke(null,
                new object[] { beePath, new List<string> { sectionPath }, encryptedConnector, CancellationToken.None });
            byte[] bee = File.ReadAllBytes(beePath);
            Assert.That(BitConverter.ToInt64(bee, 0), Is.EqualTo(encryptedConnector.LongLength));
            MethodInfo decrypt = crypto.GetMethod("DecryptFromBytesAsync");
            Task decryptedTask = (Task)decrypt.Invoke(null, new object[] { "stage6", password, encrypted, null, CancellationToken.None });
            await decryptedTask;
            object decrypted = decryptedTask.GetType().GetProperty("Result").GetValue(decryptedTask);
            Assert.That((bool)decrypted.GetType().GetField("Success").GetValue(decrypted), Is.True);
            byte[] bytes = (byte[])decrypted.GetType().GetField("Data").GetValue(decrypted);
            AssetBundle bundle = AssetBundle.LoadFromMemory(bytes, crc);
            Assert.That(bundle, Is.Not.Null);
            try { Assert.That(bundle.LoadAsset<TextAsset>(assetPath).text, Is.EqualTo(PayloadText)); }
            finally { bundle.Unload(true); }
            File.WriteAllText(Path.Combine(output, "fixture-info.json"), "{\"file\":\"stage6-native-macos.bee\",\"platform\":\"StandaloneOSX\",\"password\":\"stage6-public-fixture-only\",\"purpose\":\"TextAsset container/decryption probe; not a playable avatar/world\"}");
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
    }

    [Serializable] private sealed class FixtureEnvelope { public FixtureConnector Value; }
    [Serializable] private sealed class FixtureConnector { public string UniqueVersion; public FixtureDescription BasisBundleDescription; public FixtureSection[] BasisBundleGenerated; }
    [Serializable] private sealed class FixtureDescription { public string AssetBundleName; }
    [Serializable] private sealed class FixtureSection { public string Platform; public string AssetMode; public string AssetToLoadName; public uint AssetBundleCRC; public long EndByte; public bool IsEncrypted = true; }

    private static void Set(object value, string field, object data) => value.GetType().GetField(field).SetValue(value, data);
}
