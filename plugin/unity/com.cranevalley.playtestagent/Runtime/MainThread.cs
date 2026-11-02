// Modified by Crane Valley: moved from Runtime/Codex.cs, which is removed.

using System;
using System.Collections.Concurrent;

namespace Autopilot.Playtest
{
    /// <summary>Work for the main thread, posted from others (run at the next Tick).</summary>
    public static class MainThread
    {
        static readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();

        public static void Post(Action a)
        {
            queue.Enqueue(a);
        }

        public static void Run()
        {
            while (queue.TryDequeue(out var a))
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning("[Autopilot] " + e);
                }
            }
        }
    }
}
