// Modified by Crane Valley: the brain is found through the invite it writes for this process (Discovery.cs) and must
// prove itself with a mutual HMAC handshake; a refusal before that is shown by its code, never in the brain's words; a
// welcome that resumes nothing starts the request count and the held answers afresh; the link, token, account, usage,
// memory, steering and window plumbing is removed, and the brain alone starts a run: the command line only connects;
// a message over the brain's 16 MB limit closes the link.
// The bridge to the brain (docs/protocol.md): one WebSocket out to the brain, JSON text frames. The socket lives on
// background threads; everything that touches the game runs on the main thread, in Tick() (every frame), where all
// requests that arrived since the last frame are handled, many per frame. Deferred answers (move, watch, key taps,
// session changes) are Pending entries ticked each frame until they answer.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Cluewren.Tests")]

namespace Cluewren
{
    /// <summary>A message for the send loop: text, or bytes from the pool that it gives back once sent.</summary>
    public readonly struct Outgoing
    {
        public readonly string Text;
        public readonly byte[] Bytes;
        public readonly int Length;

        public Outgoing(byte[] bytes, int length)
        {
            Text = null;
            Bytes = bytes;
            Length = length;
        }

        Outgoing(string text)
        {
            Text = text;
            Bytes = null;
            Length = 0;
        }

        public static implicit operator Outgoing(string text) => new Outgoing(text);
    }

    /// <summary>A request answered later: Tick(pending, now) returns true once it has answered.</summary>
    public sealed class Pending
    {
        public long Id;
        public string Op;
        public double Start;
        public double Seconds;
        public Func<Pending, double, bool> Tick;
    }

    /// <summary>What differs between the editor and a player build (the editor assembly provides its own).</summary>
    public interface IPlaytestHost
    {
        /// <summary>"editor" or "game".</summary>
        string HostName { get; }

        /// <summary>The project's folder (where Assets/ is), or "" when it isn't known (a player away from its project).</summary>
        string ProjectDir { get; }

        /// <summary>The project's productGUID (Player Settings), or "".</summary>
        string ProjectId { get; }

        bool SessionRunning { get; }

        /// <summary>session start / stop / restart for the editor (play mode); a player handles them itself (return false).</summary>
        bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args);

        /// <summary>A command-line start can't go on: leave with this code.</summary>
        void Exit(int code);
    }

    public sealed class PlaytestClient
    {
        public const int Protocol = 2;
        public const string PluginVersion = "0.2.0";
        const double MaxBackoff = 30.0;
        const double LookSeconds = 1.0;
        const double HandshakeSeconds = 10.0;
        const int MaxLogEventsPerFrame = 50;

        public static PlaytestClient Current { get; private set; }

        public IPlaytestHost Host { get; }

        public string Url { get; private set; } = "";
        public bool Connected { get; private set; }
        public bool WantConnected { get; private set; }
        public bool Connecting => socket != null && !Connected;
        public string SessionId { get; private set; }
        public bool RunActive { get; private set; }
        public string Problem { get; private set; } = "";
        public string LastStatus { get; private set; } = "";

        // The session is over (its connection lost or closed, a refusal, a disconnect, a welcome to another one): what
        // waited to answer in it has nobody to answer. Not for the editor's own close when it reloads its scripts.
        public event Action SessionEnded;

        // A command-line start exits with 1 when the brain refuses it, nobody being there to read the refusal.
        public bool ExitOnFailure;
        // A command-line start (-CluewrenConnectTimeout, default 120 s; 0 for none): with no welcome from the brain in
        // this long, log the error and exit 1 instead of waiting for ever
        public double ConnectTimeout;
        public const double DefaultConnectTimeout = 120.0;
        // When this client first tried to connect (wall clock, kept across the editor's script reloads), and whether
        // the brain has welcomed it since
        double connectSince;
        bool welcomed;
        bool gaveUp;
        string lastFailure = "";

        byte[] secret;
        byte[] pluginNonce;
        double handshakeDeadline;
        double backoff = 1.0;
        double nextReconnect = double.MaxValue;
        int generation;
        const int MaxMessageBytes = 16 * 1024 * 1024;

        ClientWebSocket socket;
        CancellationTokenSource cancel;

        // (close: the status of the close frame that ended a connection, 0 when it ended without one)
        readonly ConcurrentQueue<(int gen, string kind, string text, int close)> incoming = new ConcurrentQueue<(int, string, string, int)>();
        readonly BlockingCollection<Outgoing> outgoing = new BlockingCollection<Outgoing>(new ConcurrentQueue<Outgoing>());
        readonly List<object> events = new List<object>();
        readonly List<Pending> pending = new List<Pending>();
        readonly List<string> heldResponses = new List<string>();
        double lastHeartbeat;
        string resumeSession;  // a session to resume after the editor reloaded its scripts
        long lastRequest;      // the newest request handled: on a resume, the brain sends again what came after it
        string askedResume;    // the session the last hello asked to resume

        public readonly Detectors Detectors;

        public PlaytestClient(IPlaytestHost host)
        {
            Host = host;
            Detectors = new Detectors(this);
            Current = this;
            LogCapture.Install();
        }

        public static double Now => Time.realtimeSinceStartupAsDouble;

        /// <summary>The game time of the played world, or null when no game is running.</summary>
        public object GameTime => Application.isPlaying ? (object)Json.Round(Time.timeAsDouble, 3) : null;

        // -- connecting ------------------------------------------------------------------------------------

        public void Connect()
        {
            WantConnected = true;
            backoff = 1.0;
            Problem = "";
            if (connectSince <= 0) connectSince = WallNow;
            OpenSocket();
        }

        /// <summary>Connect again after the editor reloaded its scripts: the same brain session goes on.</summary>
        public void Resume(string session, bool runActive, double connectTimeout = 0, double since = 0, bool welcomedAlready = false, long received = 0)
        {
            resumeSession = session;
            lastRequest = received;
            RunActive = runActive;
            ConnectTimeout = connectTimeout;
            connectSince = since;
            welcomed = welcomedAlready;
            Connect();
        }

        static double WallNow => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

        /// <summary>-CluewrenConnectTimeout=seconds (default 120; 0 waits for ever).</summary>
        public static double CommandLineConnectTimeout()
        {
            var v = Arg("CluewrenConnectTimeout");
            return !string.IsNullOrEmpty(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0
                ? s : DefaultConnectTimeout;
        }

        /// <summary>No welcome within ConnectTimeout: log the error, exit 1.</summary>
        void CheckConnectTimeout()
        {
            if (gaveUp || welcomed || ConnectTimeout <= 0 || connectSince <= 0 || WallNow - connectSince < ConnectTimeout) return;
            gaveUp = true;
            var why = !string.IsNullOrEmpty(Problem) ? Problem : !string.IsNullOrEmpty(lastFailure) ? lastFailure : "no run invited this process";
            var error = $"Couldn’t connect to Cluewren within {ConnectTimeout:0.#} s ({why})";
            WantConnected = false;
            CloseSocket(WebSocketCloseStatus.NormalClosure, "connect timeout");
            SetConnected(false);
            Status(error);
            Debug.LogError("[Cluewren] " + error);
            Debug.Log("[Cluewren] Exiting with code 1");
            Host.Exit(1);
        }

        public void Disconnect()
        {
            WantConnected = false;
            RunActive = false;
            Problem = "";
            handshakeDeadline = 0;
            resumeSession = null;
            CloseSocket(WebSocketCloseStatus.NormalClosure, "disconnect");
            SetConnected(false);
            Hands.ReleaseAll();
            Media.Stop();
            SessionEnded?.Invoke();
            Status("Disconnected");
        }

        void OpenSocket()
        {
            if (TestWire != null) return;  // (tests: no socket)
            CloseSocket(WebSocketCloseStatus.NormalClosure, "reconnect");
            var invite = Invites?.Look();
            if (invite == null)
            {
                Problem = Invites?.Problem ?? "";
                if (Problem.Length > 0)
                {
                    lastFailure = Problem;
                    if (LastStatus != Problem) Status(Problem);
                }
                backoff = 1.0;
                nextReconnect = Now + LookSeconds;
                return;
            }
            Url = invite.Url.ToString();
            var uri = invite.Url;
            secret = invite.Secret;
            var gen = ++generation;
            var ws = new ClientWebSocket();
            // A system-wide proxy must not carry a connection that is only ever meant for this machine.
            ws.Options.Proxy = null;
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket = ws;
            cancel = new CancellationTokenSource();
            var ct = cancel.Token;
            nextReconnect = double.MaxValue;  // until this attempt fails
            Task.Run(() => RunSocket(ws, uri, gen, ct));
        }

        async Task RunSocket(ClientWebSocket ws, Uri url, int gen, CancellationToken ct)
        {
            try
            {
                await ws.ConnectAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                incoming.Enqueue((gen, "failed", e.InnerException?.Message ?? e.Message, 0));
                return;
            }
            incoming.Enqueue((gen, "open", null, 0));
            var sender = Task.Run(() => SendLoop(ws, gen, ct));
            var buffer = new byte[1 << 16];
            var message = new MemoryStream();
            string why = "closed";
            var close = 0;
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        why = result.CloseStatusDescription ?? "";
                        close = (int)(result.CloseStatus ?? WebSocketCloseStatus.Empty);
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                    // The brain's own limit (BridgeLink.MaxFrameBytes), as Godot's and Unreal's plugins hold it: before the
                    // handshake, whatever took the announced port could send more.
                    if (message.Length > MaxMessageBytes)
                    {
                        why = "a message over 16 MB";
                        break;
                    }
                    if (!result.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    if (result.MessageType == WebSocketMessageType.Text)
                        incoming.Enqueue((gen, "text", text, 0));
                }
            }
            catch (Exception e)
            {
                why = e.InnerException?.Message ?? e.Message;
            }
            incoming.Enqueue((gen, "closed", why, close));
            try
            {
                await sender.ConfigureAwait(false);
            }
            catch
            {
                // (closing)
            }
        }

        async Task SendLoop(ClientWebSocket ws, int gen, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    if (!outgoing.TryTake(out var message, 200, ct)) continue;
                    if (gen != Volatile.Read(ref generation)) return;
                    var bytes = message.Bytes;
                    var length = message.Length;
                    if (bytes == null)
                    {
                        bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(message.Text.Length));
                        length = Encoding.UTF8.GetBytes(message.Text, 0, message.Text.Length, bytes, 0);
                    }
                    await ws.SendAsync(new ArraySegment<byte>(bytes, 0, length), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    ArrayPool<byte>.Shared.Return(bytes);
                }
            }
            catch
            {
                // (the receive loop tells)
            }
        }

        void CloseSocket(WebSocketCloseStatus status, string why)
        {
            var ws = socket;
            var c = cancel;
            socket = null;
            cancel = null;
            ++generation;
            while (outgoing.TryTake(out _))
            {
            }
            if (ws == null) return;
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseOutputAsync(status, why, CancellationToken.None).Wait(300);
            }
            catch
            {
                // (already gone)
            }
            try
            {
                c?.Cancel();
                ws.Dispose();
            }
            catch
            {
                // (already gone)
            }
        }

        /// <summary>Close for a script reload in the editor (code 1012, "service restart"): the brain keeps the session
        /// for the plugin to resume.</summary>
        public void CloseForReload()
        {
            CloseSocket((WebSocketCloseStatus)1012, "domain reload");
            Connected = false;
        }

        void ScheduleReconnect()
        {
            if (!WantConnected) return;
            nextReconnect = Now + backoff;
            backoff = Math.Min(backoff * 2, MaxBackoff);
        }

        void SetConnected(bool connected)
        {
            if (Connected == connected) return;
            Connected = connected;
        }

        // -- sending ---------------------------------------------------------------------------------------

        public void Send(Dictionary<string, object> frame)
        {
            if (Linked) Put(Json.Write(frame));
        }

        bool Linked => Connected && (socket != null || TestWire != null);

        void Put(string text)
        {
            if (TestWire != null) TestWire(text);
            else outgoing.Add(text);
        }

        // -- tests: frames sent go to TestWire instead of a socket, and frames come in as from the brain -----------

        internal Action<string> TestWire;
        internal InviteWatch TestInvites;

        // A test never takes, or refuses, this process's own invite.
        InviteWatch Invites => TestInvites ?? (TestWire == null ? Discovery.Invites : null);

        internal void TestConnected(bool connected) => SetConnected(connected);

        internal void TestReceive(string json) => HandleMessage(json);

        internal void TestOpen(byte[] invited)
        {
            secret = invited;
            SendHello();
        }

        /// <summary>The connection's number now: a message made for one (on a worker thread) isn't sent on another.</summary>
        public int ConnectionGeneration => Volatile.Read(ref generation);

        /// <summary>Send a message already written as JSON, from any thread, only on the connection it was made for.</summary>
        public bool SendIfConnection(Outgoing message, int gen)
        {
            if (!Connected || socket == null || gen != Volatile.Read(ref generation)) return false;
            outgoing.Add(message);
            return true;
        }

        /// <summary>A response already written as JSON: sent now, or held until connected again (as Respond does).</summary>
        public void SendResponseText(string text) => SendOrHoldText(text);

        public int SessionSerial { get; private set; }

        // On the main thread, where a new session begins: what a worker thread finishes for the session before (a
        // capture read back and encoded) would otherwise answer, with that session's request id, into this one.
        public void InSession(int serial, Action answer)
        {
            MainThread.Post(() =>
            {
                if (serial == SessionSerial) answer();
            });
        }

        void SendHello()
        {
            pluginNonce = Handshake.NewNonce();
            var projectDir = Host.ProjectDir ?? "";
            var hello = new Dictionary<string, object>
            {
                ["type"] = "hello",
                ["protocol"] = Protocol,
                ["plugin_version"] = PluginVersion,
                ["engine"] = "unity",
                ["engine_version"] = Application.unityVersion,
                ["project"] = Application.productName,
                ["host"] = Host.HostName,
                ["input"] = Hands.Capabilities(),
                // what this plugin does beyond the protocol's first version: the brain falls back for one without them
                ["features"] = Media.Compiled ? new List<object> { "timed_hold", "pointer", "levels", "media" } : new List<object> { "timed_hold", "pointer", "levels" },
                ["nonce"] = Convert.ToBase64String(pluginNonce),
                ["project_dir"] = projectDir,
                ["mac"] = Convert.ToBase64String(Handshake.HelloMac(secret, pluginNonce, projectDir)),
            };
            var id = Host.ProjectId;
            if (!string.IsNullOrEmpty(id)) hello["project_id"] = id;
            if (!string.IsNullOrEmpty(resumeSession))
            {
                hello["resume"] = resumeSession;
                hello["received"] = lastRequest;
            }
            else Media.Stop();  // (a new connection: no recording goes on from the one before)
            askedResume = resumeSession;
            Put(Json.Write(hello));
        }

        public void Respond(long id, object result)
        {
            var frame = new Dictionary<string, object>
            {
                ["type"] = "response",
                ["id"] = id,
                ["ok"] = true,
                ["result"] = result ?? new Dictionary<string, object>(),
                ["t"] = GameTime,
            };
            SendOrHold(frame);
        }

        public void RespondError(long id, string error, int status = 400)
        {
            var frame = new Dictionary<string, object>
            {
                ["type"] = "response",
                ["id"] = id,
                ["ok"] = false,
                ["status"] = status,
                ["error"] = error,
                ["t"] = GameTime,
            };
            SendOrHold(frame);
        }

        void SendOrHold(Dictionary<string, object> frame)
        {
            SendOrHoldText(Json.Write(frame));
        }

        void SendOrHoldText(string text)
        {
            if (Linked)
            {
                Put(text);
                return;
            }
            // (between a script reload and the resumed connection: answered once connected again)
            if (WantConnected && heldResponses.Count < 200) heldResponses.Add(text);
        }

        public void QueueEvent(Dictionary<string, object> e)
        {
            if (Connected) events.Add(e);
        }

        public void AddPending(Pending p)
        {
            p.Start = Now;
            // Its first tick right away: movement starts in the frame the request came
            if (p.Tick != null && p.Tick(p, p.Start)) return;
            pending.Add(p);
        }

        public void Status(string text)
        {
            LastStatus = text;
            Debug.Log("[Cluewren] " + text);
        }

        // -- every frame -----------------------------------------------------------------------------------

        public void Tick()
        {
            MainThread.Run();
            CheckConnectTimeout();
            var now = Now;
            if (WantConnected && !Connected && socket == null && now >= nextReconnect)
                OpenSocket();
            HandleReceived();
            CheckHandshake(now);
            Hands.Tick(Connected, RunActive);
            if (!Connected)
            {
                LogCapture.Drain();
                events.Clear();
                return;
            }
            CheckSessionChange();
            for (var i = pending.Count - 1; i >= 0; --i)
            {
                bool done;
                try
                {
                    done = pending[i].Tick(pending[i], now);
                }
                catch (Exception e)
                {
                    RespondError(pending[i].Id, $"{pending[i].Op} failed: {e.Message}", 500);
                    done = true;
                }
                if (done) pending.RemoveAt(i);
            }
            Media.Tick(this);
            var lines = LogCapture.Drain();
            var sent = 0;
            foreach (var line in lines)
            {
                if (!Application.isPlaying) break;
                if (++sent <= MaxLogEventsPerFrame)
                {
                    QueueEvent(new Dictionary<string, object>
                    {
                        ["kind"] = "log",
                        ["t"] = GameTime,
                        ["verbosity"] = line.Verbosity,
                        ["category"] = line.Category,
                        ["message"] = line.Message.Length > 2000 ? line.Message.Substring(0, 2000) : line.Message,
                    });
                }
            }
            if (lines.Count > 0 && Application.isPlaying) Detectors.OnLogLines(lines);
            if (Application.isPlaying) Detectors.Tick();
            if (now - lastHeartbeat >= 1.0)
            {
                lastHeartbeat = now;
                QueueEvent(new Dictionary<string, object>
                {
                    ["kind"] = "heartbeat",
                    ["t"] = GameTime,
                    ["fps"] = Math.Round(Time.unscaledDeltaTime > 0 ? 1.0 / Time.unscaledDeltaTime : 0.0),
                    ["paused"] = Ops.IsPaused,
                });
            }
            Hands.Flush();
            FlushEvents();
        }

        // A peer that accepts the socket and never welcomes must not hold the next attempt back for ever.
        void CheckHandshake(double now)
        {
            if (handshakeDeadline <= 0 || now < handshakeDeadline) return;
            handshakeDeadline = 0;
            CloseSocket(WebSocketCloseStatus.NormalClosure, "no welcome");
            lastFailure = $"No welcome from {Url} within {HandshakeSeconds:0} s";
            Problem = lastFailure;
            Status(Problem);
            ScheduleReconnect();
        }

        void HandleReceived()
        {
            while (incoming.TryDequeue(out var item))
            {
                if (item.gen != generation) continue;
                switch (item.kind)
                {
                    case "open":
                        lastHeartbeat = 0;
                        handshakeDeadline = Now + HandshakeSeconds;
                        SendHello();
                        break;
                    case "failed":
                        socket = null;
                        // (Windows' socket errors come padded with NULs and a line break)
                        lastFailure = (item.text ?? "").Replace("\0", "").Trim();
                        Problem = $"Can’t reach Cluewren at {Url} ({lastFailure}). Trying again…";
                        if (LastStatus != Problem) Status(Problem);
                        ScheduleReconnect();
                        break;
                    case "closed":
                        Closed(item.close, item.text);
                        break;
                    case "text":
                        HandleMessage(item.text);
                        break;
                }
            }
        }

        internal void Closed(int status, string why)
        {
            var was = Connected;
            socket = null;
            handshakeDeadline = 0;
            SetConnected(false);
            if (!was && status == (int)WebSocketCloseStatus.PolicyViolation)
            {
                // The brain's refusal, whose error frame didn't arrive: the close's reason is the same code.
                if (RefusalCode(why) is { } code) Refuse("Refused: " + Refusal(code), WebSocketCloseStatus.NormalClosure, "refused");
                else NoProof();
                return;
            }
            if (was) Status(status > 0 ? $"Connection closed ({status} {why})" : $"Connection closed ({why})");
            Hands.ReleaseAll();
            Media.Stop();  // (a recording doesn't outlive its connection)
            // A session ends with its connection, however that was lost: the next hello asks for no resume. Only the
            // editor's own close for a script reload (CloseForReload, which doesn't come here) is resumed: an answer
            // on its way when a socket drops is not known to have arrived, and the brain acknowledges none.
            if (was)
            {
                // Forgotten here, not only left unasked for: a script reload before the next welcome saves what the
                // client holds, and would ask to resume it.
                SessionId = null;
                SessionEnded?.Invoke();
            }
            ScheduleReconnect();
        }

        void FlushEvents()
        {
            if (events.Count == 0) return;
            Send(new Dictionary<string, object> { ["type"] = "events", ["events"] = new List<object>(events), ["t"] = GameTime });
            events.Clear();
        }

        // Sessions: play mode starting or stopping, a scene loaded in its place
        bool lastRunning;
        int lastSceneHandle = -1;

        void CheckSessionChange()
        {
            var running = Host.SessionRunning;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var handle = running && scene.IsValid() ? scene.GetHashCode() : -1;
            if (running == lastRunning && handle == lastSceneHandle) return;
            var first = lastSceneHandle == -1 && !lastRunning;
            lastRunning = running;
            lastSceneHandle = handle;
            Detectors.Reset();
            Paths.Prune();
            if (first && !running) return;
            QueueEvent(new Dictionary<string, object> { ["kind"] = "session", ["t"] = GameTime, ["running"] = running, ["scene"] = running ? scene.path : "" });
        }

        // -- frames from the brain -------------------------------------------------------------------------

        void HandleMessage(string text)
        {
            Dictionary<string, object> m;
            try
            {
                m = Json.ParseObject(text);
            }
            catch (Exception e)
            {
                if (!Connected)
                {
                    NoProof();
                    return;
                }
                Debug.LogWarning($"[Cluewren] Unreadable message from the brain ({e.Message}): {text.Substring(0, Math.Min(200, text.Length))}");
                return;
            }
            if (!Connected)
            {
                if (m != null && Json.Str(m, "type") == "welcome" && Proved(m, out var brainNonce)) Welcomed(m, brainNonce);
                else if (m != null && Json.Str(m, "type") == "error" && m.TryGetValue("code", out var given) && RefusalCode(given) is { } code)
                    Refuse("Refused: " + Refusal(code), WebSocketCloseStatus.NormalClosure, "refused");
                else NoProof();
                return;
            }
            if (m == null) return;
            var type = Json.Str(m, "type");
            switch (type)
            {
                case "request":
                {
                    var id = (long)Json.Num(m, "id", 0);
                    lastRequest = Math.Max(lastRequest, id);
                    var op = Json.Str(m, "op");
                    var args = Json.Obj(m, "args") ?? new Dictionary<string, object>();
                    try
                    {
                        Ops.Handle(this, id, op, args);
                    }
                    catch (RcError e)
                    {
                        RespondError(id, e.Message, e.Status);
                    }
                    catch (Exception e)
                    {
                        RespondError(id, $"{op} failed: {e.GetType().Name}: {e.Message}", 500);
                    }
                    break;
                }
                case "status":
                    Status(Json.Str(m, "text"));
                    break;
                case "finding":
                {
                    var f = Json.Obj(m, "finding");
                    if (f != null) Status("Finding: " + Json.Str(f, "summary"));
                    break;
                }
                case "result":
                    RunActive = false;
                    break;
                case "run_started":
                    RunActive = true;
                    break;
                case "error":
                {
                    // Only after the welcome, so in the words of a brain that has proved itself. The same invite
                    // wouldn't do better a second time.
                    Problem = Json.Str(m, "message");
                    Status(Problem);
                    WantConnected = false;
                    Invites?.Refuse();
                    SessionEnded?.Invoke();
                    if (ExitOnFailure) Host.Exit(1);
                    break;
                }
            }
        }

        bool Proved(Dictionary<string, object> welcome, out byte[] brainNonce)
        {
            brainNonce = Bytes(welcome, "nonce");
            var proof = Bytes(welcome, "proof");
            return brainNonce != null && brainNonce.Length == Handshake.NonceBytes && proof != null && secret != null
                   && Handshake.Verify(secret, Handshake.BrainRole, pluginNonce, brainNonce, proof);
        }

        static byte[] Bytes(Dictionary<string, object> frame, string key)
        {
            try
            {
                return Convert.FromBase64String(Json.Str(frame, key));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        void Welcomed(Dictionary<string, object> welcome, byte[] brainNonce)
        {
            var proof = Handshake.Prove(secret, Handshake.PluginRole, pluginNonce, brainNonce);
            Put(Json.Write(new Dictionary<string, object> { ["type"] = "proof", ["proof"] = Convert.ToBase64String(proof) }));
            secret = null;
            pluginNonce = null;
            handshakeDeadline = 0;
            var session = Json.Str(welcome, "session");
            if (string.IsNullOrEmpty(askedResume) || session != askedResume)
            {
                // The brain counts this session's requests from 1 again: an answer or a count kept from the one before
                // would be taken for one of them. No recording or held key goes on either.
                lastRequest = 0;
                ++SessionSerial;
                heldResponses.Clear();
                pending.Clear();
                RunActive = false;
                Hands.ReleaseAll();
                Media.Stop();
                SessionEnded?.Invoke();
            }
            askedResume = null;
            if (!string.IsNullOrEmpty(session)) SessionId = session;
            resumeSession = null;
            welcomed = true;
            backoff = 1.0;
            Problem = "";
            SetConnected(true);
            // (only now, for a brain that has proved itself: the devices, as these held answers)
            Hands.EnsureDevices();
            foreach (var r in heldResponses) Put(r);
            heldResponses.Clear();
            Status("Connected to Cluewren");
        }

        // The code comes from a peer that has proved nothing: only a short plain word of it is ever shown.
        static string RefusalCode(object given)
        {
            if (!(given is string code) || code.Length == 0 || code.Length > 32) return null;
            foreach (var c in code)
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_'))
                    return null;
            return code;
        }

        static string Refusal(string code)
        {
            switch (code)
            {
                case "protocol": return Discovery.OtherRelease;
                case "not_authenticated": return "The brain did not accept this editor's proof of the invite";
                case "project": return "The run is for another project than this editor's";
                case "resume": return "The session this editor tried to resume belongs to another project or engine";
                default: return $"The brain refused the connection ({code})";
            }
        }

        void NoProof() =>
            Refuse($"Refused {Url}: it didn't prove it is this machine's brain", WebSocketCloseStatus.PolicyViolation, "bad proof");

        // No reconnect until the invite changes: the same invite would be refused the same way.
        void Refuse(string why, WebSocketCloseStatus status, string closing)
        {
            WantConnected = false;
            handshakeDeadline = 0;
            secret = null;
            pluginNonce = null;
            resumeSession = null;
            Media.Stop();
            Problem = why;
            lastFailure = why;
            Invites?.Refuse();
            CloseSocket(status, closing);
            SessionEnded?.Invoke();
            Status(Problem);
            if (ExitOnFailure) Host.Exit(1);
        }

        // -- the command line --------------------------------------------------------------------------------

        /// <summary>-Name=value (or -Name value), quotes taken off.</summary>
        public static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var prefix = "-" + name + "=";
            for (var i = 0; i < args.Length; ++i)
            {
                var a = args[i];
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(prefix.Length).Trim('"');
                if (string.Equals(a, "-" + name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    return args[i + 1].Trim('"');
            }
            return "";
        }

        public static bool Flag(string name)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-" + name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>What the editor keeps across a script reload.</summary>
        public Dictionary<string, object> SaveState()
        {
            return new Dictionary<string, object>
            {
                ["want"] = WantConnected,
                ["session"] = SessionId ?? "",
                ["run_active"] = RunActive,
                ["exit_on_failure"] = ExitOnFailure,
                ["connect_timeout"] = ConnectTimeout,
                ["connect_since"] = connectSince,
                ["welcomed"] = welcomed,
                ["received"] = lastRequest,
                ["record"] = Media.SaveState(),
            };
        }

        public void Shutdown()
        {
            CloseSocket(WebSocketCloseStatus.NormalClosure, "shutdown");
            Connected = false;
            WantConnected = false;
            if (Current == this) Current = null;
        }
    }
}
