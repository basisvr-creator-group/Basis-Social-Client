// Isolated fixture harness only: real HTTP ranges + SDK decryption + native AssetBundle load.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

public sealed class BasisCatalogRangeProbe
{
    [UnityTest]
    public IEnumerator ReadsSeededBeeOverHttpAndLoadsItsMacSection()
    {
        Task run = ReadPackage();
        while (!run.IsCompleted) yield return null;
        run.GetAwaiter().GetResult();
    }

    private static async Task ReadPackage()
    {
        string url = Environment.GetEnvironmentVariable("BASIS_FIXTURE_HTTP_DOWNLOAD");
        Assert.That(Uri.TryCreate(url, UriKind.Absolute, out Uri endpoint) && endpoint.IsLoopback, Is.True, "This probe only uses the isolated loopback test service.");
        string basis = Environment.GetEnvironmentVariable("BASIS_FIXTURE_SOURCE");
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
            var header = await Range(url, 0, 7, null, -1);
            long connectorLength = BitConverter.ToInt64(header.bytes, 0);
            Assert.That(connectorLength, Is.InRange(1L, 1024L * 1024L));
            Assert.That(header.total, Is.LessThanOrEqualTo(1024L * 1024L));
            var connectorBytes = await Range(url, 8, 7 + connectorLength, header.etag, header.total);
            var payload = await Range(url, 8 + connectorLength, header.total - 1, header.etag, header.total);
            using var joined = new MemoryStream();
            joined.Write(header.bytes, 0, header.bytes.Length); joined.Write(connectorBytes.bytes, 0, connectorBytes.bytes.Length); joined.Write(payload.bytes, 0, payload.bytes.Length);
            using var hash = SHA256.Create();
            string digest = BitConverter.ToString(hash.ComputeHash(joined.ToArray())).Replace("-", "").ToLowerInvariant();
            Assert.That(header.etag, Is.EqualTo("\"" + digest + "\""));
            Assembly sdk = Assembly.LoadFrom(Path.Combine(assemblies, "BasisSDK.dll"));
            byte[] metadata = await Decrypt(sdk, connectorBytes.bytes);
            Type connectorType = sdk.GetType("BasisBundleConnector", true);
            object connector = sdk.GetType("BasisSerialization", true).GetMethod("DeserializeValue").MakeGenericMethod(connectorType).Invoke(null, new object[] { metadata });
            Assert.That(connectorType.GetField("UniqueVersion").GetValue(connector), Is.EqualTo("stage6-public-fixture-v1"));
            Array sections = (Array)connectorType.GetField("BasisBundleGenerated").GetValue(connector);
            Assert.That(sections.Length, Is.EqualTo(1));
            object section = sections.GetValue(0);
            Assert.That(section.GetType().GetField("Platform").GetValue(section), Is.EqualTo("StandaloneOSX"));
            Assert.That(section.GetType().GetField("EndByte").GetValue(section), Is.EqualTo(payload.bytes.LongLength));
            uint crc = (uint)section.GetType().GetField("AssetBundleCRC").GetValue(section);
            byte[] bytes = await Decrypt(sdk, payload.bytes);
            AssetBundle bundle = AssetBundle.LoadFromMemory(bytes, crc);
            Assert.That(bundle, Is.Not.Null);
            try { Assert.That(bundle.LoadAsset<TextAsset>("Assets/stage6-fixture.txt").text, Is.EqualTo("Basis catalog integration fixture: harmless TextAsset")); }
            finally { bundle.Unload(true); }
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
    }

    private static async Task<byte[]> Decrypt(Assembly sdk, byte[] bytes)
    {
        Type crypto = sdk.GetType("BasisEncryptionWrapper", true);
        object password = Activator.CreateInstance(crypto.GetNestedType("BasisPassword"));
        password.GetType().GetField("VP").SetValue(password, "stage6-public-fixture-only");
        Task task = (Task)crypto.GetMethod("DecryptFromBytesAsync").Invoke(null, new object[] { "stage6-http", password, bytes, null, CancellationToken.None });
        await task;
        object result = task.GetType().GetProperty("Result").GetValue(task);
        Assert.That(result.GetType().GetField("Success").GetValue(result), Is.EqualTo(true));
        return (byte[])result.GetType().GetField("Data").GetValue(result);
    }

    private static async Task<(byte[] bytes, string etag, long total)> Range(string url, long start, long end, string etag, long total)
    {
        using var request = UnityWebRequest.Get(url);
        request.redirectLimit = 0; request.timeout = 15;
        request.SetRequestHeader("Range", "bytes=" + start + "-" + end);
        if (etag != null) request.SetRequestHeader("If-Match", etag);
        var operation = request.SendWebRequest();
        while (!operation.isDone) await Task.Yield();
        Assert.That(request.responseCode, Is.EqualTo(206));
        Assert.That(BasisBeeRangePolicy.Validate(request.GetResponseHeader("Content-Range"), start, end,
            request.GetResponseHeader("ETag"), etag, total, out long receivedTotal, out string error), Is.True, error);
        Assert.That(request.downloadHandler.data.LongLength, Is.EqualTo(end - start + 1));
        return (request.downloadHandler.data, request.GetResponseHeader("ETag"), receivedTotal);
    }
}
