// The loaded objects of a type, on every Unity 6 the package supports. FindObjectsByType<T>(FindObjectsInactive) came
// with 6000.4, which also made the overloads with a FindObjectsSortMode obsolete (a warning); before it only those
// exist. Neither sorts here: no caller depends on the order.

using UnityEngine;

namespace Autopilot.Playtest
{
    public static class Loaded
    {
        public static T[] All<T>(FindObjectsInactive inactive) where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            return Object.FindObjectsByType<T>(inactive);
#else
            return Object.FindObjectsByType<T>(inactive, FindObjectsSortMode.None);
#endif
        }
    }
}
