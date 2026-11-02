// Modified by Crane Valley: names renamed from Autopilot to Cluewren.
// view: what the player sees (simpler than Unreal's; see the README for what it leaves out) and watch: how things move.
// A "thing" is a GameObject as a player tells things apart: a renderer's object, taken up through its parents to the
// highest one that isn't a plain group (an object with nothing but a Transform, like "Pickups" or "Environment").

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cluewren
{
    public static class View
    {
        /// <summary>A plain group: nothing but a Transform, at the top or in another plain group ("Level", "Pickups").
        /// An empty object inside a thing (a camera pivot, a hand socket) is part of that thing.</summary>
        public static bool IsGroup(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.gameObject.GetComponentCount() > 1)
                    return false;
            return true;
        }

        static readonly Plane[] planes = new Plane[6];
        static readonly Dictionary<GameObject, bool> asked = new Dictionary<GameObject, bool>();

        public static GameObject ThingOf(GameObject go)
        {
            var t = go.transform;
            while (t.parent != null && !IsGroup(t.parent)) t = t.parent;
            return t.gameObject;
        }

        static string ClassOf(GameObject thing)
        {
            foreach (var mb in thing.GetComponents<MonoBehaviour>())
                if (mb != null && Reflect.IsGameType(mb.GetType()))
                    return mb.GetType().Name;
            foreach (var mb in thing.GetComponentsInChildren<MonoBehaviour>())
                if (mb != null && Reflect.IsGameType(mb.GetType()))
                    return mb.GetType().Name;
            var r = thing.GetComponentInChildren<Renderer>();
            return r != null ? r.GetType().Name : "GameObject";
        }

        public static bool Rendering => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        sealed class Seen
        {
            public GameObject Thing;
            public Bounds Box;
            public List<Renderer> Parts = new List<Renderer>();
            public float Distance;
        }

        public static Dictionary<string, object> Look(Dictionary<string, object> args)
        {
            var player = Player.Find();
            var cam = Player.Eyes(player);
            if (cam == null) throw new RcError("No camera");
            var maxDistance = (float)(Json.Num(args, "max_distance", 5000) / Frame.Scale);
            var maxCount = (int)Json.Num(args, "max_count", 24);
            var onlyList = Json.List(args, "only");
            var only = onlyList != null ? new HashSet<string>(onlyList.Select(o => Convert.ToString(o))) : null;
            var camPos = cam.transform.position;
            GeometryUtility.CalculateFrustumPlanes(cam, planes);
            Vector3 feet = player != null ? Player.Center(player, out var hh, out _) - Vector3.up * hh : camPos;
            GameObject standingOn = null;
            Vector3 floorNormal = Vector3.up;
#if CLUEWREN_PHYSICS
            if (player != null)
            {
                var floor = Player.StandingOn(player, out floorNormal, out _);
                if (floor != null) standingOn = ThingOf(floor.gameObject);
            }
#endif
            // The things with a visible body
            var things = new Dictionary<GameObject, Seen>();
            asked.Clear();
            foreach (var r in Loaded.All<Renderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) continue;
                if (Ops.IsParticles(r) || r is TrailRenderer || r is LineRenderer) continue;
                if (Paths.Hidden(r.gameObject) || r.gameObject.layer == 5 /* UI */) continue;
                if (player != null && r.transform.IsChildOf(player.transform)) continue;
                var thing = ThingOf(r.gameObject);
                if (only != null)
                {
                    if (!asked.TryGetValue(thing, out var wanted))
                        asked[thing] = wanted = only.Contains(Paths.NameOf(thing)) || only.Contains(Paths.Of(thing));
                    if (!wanted) continue;
                }
                if (!things.TryGetValue(thing, out var seen))
                {
                    seen = new Seen { Thing = thing, Box = r.bounds };
                    things[thing] = seen;
                }
                else seen.Box.Encapsulate(r.bounds);
                seen.Parts.Add(r);
            }
            var visible = new List<Seen>();
            foreach (var s in things.Values)
            {
                if (s.Thing == standingOn && only == null) continue;  // (the ground under the feet)
                s.Distance = Mathf.Sqrt(s.Box.SqrDistance(camPos));
                if (s.Distance > maxDistance) continue;
                if (!GeometryUtility.TestPlanesAABB(planes, s.Box)) continue;
                if (!InSight(camPos, s, player)) continue;
                visible.Add(s);
            }
            visible.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (visible.Count > maxCount) visible.RemoveRange(maxCount, visible.Count - maxCount);
            // The crosshair: the nearest thing along the view's line, by its drawn box (a pickup is often a trigger,
            // which physics rays pass through), not past a solid collider nearer than it
            GameObject crosshair = null;
            var ray = new Ray(camPos, cam.transform.forward);
            var nearest = maxDistance;
#if CLUEWREN_PHYSICS
            GameObject hitFirst = null;
            for (var i = RayHits(ray.origin, ray.direction, maxDistance) - 1; i >= 0; --i)
            {
                var h = hits[i];
                if (h.distance > nearest && hitFirst != null) continue;
                if (player != null && h.collider.transform.IsChildOf(player.transform)) continue;
                hitFirst = h.collider.gameObject;
                nearest = h.distance;
            }
            if (hitFirst != null) crosshair = ThingOf(hitFirst);
#endif
            foreach (var s in things.Values)
            {
                if (s.Thing == standingOn || s.Box.Contains(camPos)) continue;
                if (s.Box.IntersectRay(ray, out var d) && d < nearest)
                {
                    nearest = d;
                    crosshair = s.Thing;
                }
            }
            var list = new List<object>();
            foreach (var s in visible)
            {
                var vp = cam.WorldToViewportPoint(s.Box.center);
                var item = new Dictionary<string, object>
                {
                    ["name"] = Paths.NameOf(s.Thing),
                    ["path"] = Paths.Of(s.Thing),
                    ["class"] = ClassOf(s.Thing),
                    ["center"] = Frame.Point(s.Box.center, 0),
                    ["size"] = Frame.Size(s.Box.size, 0),
                    ["yaw"] = Json.Round(Frame.Yaw(s.Thing.transform.rotation), 1),
                    ["distance"] = Math.Round(s.Distance * Frame.Scale),
                    ["screen_x"] = Json.Round((vp.x - 0.5) * 2, 3),
                    ["screen_y"] = Json.Round((vp.y - 0.5) * 2, 3),
                    ["top_above_feet"] = Math.Round((s.Box.max.y - feet.y) * Frame.Scale),
                    ["bottom_above_feet"] = Math.Round((s.Box.min.y - feet.y) * Frame.Scale),
                    ["crosshair_on"] = crosshair == s.Thing,
                };
                var mf = s.Thing.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) item["mesh"] = mf.sharedMesh.name;
                if (vp.x < 0 || vp.x > 1 || vp.y < 0 || vp.y > 1) item["partly_in_view"] = true;
#if CLUEWREN_PHYSICS
                var rb = s.Thing.GetComponentInParent<Rigidbody>();
                if (rb != null && !rb.isKinematic && rb.linearVelocity.magnitude > 0.01f) item["speed"] = Math.Round(rb.linearVelocity.magnitude * Frame.Scale);
                if (player != null)
                {
                    var blocks = s.Thing.GetComponentsInChildren<Collider>().Any(c => c.enabled && !c.isTrigger);
                    item["blocks_player"] = blocks;
                }
#endif
                var look = Appearance(s);
                if (look.Count > 0) item["look"] = look;
                list.Add(item);
            }
            var rot = cam.transform.rotation;
            var result = new Dictionary<string, object>
            {
                ["visible"] = list,
                ["scene"] = new Dictionary<string, object>(),
                ["camera"] = new Dictionary<string, object>
                {
                    ["location"] = Frame.Point(camPos, 0),
                    ["yaw"] = Json.Round(Frame.Yaw(rot), 2),
                    ["pitch"] = Json.Round(Frame.Pitch(rot), 2),
                    ["fov"] = Math.Round(Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect > 0 ? cam.aspect : 16f / 9f)),
                },
                ["crosshair_tested"] = true,
                ["oriented"] = false,  // (boxes are world-aligned)
                ["effects_play"] = Rendering,
                ["omitted"] = new List<object> { "held", "openings", "ways_up", "point_blank", "lane", "top_slope", "lights", "effects", "highlighted", "overhead" },
            };
            if (only != null) result["only"] = only.Select(o => (object)o).ToList();
            var body = new Dictionary<string, object>();
            if (player != null)
            {
                var grounded = Player.Grounded(player, out var velocity);
                body["velocity"] = Frame.Point(velocity, 0);
                body["feet_z"] = Math.Round(feet.y * Frame.Scale);
                body["on_ground"] = grounded ?? false;
                body["falling"] = grounded == false && velocity.y < -0.05f;
                body["crouched"] = false;
                if (standingOn != null)
                {
                    body["standing_on"] = Paths.NameOf(standingOn);
                    body["floor_slope_degrees"] = Math.Round(Vector3.Angle(floorNormal, Vector3.up));
                }
#if CLUEWREN_PHYSICS
                if (only == null) body["touching"] = Touching(player, Frame.Yaw(rot));
                if (only == null && Json.Bool(args, "sonar", false))
                {
                    var sonar = Sonar(player, args, Frame.Yaw(rot));
                    if (sonar != null) body["sonar"] = sonar;
                }
#endif
            }
            result["body"] = body;
            return result;
        }

#if CLUEWREN_PHYSICS
        static RaycastHit[] hits = new RaycastHit[64];
        static readonly Vector3[] sightTargets = new Vector3[3];

        static int RayHits(Vector3 from, Vector3 direction, float distance)
        {
            var count = Physics.RaycastNonAlloc(from, direction, hits, distance, ~0, QueryTriggerInteraction.Ignore);
            while (count == hits.Length)
            {
                hits = new RaycastHit[count * 2];
                count = Physics.RaycastNonAlloc(from, direction, hits, distance, ~0, QueryTriggerInteraction.Ignore);
            }
            return count;
        }
#endif

        static bool InSight(Vector3 eye, Seen s, GameObject player)
        {
#if CLUEWREN_PHYSICS
            sightTargets[0] = s.Box.center;
            sightTargets[1] = new Vector3(s.Box.center.x, s.Box.max.y - 0.01f, s.Box.center.z);
            sightTargets[2] = s.Box.ClosestPoint(eye);
            foreach (var target in sightTargets)
            {
                var dir = target - eye;
                var dist = dir.magnitude;
                if (dist < 0.01f) return true;
                var blocked = false;
                var count = RayHits(eye, dir / dist, dist - 0.02f);
                for (var i = 0; i < count; ++i)
                {
                    var h = hits[i];
                    if (player != null && h.collider.transform.IsChildOf(player.transform)) continue;
                    if (h.collider.transform.IsChildOf(s.Thing.transform)) continue;
                    blocked = true;
                    break;
                }
                if (!blocked) return true;
            }
            return false;
#else
            return true;
#endif
        }

        static Dictionary<string, object> Appearance(Seen s)
        {
            var look = new Dictionary<string, object>();
            var parts = new SortedSet<string>();
            foreach (var r in s.Parts.OrderByDescending(r => r.bounds.size.sqrMagnitude).Take(8))
            {
                var mf = r.GetComponent<MeshFilter>();
                var name = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : r is SkinnedMeshRenderer sk && sk.sharedMesh != null ? sk.sharedMesh.name : r is SpriteRenderer sr && sr.sprite != null ? sr.sprite.name : null;
                if (!string.IsNullOrEmpty(name)) parts.Add(name);
            }
            if (parts.Count > 0) look["parts"] = parts.Select(p => (object)p).ToList();
            var texts = new List<object>();
            foreach (var tm in s.Thing.GetComponentsInChildren<TextMesh>())
                if (texts.Count < 4 && !string.IsNullOrWhiteSpace(tm.text)) texts.Add(Cut(tm.text));
#if CLUEWREN_TMP
            foreach (var t in s.Thing.GetComponentsInChildren<TMPro.TMP_Text>())
                if (texts.Count < 4 && t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text)) texts.Add(Cut(t.text));
#endif
#if CLUEWREN_UGUI
            foreach (var t in s.Thing.GetComponentsInChildren<UnityEngine.UI.Text>())
                if (texts.Count < 4 && t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text)) texts.Add(Cut(t.text));
#endif
            if (texts.Count > 0) look["texts"] = texts;
            var animator = s.Thing.GetComponentInChildren<Animator>();
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                var clips = animator.GetCurrentAnimatorClipInfo(0);
                if (clips.Length > 0 && clips[0].clip != null) look["animation"] = clips[0].clip.name;
            }
            return look;
        }

        static string Cut(string s)
        {
            s = s.Trim();
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }

#if CLUEWREN_PHYSICS
        /// <summary>body.sonar (docs/protocol.md, view with sonar): how far the player's body could walk each way
        /// before something stops it, felt out with its own capsule (swept a step above the floor, a little thinner) and
        /// rays down onto the floor; never navigation data. ways: [{"turn" (right of the view), "clear" (cm across the
        /// floor), "by": wall|drop|steep|reach, "thing"?, "normal"? {X,Y}, "drop"? (cm), "rise"? (cm)}].</summary>
        static Dictionary<string, object> Sonar(GameObject player, Dictionary<string, object> args, double viewYaw)
        {
            var center = Player.Center(player, out var halfHeight, out var radius);
            if (radius <= 0 || halfHeight <= 0) return null;  // (no capsule to feel with)
            var cc = player.GetComponent<CharacterController>();
            var stepHeight = cc != null ? Mathf.Max(0.05f, cc.stepOffset * Mathf.Abs(player.transform.lossyScale.y)) : 0.3f;
            var maxSlope = cc != null ? cc.slopeLimit : 45f;
            var reach = (float)(Math.Max(50.0, Math.Min(5000.0, Json.Num(args, "sonar_reach", 1500))) / Frame.Scale);
            var turnsList = Json.List(args, "sonar_turns") ?? new List<object> { 0.0, -30.0, 30.0, -60.0, 60.0, -90.0, 90.0, -135.0, 135.0, 180.0 };
            var r = radius * 0.9f;
            const float seg = 0.5f;
            var dropAt = Mathf.Max(0.6f, stepHeight * 2f);
            var ways = new List<object>();
            var casts = 0;
            foreach (var t in turnsList.Take(16))
            {
                var turn = Convert.ToDouble(t, System.Globalization.CultureInfo.InvariantCulture);
                var yaw = (viewYaw + turn) * Math.PI / 180.0;
                var dir = new Vector3((float)Math.Sin(yaw), 0f, (float)Math.Cos(yaw));
                var at = center;
                var startY = at.y;
                var travelled = 0f;
                var way = new Dictionary<string, object> { ["turn"] = Json.Round(turn, 1), ["by"] = "reach" };
                var guard = 0;
                while (travelled < reach - 1e-3f && guard++ < 96 && casts < 512)
                {
                    var len = Mathf.Min(seg, reach - travelled);
                    // The capsule, its bottom a step above the feet (steps and bumps in the floor don't stop it), thinner
                    var top = at + Vector3.up * (halfHeight - r);
                    var bottom = at - Vector3.up * (halfHeight - r) + Vector3.up * stepHeight;
                    if (bottom.y > top.y) bottom.y = top.y;
                    ++casts;
                    RaycastHit? wall = null;
                    foreach (var h in Physics.CapsuleCastAll(top, bottom, r, dir, len, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider == null || h.collider.transform.IsChildOf(player.transform)) continue;
                        if (h.distance <= 0f)
                        {
                            // Overlapping it at the start: a wall only if it lies ahead
                            Vector3 near;
                            try
                            {
                                near = h.collider.ClosestPoint(at);
                            }
                            catch
                            {
                                near = h.collider.bounds.ClosestPoint(at);
                            }
                            var to = near - at;
                            to.y = 0;
                            if (Vector3.Dot(to, dir) <= 0.01f) continue;
                        }
                        if (wall == null || h.distance < wall.Value.distance) wall = h;
                    }
                    if (wall != null)
                    {
                        var hit = wall.Value;
                        var n = hit.normal;
                        if (Vector3.Angle(n, Vector3.up) <= maxSlope && hit.distance > 0f && guard < 95)
                        {
                            at += Vector3.up * 0.25f;  // walkable floor rising: go up it
                            continue;
                        }
                        travelled += Mathf.Max(0f, hit.distance);
                        way["by"] = "wall";
                        way["thing"] = Paths.NameOf(ThingOf(hit.collider.gameObject));
                        var flat = new Vector3(n.x, 0, n.z);
                        if (flat.sqrMagnitude > 1e-6f) flat.Normalize();
                        way["normal"] = new Dictionary<string, object> { ["X"] = Json.Round(flat.z, 2), ["Y"] = Json.Round(flat.x, 2) };
                        break;
                    }
                    var next = at + dir * len;
                    // The floor under the body's middle there
                    var feet = next.y - halfHeight;
                    RaycastHit? floor = null;
                    foreach (var h in Physics.RaycastAll(next, Vector3.down, halfHeight + 30f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (h.collider.transform.IsChildOf(player.transform)) continue;
                        if (floor == null || h.distance < floor.Value.distance) floor = h;
                    }
                    var below = floor != null ? feet - floor.Value.point.y : 31f;
                    if (below > dropAt)
                    {
                        // The edge, to a 32nd of the segment: where the floor under the body's middle ends
                        float lo = 0f, hi = len;
                        for (var i = 0; i < 5; ++i)
                        {
                            var mid = (lo + hi) / 2f;
                            var probe = at + dir * mid;
                            if (Physics.Raycast(probe, Vector3.down, halfHeight + dropAt, ~0, QueryTriggerInteraction.Ignore)) lo = mid;
                            else hi = mid;
                        }
                        travelled += lo;
                        way["by"] = "drop";
                        way["drop"] = below < 30f ? (object)Json.Round(below * Frame.Scale, 0) : null;
                        break;
                    }
                    if (floor != null && Vector3.Angle(floor.Value.normal, Vector3.up) > maxSlope + 1f)
                    {
                        way["by"] = "steep";
                        way["thing"] = Paths.NameOf(ThingOf(floor.Value.collider.gameObject));
                        break;
                    }
                    at = next;
                    travelled += len;
                    if (floor != null) at.y = floor.Value.point.y + halfHeight + 0.01f;
                }
                way["clear"] = Json.Round(travelled * Frame.Scale, 0);
                var rise = at.y - startY;
                if (Math.Abs(rise) > stepHeight / 4f) way["rise"] = Json.Round(rise * Frame.Scale, 0);
                ways.Add(way);
            }
            return new Dictionary<string, object> { ["ways"] = ways, ["reach"] = Json.Round(reach * Frame.Scale, 0) };
        }

        /// <summary>What the body is against: colliders within a few centimetres of its shape.</summary>
        static List<object> Touching(GameObject player, double viewYaw)
        {
            var list = new List<object>();
            var center = Player.Center(player, out var halfHeight, out var radius);
            if (radius <= 0) return list;
            var up = Vector3.up * Mathf.Max(0, halfHeight - radius);
            var seen = new HashSet<GameObject>();
            foreach (var c in Physics.OverlapCapsule(center - up + Vector3.up * 0.05f, center + up, radius + 0.08f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c.transform.IsChildOf(player.transform)) continue;
                var thing = ThingOf(c.gameObject);
                if (!seen.Add(thing)) continue;
                Vector3 closest;
                try
                {
                    closest = c.ClosestPoint(center);
                }
                catch
                {
                    closest = c.bounds.ClosestPoint(center);
                }
                var away = closest - center;
                away.y = 0;
                if (away.sqrMagnitude < 1e-6f) continue;  // (under the feet: the floor)
                var yaw = Frame.YawOf(away);
                var normal = -away.normalized;
                list.Add(new Dictionary<string, object>
                {
                    ["thing"] = Paths.NameOf(thing),
                    ["yaw"] = Json.Round(yaw, 1),
                    ["turn"] = Json.Round(Frame.Wrap(yaw - viewYaw), 1),
                    ["normal"] = new Dictionary<string, object> { ["X"] = Json.Round(normal.z, 2), ["Y"] = Json.Round(normal.x, 2), ["Z"] = 0.0 },
                });
            }
            return list;
        }
#endif
    }

    /// <summary>watch: objects' transforms (relative to their parents) sampled every frame against the first sample.</summary>
    public static class Watch
    {
        sealed class Sample
        {
            public string Path;
            public Transform T;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
        }

        public static void Handle(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var samples = new List<Sample>();
            foreach (var o in Json.List(args, "objects") ?? new List<object>())
            {
                var path = Convert.ToString(o);
                var r = Paths.Resolve(path);
                var t = r is GameObject go ? go.transform : r is Component c ? c.transform : null;
                if (t == null) continue;
                samples.Add(new Sample { Path = path, T = t, Pos = t.localPosition, Rot = t.localRotation, Scale = t.localScale });
            }
            var turned = Json.Num(args, "turned", 1.0);
            var moved = Json.Num(args, "moved", 2.0);
            var resized = Json.Num(args, "resized", 0.02);
            var changes = new List<object>();
            var told = new HashSet<string>();
            client.AddPending(new Pending
            {
                Id = id,
                Op = "watch",
                Seconds = Json.Num(args, "seconds", 0.6),
                Tick = (p, now) =>
                {
                    foreach (var s in samples)
                    {
                        if (s.T == null) continue;
                        void Tell(string what)
                        {
                            if (!told.Add(s.Path + "\n" + what)) return;
                            changes.Add(new Dictionary<string, object> { ["t"] = client.GameTime, ["object"] = s.Path, ["what"] = what });
                        }
                        var parent = s.T.parent;
                        var delta = s.T.localPosition - s.Pos;
                        var dist = (parent != null ? parent.TransformVector(delta) : delta).magnitude * Frame.Scale;
                        if (dist > moved) Tell("moved");
                        if (Quaternion.Angle(s.T.localRotation, s.Rot) > turned) Tell("turned");
                        var sc = s.T.localScale;
                        if (Changed(sc.x, s.Scale.x, resized) || Changed(sc.y, s.Scale.y, resized) || Changed(sc.z, s.Scale.z, resized)) Tell("changed size");
                    }
                    if (now - p.Start < p.Seconds) return false;
                    client.Respond(p.Id, new Dictionary<string, object> { ["changes"] = changes });
                    return true;
                },
            });
        }

        static bool Changed(float now, float then, double fraction)
        {
            return Math.Abs(now - then) > Math.Abs(then) * fraction + 1e-6;
        }
    }
}
