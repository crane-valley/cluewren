// Modified by Crane Valley: names renamed from Autopilot to Cluewren; the picture is scaled and flipped on the GPU, so
// only the size sent is read back.
// Recording and stills of what the player sees (docs/protocol.md, "Recording, stills and usage"): `record` start/stop
// sends `frame` messages while the game plays, `capture` answers one still. The picture is the game's view with its UI
// (the editor's Game view, a player's screen), taken at the end of a frame with ScreenCapture into a render texture.
//
// Capture never makes the game wait: the picture is scaled to the size sent on the GPU, read back with
// AsyncGPUReadback (the game thread never waits on the GPU), and JPEG encoding (ImageConversion.EncodeArrayToJPG is
// thread safe), base64 and the message's JSON are done on a worker thread. A frame whose time comes while the one
// before is still being read or encoded is skipped: its number is used (a gap in `n`).
//
// A recording lasts while the brain wants it on this connection: it pauses while no game runs (play mode stopped) or
// the game is paused, and goes on by itself when the game runs again; a new connection (a hello without `resume`)
// ends it. Across the editor's script reload (the 1012 resume) it is kept with the client's state.

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
using Unity.Collections;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#endif

namespace Cluewren
{
    public static class Media
    {
        /// <summary>Whether this build can capture at all (the Screen Capture and Image Conversion modules are in).
        /// The hello's features say "media" then; with no picture at run time the ops answer 409.</summary>
        public static bool Compiled
        {
            get
            {
#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
                return true;
#else
                return false;
#endif
            }
        }

        sealed class Job
        {
            public bool Still;
            public long Id;       // a still: the request it answers
            public long N;        // a recording's frame: its number
            public int RecGen;
            public int ConnGen;
            public int MaxDim;
            public int Quality;
            public double Started;
            public double T;
            public double Wall;
            public int W;
            public int H;
            public int State;     // a still: 0 until answered (Interlocked)
        }

        static readonly object gate = new object();
        static bool want;
        static double fps = 5;
        static int maxDim = 960;
        static int quality = 75;
        static long nextN;
        static long sent;
        static int recGen;
#pragma warning disable 0414  // (read only where the build can capture)
        static double nextDue;
#pragma warning restore 0414
        static Job current;        // the recording's frame being read or encoded
        static byte[] readBuffer;  // reused: the recording's read back picture
#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
        static RenderTexture recTexture;
        static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();
#endif

        public static bool Recording
        {
            get
            {
                lock (gate) return want;
            }
        }

        static bool EditorPaused
        {
            get
            {
#if UNITY_EDITOR
                return UnityEditor.EditorApplication.isPaused;
#else
                return false;
#endif
            }
        }

        static double WallNow => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

        /// <summary>Why there is no picture to capture now, or null when there is one.</summary>
        public static string NoPicture(PlaytestClient client)
        {
            if (!Compiled) return "this build has no Screen Capture or Image Conversion module";
            if (!View.Rendering) return "the game runs without graphics (-nographics)";
            if (!Application.isPlaying || !client.Host.SessionRunning) return "no game is running";
            if (!SystemInfo.supportsAsyncGPUReadback) return "this graphics device can't read the picture back without stalling the game";
            return null;
        }

        // -- the ops ------------------------------------------------------------------------------------------

        /// <summary>record {"action": "start", "fps", "max_dim", "quality"} | {"action": "stop"}.</summary>
        public static void Record(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var action = Json.Str(args, "action", "start");
            if (action == "stop")
            {
                client.Respond(id, new Dictionary<string, object> { ["recording"] = false, ["frames"] = Stop() });
                return;
            }
            if (action != "start")
            {
                client.RespondError(id, $"record: unknown action {action} (start or stop)");
                return;
            }
            var why = NoPicture(client);
            if (why != null)
            {
                client.RespondError(id, "no picture: " + why, 409);
                return;
            }
            int dim;
            lock (gate)
            {
                want = true;
                fps = Clamp(Json.Num(args, "fps", 5), 0.05, 60);
                maxDim = (int)Clamp(Json.Num(args, "max_dim", 960), 16, 8192);
                quality = (int)Clamp(Json.Num(args, "quality", 75), 1, 100);
                nextN = 0;
                sent = 0;
                ++recGen;
                current = null;
                nextDue = 0;
                dim = maxDim;
            }
            FitSize(Screen.width, Screen.height, dim, out var w, out var h);
            client.Respond(id, new Dictionary<string, object> { ["recording"] = true, ["width"] = w, ["height"] = h });
        }

        /// <summary>capture {"max_dim": 1920, "quality": 90}: one still, answered once it is encoded.</summary>
        public static void Capture(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var why = NoPicture(client) ?? (EditorPaused ? "the game is paused in the editor" : null);
            var host = why == null ? Bootstrap.CoroutineHost : null;
            if (why == null && host == null) why = "no game is running";
            if (why != null)
            {
                client.RespondError(id, "no picture: " + why, 409);
                return;
            }
#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
            var job = new Job
            {
                Still = true,
                Id = id,
                MaxDim = (int)Clamp(Json.Num(args, "max_dim", 1920), 16, 8192),
                Quality = (int)Clamp(Json.Num(args, "quality", 90), 1, 100),
                Started = PlaytestClient.Now,
                ConnGen = client.ConnectionGeneration,
            };
            host.StartCoroutine(Grab(client, job));
            // (answered by the worker; this only answers when the picture never came: the game stopped first)
            client.AddPending(new Pending
            {
                Id = id,
                Op = "capture",
                Seconds = 10,
                Tick = (p, now) =>
                {
                    if (Volatile.Read(ref job.State) != 0) return true;
                    if (now - p.Start <= p.Seconds) return false;
                    if (Interlocked.CompareExchange(ref job.State, 1, 0) == 0)
                        client.RespondError(p.Id, "no picture: the game stopped before the picture was taken", 409);
                    return true;
                },
            });
#endif
        }

        /// <summary>End the recording (the brain's stop, the connection gone). Returns the frames it sent.</summary>
        public static long Stop()
        {
            long frames;
            lock (gate)
            {
                frames = sent;
                want = false;
                ++recGen;
                current = null;
            }
            ReleaseTexture();
            return frames;
        }

        /// <summary>What the editor keeps across a script reload (null: not recording).</summary>
        public static Dictionary<string, object> SaveState()
        {
            lock (gate)
            {
                if (!want) return null;
                return new Dictionary<string, object>
                {
                    ["fps"] = fps, ["max_dim"] = maxDim, ["quality"] = quality, ["n"] = nextN, ["sent"] = sent,
                };
            }
        }

        public static void RestoreState(Dictionary<string, object> s)
        {
            if (s == null) return;
            lock (gate)
            {
                want = true;
                fps = Clamp(Json.Num(s, "fps", 5), 0.05, 60);
                maxDim = (int)Clamp(Json.Num(s, "max_dim", 960), 16, 8192);
                quality = (int)Clamp(Json.Num(s, "quality", 75), 1, 100);
                nextN = (long)Json.Num(s, "n", 0);
                sent = (long)Json.Num(s, "sent", 0);
                ++recGen;
                current = null;
                nextDue = 0;
            }
        }

        // -- every frame ----------------------------------------------------------------------------------------

        /// <summary>The recording's next frame when its time has come (main thread, at the start of the frame; the
        /// picture is taken at its end).</summary>
        public static void Tick(PlaytestClient client)
        {
            if (!want)
            {
                return;
            }
            if (!Application.isPlaying)
            {
                lock (gate) current = null;  // (the game stopped: a frame being taken is gone with it)
                ReleaseTexture();
                return;
            }
            if (!View.Rendering || !Compiled || !client.Host.SessionRunning || Ops.IsPaused || EditorPaused || !SystemInfo.supportsAsyncGPUReadback)
                return;
#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
            var now = PlaytestClient.Now;
            Job job;
            lock (gate)
            {
                if (current != null && now - current.Started > 5) current = null;  // (lost: never read back)
                if (now < nextDue) return;
                var period = 1.0 / fps;
                nextDue = nextDue <= 0 || now - nextDue > period ? now + period : nextDue + period;
                var number = nextN++;
                if (current != null) return;  // the one before is still being read or encoded: skipped (a gap in n)
                job = new Job
                {
                    N = number, RecGen = recGen, ConnGen = client.ConnectionGeneration, MaxDim = maxDim, Quality = quality,
                    Started = now,
                };
                current = job;
            }
            var host = Bootstrap.CoroutineHost;
            if (host == null)
            {
                lock (gate)
                    if (current == job)
                        current = null;
                return;
            }
            host.StartCoroutine(Grab(client, job));
#endif
        }

        static bool Live(Job job)
        {
            if (job.Still) return Volatile.Read(ref job.State) == 0;
            lock (gate) return want && current == job && job.RecGen == recGen;
        }

        static void Done(Job job)
        {
            if (job.Still) return;
            lock (gate)
                if (current == job)
                    current = null;
        }

        static void ReleaseTexture()
        {
#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
            if (recTexture == null) return;
            recTexture.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(recTexture);
            else UnityEngine.Object.DestroyImmediate(recTexture);
            recTexture = null;
#endif
        }

        /// <summary>A still that can't be had: its error answer (once), from any thread.</summary>
        static void FailStill(PlaytestClient client, Job job, int status, string error)
        {
            if (!job.Still || Interlocked.CompareExchange(ref job.State, 1, 0) != 0) return;
            MainThread.Post(() => client.RespondError(job.Id, error, status));
        }

#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
        static IEnumerator Grab(PlaytestClient client, Job job)
        {
            yield return EndOfFrame;
            if (!Live(job)) yield break;
            var w = Screen.width;
            var h = Screen.height;
            if (w < 1 || h < 1)
            {
                FailStill(client, job, 409, "no picture: the game's view has no size");
                Done(job);
                yield break;
            }
            RenderTexture screen;
            if (job.Still)
            {
                screen = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            }
            else
            {
                if (recTexture == null || recTexture.width != w || recTexture.height != h)
                {
                    ReleaseTexture();
                    recTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
                    {
                        name = "Cluewren recording", hideFlags = HideFlags.HideAndDontSave,
                    };
                    recTexture.Create();
                }
                screen = recTexture;
            }
            RenderTexture picture;
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(screen);
                FitSize(w, h, job.MaxDim, out job.W, out job.H);
                // (where the graphics API's textures start at the top, the screen comes into the texture upside down)
                picture = Shrink(screen, job.W, job.H, SystemInfo.graphicsUVStartsAtTop);
            }
            catch (Exception e)
            {
                FailStill(client, job, 500, "capture failed: " + e.Message);
                Done(job);
                yield break;
            }
            finally
            {
                if (job.Still) RenderTexture.ReleaseTemporary(screen);
            }
            job.T = Time.timeAsDouble;
            job.Wall = WallNow;
            AsyncGPUReadback.Request(picture, 0, TextureFormat.RGBA32, request => OnReadback(client, job, request, picture));
        }

        static RenderTexture Shrink(RenderTexture screen, int w, int h, bool flip)
        {
            var active = RenderTexture.active;
            var from = screen;
            try
            {
                // Filtering an sRGB texture averages in linear light, which brightens fine detail; the picture is
                // averaged as its bytes are stored, so they are copied to a texture that isn't sRGB first.
                if (screen.sRGB && SystemInfo.copyTextureSupport != CopyTextureSupport.None)
                {
                    from = Unencoded(screen.width, screen.height);
                    Graphics.CopyTexture(screen, from);
                }
                // Halving with bilinear filtering averages each 2 x 2 block; one blit to a far smaller size would skip
                // most of the screen's pixels.
                while (from.width / 2 > w && from.height / 2 > h)
                {
                    var half = Unencoded(from.width / 2, from.height / 2);
                    Graphics.Blit(from, half);
                    if (from != screen) RenderTexture.ReleaseTemporary(from);
                    from = half;
                }
                var picture = Unencoded(w, h);
                if (flip) Graphics.Blit(from, picture, new Vector2(1, -1), new Vector2(0, 1));
                else Graphics.Blit(from, picture);
                return picture;
            }
            finally
            {
                if (from != screen) RenderTexture.ReleaseTemporary(from);
                RenderTexture.active = active;
            }
        }

        static RenderTexture Unencoded(int w, int h) =>
            RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

        static void OnReadback(PlaytestClient client, Job job, AsyncGPUReadbackRequest request, RenderTexture picture)
        {
            RenderTexture.ReleaseTemporary(picture);
            if (!Live(job)) return;
            if (request.hasError)
            {
                FailStill(client, job, 500, "capture failed: the picture couldn't be read back from the GPU");
                Done(job);
                return;
            }
            var data = request.GetData<byte>();
            var buffer = job.Still ? null : Interlocked.Exchange(ref readBuffer, null);
            if (buffer == null || buffer.Length != data.Length) buffer = new byte[data.Length];
            NativeArray<byte>.Copy(data, buffer, data.Length);
            Task.Run(() => Encode(client, job, buffer));
        }

        /// <summary>Worker thread: encode, base64, the message's JSON, sent.</summary>
        static void Encode(PlaytestClient client, Job job, byte[] pixels)
        {
            try
            {
                var jpeg = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)job.W, (uint)job.H, 0, job.Quality);
                if (job.Still)
                {
                    if (Interlocked.CompareExchange(ref job.State, 1, 0) == 0)
                        client.SendResponseText(StillJson(job.Id, job.W, job.H, job.T, Convert.ToBase64String(jpeg)));
                    return;
                }
                var message = FrameMessage(job.N, job.T, job.Wall, job.W, job.H, jpeg);
                lock (gate)
                {
                    if (want && current == job && job.RecGen == recGen && client.SendIfConnection(message, job.ConnGen))
                    {
                        ++sent;
                        message = default;
                    }
                }
                if (message.Bytes != null) ArrayPool<byte>.Shared.Return(message.Bytes);
            }
            catch (Exception e)
            {
                FailStill(client, job, 500, "capture failed: " + e.Message);
                if (!job.Still) MainThread.Post(() => Debug.LogWarning("[Cluewren] A recording frame failed: " + e.Message));
            }
            finally
            {
                if (!job.Still)
                {
                    readBuffer = pixels;
                    Done(job);
                }
            }
        }
#endif

        // -- pure helpers (tested) -------------------------------------------------------------------------------

        public static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : v > hi ? hi : v;
        }

        /// <summary>The size a picture is sent at: the longer side at most maxDim (never scaled up).</summary>
        public static void FitSize(int w, int h, int maxDim, out int dw, out int dh)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            var longer = Math.Max(w, h);
            if (maxDim <= 0 || longer <= maxDim)
            {
                dw = w;
                dh = h;
                return;
            }
            var scale = (double)maxDim / longer;
            dw = Math.Max(1, (int)Math.Round(w * scale));
            dh = Math.Max(1, (int)Math.Round(h * scale));
        }

        static string Num(double v) => Json.Round(v, 3).ToString("R", CultureInfo.InvariantCulture);

        /// <summary>A `frame` message, in a pooled buffer the send loop gives back (base64 needs no escaping). As strings
        /// a picture would be allocated about ten times over, every frame, and the GC stops the game's thread for it.</summary>
        public static Outgoing FrameMessage(long n, double t, double wall, int w, int h, byte[] jpeg)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"type\":\"frame\",\"n\":").Append(n.ToString(CultureInfo.InvariantCulture))
                .Append(",\"t\":").Append(Num(t))
                .Append(",\"wall\":").Append(Num(wall))
                .Append(",\"width\":").Append(w.ToString(CultureInfo.InvariantCulture))
                .Append(",\"height\":").Append(h.ToString(CultureInfo.InvariantCulture))
                .Append(",\"jpeg\":\"");
            var head = sb.ToString();
            var longest = (jpeg.Length + 2) / 3 * 4;
            var chars = ArrayPool<char>.Shared.Rent(longest);
            var bytes = ArrayPool<byte>.Shared.Rent(head.Length + longest + 2);
            var count = Convert.ToBase64CharArray(jpeg, 0, jpeg.Length, chars, 0);
            var length = Encoding.ASCII.GetBytes(head, 0, head.Length, bytes, 0);
            length += Encoding.ASCII.GetBytes(chars, 0, count, bytes, length);
            ArrayPool<char>.Shared.Return(chars);
            bytes[length++] = (byte)'"';
            bytes[length++] = (byte)'}';
            return new Outgoing(bytes, length);
        }

        /// <summary>The `capture` answer.</summary>
        public static string StillJson(long id, int w, int h, double t, string jpegBase64)
        {
            var sb = new StringBuilder(jpegBase64.Length + 200);
            sb.Append("{\"type\":\"response\",\"id\":").Append(id.ToString(CultureInfo.InvariantCulture))
                .Append(",\"ok\":true,\"result\":{\"width\":").Append(w.ToString(CultureInfo.InvariantCulture))
                .Append(",\"height\":").Append(h.ToString(CultureInfo.InvariantCulture))
                .Append(",\"t\":").Append(Num(t))
                .Append(",\"jpeg\":\"").Append(jpegBase64).Append("\"},\"t\":").Append(Num(t)).Append('}');
            return sb.ToString();
        }
    }
}
