using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Basis.Social
{
    [Serializable] public sealed class BasisSocialRealtimeEvent { public string id, cursor, type, actorId; }
    public enum BasisSocialRealtimeState { Stopped, Connecting, Connected, Reconnecting }

    /// <summary>Small incremental SSE parser. Bounds both UTF-8 input and parsed event memory.</summary>
    public sealed class BasisSocialEventParser
    {
        private readonly StringBuilder line = new(), data = new();
        private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
        private readonly char[] chars = new char[4096];
        private readonly Action<BasisSocialRealtimeEvent> receive;
        private readonly HashSet<string> seen = new();
        private readonly Queue<string> recent = new();
        public string Cursor { get; private set; }
        public BasisSocialEventParser(Action<BasisSocialRealtimeEvent> receive, string cursor = null) { this.receive = receive ?? throw new ArgumentNullException(nameof(receive)); Cursor = cursor; }
        public void Feed(byte[] bytes, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                decoder.Convert(bytes, offset, count - offset, chars, 0, chars.Length, false, out int used, out int written, out _);
                offset += used;
                for (int i = 0; i < written; i++)
                {
                    char value = chars[i];
                    if (value == '\n') { ProcessLine(line.ToString().TrimEnd('\r')); line.Clear(); }
                    else { if (line.Length >= 65536) throw new FormatException("Realtime event exceeds its size limit."); line.Append(value); }
                }
            }
        }
        private void ProcessLine(string value)
        {
            if (value.Length == 0)
            {
                if (data.Length == 0) return;
                BasisSocialRealtimeEvent item;
                try { item = JsonUtility.FromJson<BasisSocialRealtimeEvent>(data.ToString()); }
                finally { data.Clear(); }
                if (item == null || string.IsNullOrEmpty(item.type)) throw new FormatException("Invalid realtime event.");
                if (!string.IsNullOrEmpty(item.cursor))
                {
                    if (!ValidCursor(item.cursor)) throw new FormatException("Invalid realtime cursor.");
                    if (ValidCursor(Cursor) && CompareCursor(item.cursor, Cursor) <= 0) return;
                    Cursor = item.cursor;
                }
                if (!string.IsNullOrEmpty(item.id))
                {
                    if (!seen.Add(item.id)) return;
                    recent.Enqueue(item.id);
                    if (recent.Count > 512) seen.Remove(recent.Dequeue());
                }
                receive(item); return;
            }
            if (!value.StartsWith("data:", StringComparison.Ordinal)) return;
            string part = value.Substring(5); if (part.StartsWith(" ", StringComparison.Ordinal)) part = part.Substring(1);
            if (data.Length + part.Length > 65536) throw new FormatException("Realtime event exceeds its size limit.");
            if (data.Length > 0) data.Append('\n'); data.Append(part);
        }
        public static bool ValidCursor(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 42) return false;
            foreach (char character in value) if ((character < '0' || character > '9') && character != '-') return false;
            var parts = value.Split('-'); return parts.Length == 2 && ulong.TryParse(parts[0], out _) && ulong.TryParse(parts[1], out _);
        }
        private static int CompareCursor(string left, string right)
        {
            var a = left.Split('-'); var b = right.Split('-');
            int first = ulong.Parse(a[0]).CompareTo(ulong.Parse(b[0])); return first == 0 ? ulong.Parse(a[1]).CompareTo(ulong.Parse(b[1])) : first;
        }
    }

    /// <summary>Reconnects with the last cursor; snapshots are refreshed on connection and replay gaps.</summary>
    public sealed class BasisSocialRealtime : IDisposable
    {
        private readonly BasisSocialApiClient client;
        private readonly string baseUrl;
        private readonly CancellationTokenSource lifetime = new();
        private bool started;
        private string loggedDiagnostic;
        public string LastErrorCode { get; private set; }
        public BasisSocialRealtimeState State { get; private set; }
        public event Action Changed;
        public event Action<BasisSocialRealtimeEvent> Event;
        public BasisSocialRealtime(BasisSocialApiClient client, string baseUrl, bool allowLoopbackHttp = false)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.baseUrl = BasisSocialRuntime.NormalizeBaseUrl(baseUrl, allowLoopbackHttp);
        }
        public void Start() { if (!started) { started = true; _ = RunAsync(); } }
        private async Task RunAsync()
        {
            int failures = 0; string cursor = null;
            try
            {
                while (!lifetime.IsCancellationRequested && client.HasSession)
                {
                    SetState(failures == 0 ? BasisSocialRealtimeState.Connecting : BasisSocialRealtimeState.Reconnecting);
                    var parser = new BasisSocialEventParser(item =>
                    {
                        if (lifetime.IsCancellationRequested) return;
                        if (item.type == "realtime.connected") { failures = 0; LastErrorCode = null; SetState(BasisSocialRealtimeState.Connected); }
                        Dispatch(item);
                    }, cursor);
                    try { await client.ReadRealtimeAsync(baseUrl, parser, lifetime.Token); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                    catch (Exception error) { Diagnose(FailureCode(error)); }
                    cursor = parser.Cursor;
                    if (!client.HasSession || lifetime.IsCancellationRequested) break;
                    if (LastErrorCode == null) Diagnose("stream_closed");
                    SetState(BasisSocialRealtimeState.Reconnecting);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(5, failures++))), lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally { SetState(BasisSocialRealtimeState.Stopped); }
        }
        // A destroyed UI listener must not make DownloadHandlerScript reject valid network bytes.
        private void Dispatch(BasisSocialRealtimeEvent item)
        {
            if (Event == null) return;
            foreach (Action<BasisSocialRealtimeEvent> listener in Event.GetInvocationList())
                try { listener(item); } catch (Exception) { Diagnose("listener_failed"); }
        }
        private void SetState(BasisSocialRealtimeState state)
        {
            if (State == state) return; State = state;
            if (Changed == null) return;
            foreach (Action listener in Changed.GetInvocationList())
                try { listener(); } catch (Exception) { Diagnose("listener_failed"); }
        }
        private void Diagnose(string code)
        {
            LastErrorCode = code;
            if (loggedDiagnostic == code) return;
            loggedDiagnostic = code;
            Debug.LogWarning("Basis Social realtime: " + code + ".");
        }
        /// <summary>Only fixed local codes and numeric HTTP status may enter diagnostics; never remote bodies or exception messages.</summary>
        public static string FailureCode(Exception error)
        {
            if (error is TimeoutException) return "stream_timeout";
            if (error is OperationCanceledException) return "session_changed";
            if (error is BasisSocialApiException api)
            {
                if (api.ErrorCode == "realtime_invalid_event") return "invalid_event";
                if (api.ErrorCode == "realtime_invalid_content_type") return "invalid_content_type";
                if (api.StatusCode >= 100 && api.StatusCode <= 599) return "http_" + api.StatusCode;
            }
            return "network_error";
        }
        public void Dispose() { lifetime.Cancel(); Changed = null; Event = null; }
    }

    public sealed partial class BasisSocialApiClient
    {
        internal async Task ReadRealtimeAsync(string baseUrl, BasisSocialEventParser parser, CancellationToken cancellationToken)
        {
            long epoch = SnapshotGeneration();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string access;
                lock (sessionGate) { AssertCurrent(epoch); access = tokenStore.AccessToken; }
                if (string.IsNullOrEmpty(access)) { await RefreshSessionAsync(cancellationToken); lock (sessionGate) { AssertCurrent(epoch); access = tokenStore.AccessToken; } }
                using var handler = new SocialStreamHandler(parser);
                using var request = new UnityWebRequest(baseUrl + "/api/v1/realtime/events", UnityWebRequest.kHttpVerbGET)
                { downloadHandler = handler, redirectLimit = 0, timeout = 0, disposeDownloadHandlerOnDispose = false };
                request.SetRequestHeader("Authorization", "Bearer " + access);
                request.SetRequestHeader("Accept", "text/event-stream");
                if (BasisSocialEventParser.ValidCursor(parser.Cursor)) request.SetRequestHeader("Last-Event-ID", parser.Cursor);
                var operation = request.SendWebRequest();
                float lastByte = Time.realtimeSinceStartup;
                long received = 0;
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested) { request.Abort(); cancellationToken.ThrowIfCancellationRequested(); }
                    try { AssertCurrent(epoch); } catch { request.Abort(); throw; }
                    if (handler.BytesReceived != received) { received = handler.BytesReceived; lastByte = Time.realtimeSinceStartup; }
                    if (Time.realtimeSinceStartup - lastByte > 60) { request.Abort(); throw new TimeoutException("Realtime connection timed out."); }
                    await Task.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested(); AssertCurrent(epoch);
                if (request.responseCode == 401 && attempt == 0) { await RefreshSessionAsync(cancellationToken); continue; }
                if (request.responseCode != 200) throw new BasisSocialApiException(request.responseCode, "realtime_disconnected", "Live updates disconnected.");
                if (handler.Error != null) throw new BasisSocialApiException(0, "realtime_invalid_event", "The event stream could not be decoded.");
                if (!(request.GetResponseHeader("Content-Type") ?? "").StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                    throw new BasisSocialApiException(0, "realtime_invalid_content_type", "The service did not return an event stream.");
                if (request.result == UnityWebRequest.Result.ConnectionError) throw new BasisSocialApiException(0, "realtime_disconnected", "Live updates disconnected.");
                return;
            }
        }
        private sealed class SocialStreamHandler : DownloadHandlerScript
        {
            private readonly BasisSocialEventParser parser;
            public long BytesReceived { get; private set; }
            public Exception Error { get; private set; }
            public SocialStreamHandler(BasisSocialEventParser parser) : base(new byte[8192]) { this.parser = parser; }
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return true;
                BytesReceived += dataLength;
                try { parser.Feed(data, dataLength); return true; }
                catch (Exception error) { Error = error; return false; }
            }
        }
    }
}
