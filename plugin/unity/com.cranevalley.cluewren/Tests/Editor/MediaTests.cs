// Modified by Crane Valley: names renamed from Autopilot to Cluewren; scaling and flipping moved to the GPU, so their
// tests went with Media.Fit.
// Recording's pure parts: the size pictures are sent at, the messages' JSON, and JPEG encoding on a worker thread (the
// picture itself, scaled and flipped on the GPU, needs a rendered game: not here).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Cluewren.Tests
{
    public class MediaTests
    {
        [Test]
        public void FitSizeNeverScalesUp()
        {
            Media.FitSize(1920, 1080, 960, out var w, out var h);
            Assert.AreEqual((960, 540), (w, h));
            Media.FitSize(1080, 1920, 960, out w, out h);
            Assert.AreEqual((540, 960), (w, h));
            Media.FitSize(800, 600, 960, out w, out h);
            Assert.AreEqual((800, 600), (w, h));
            Media.FitSize(3000, 7, 960, out w, out h);
            Assert.AreEqual((960, 2), (w, h));
            Media.FitSize(0, 0, 960, out w, out h);
            Assert.AreEqual((1, 1), (w, h));
        }

        static byte[] Picture(int w, int h, Func<int, int, byte> value)
        {
            var p = new byte[w * h * 4];
            for (var y = 0; y < h; ++y)
            for (var x = 0; x < w; ++x)
            {
                var i = (y * w + x) * 4;
                p[i] = value(x, y);
                p[i + 1] = (byte)(255 - value(x, y));
                p[i + 2] = 7;
                p[i + 3] = 0;
            }
            return p;
        }

        [Test]
        public void MessagesAreJson()
        {
            var message = Media.FrameMessage(17, 12.3456, 1790000000.1234, 960, 540, new byte[] { 65, 66, 67 });
            var frame = Json.ParseObject(System.Text.Encoding.ASCII.GetString(message.Bytes, 0, message.Length));
            Assert.AreEqual("frame", frame["type"]);
            Assert.AreEqual(17L, frame["n"]);
            Assert.AreEqual(12.346, Json.Num(frame, "t", 0), 1e-9);
            Assert.AreEqual(1790000000.123, Json.Num(frame, "wall", 0), 1e-6);
            Assert.AreEqual(960L, frame["width"]);
            Assert.AreEqual(540L, frame["height"]);
            Assert.AreEqual("QUJD", frame["jpeg"]);

            var still = Json.ParseObject(Media.StillJson(5, 1920, 1080, 3.5, "QUJD"));
            Assert.AreEqual("response", still["type"]);
            Assert.AreEqual(5L, still["id"]);
            Assert.AreEqual(true, still["ok"]);
            var result = Json.Obj(still, "result");
            Assert.AreEqual(1920L, result["width"]);
            Assert.AreEqual("QUJD", result["jpeg"]);
            Assert.AreEqual(3.5, Json.Num(result, "t", 0));
        }

        [Test]
        public void ClampKeepsArgsInRange()
        {
            Assert.AreEqual(1, Media.Clamp(-3, 1, 100));
            Assert.AreEqual(100, Media.Clamp(400, 1, 100));
            Assert.AreEqual(1, Media.Clamp(double.NaN, 1, 100));
            Assert.AreEqual(75, Media.Clamp(75, 1, 100));
        }

#if CLUEWREN_SCREENCAPTURE && CLUEWREN_IMAGECONVERSION
        [Test]
        public void JpegEncodesOnAWorkerThread()
        {
            Assert.IsTrue(Media.Compiled);
            var src = Picture(32, 18, (x, y) => (byte)(x * 8));
            var jpeg = Task.Run(() =>
                UnityEngine.ImageConversion.EncodeArrayToJPG(src, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, 32, 18, 0, 75)).Result;
            Assert.Greater(jpeg.Length, 100);
            Assert.AreEqual(0xFF, jpeg[0]);
            Assert.AreEqual(0xD8, jpeg[1]);
        }
#endif
    }
}
