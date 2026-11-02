// Modified by Crane Valley: rc reaches only project and Unity members and never a refused namespace (Refused); resolved
// members are cached; ToJson enumerates arrays and materialized collections, never a lazy iterator; assembly-qualified type names aren't loaded.
// rc: Remote Control compatible calls over C# reflection (docs/protocol.md, `rc`; the Unity notes in this package's
// README). call / property (read, write) / describe on GameObjects, components, types (static members) and other Unity
// objects. Values are native: Unity's axes, metres, vectors as lowercase {"x","y","z"}.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Autopilot.Playtest
{
    public sealed class RcError : Exception
    {
        public readonly int Status;

        public RcError(string message, int status = 400) : base(message)
        {
            Status = status;
        }
    }

    public static class Reflect
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const int MaxItems = 1000;
        const int MaxDepth = 5;

        static readonly string[] RefusedNamespaces =
        {
            "System.Reflection", "System.IO", "System.Diagnostics", "System.Net", "System.Runtime.InteropServices", "Mono",
            "Microsoft.Win32",
        };

        // Getters that make copies, leak or log errors when read (Unity's own): never read in a dump, never by name
        static readonly HashSet<string> Unsafe = new HashSet<string>
        {
            "material", "materials", "mesh", "particleSystem", "rigidbody", "rigidbody2D", "collider", "collider2D",
            "renderer", "camera", "light", "animation", "constantForce", "audio", "guiText", "networkView",
            "guiElement", "guiTexture", "hingeJoint", "runInEditMode", "useGUILayout",
        };

        // -- types ------------------------------------------------------------------------------------------

        static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();
        static readonly Dictionary<Assembly, bool> gameAssemblies = new Dictionary<Assembly, bool>();

        static bool IsGameAssembly(Assembly a)
        {
            var name = a.GetName().Name;
            return !(name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("System", StringComparison.Ordinal)
                     || name.StartsWith("Mono", StringComparison.Ordinal) || name == "mscorlib" || name == "netstandard"
                     || name.StartsWith("Autopilot.Playtest", StringComparison.Ordinal) || name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
                     || name.StartsWith("Bee.", StringComparison.Ordinal) || name.StartsWith("ExCSS", StringComparison.Ordinal)
                     || name.StartsWith("Newtonsoft", StringComparison.Ordinal) || name.StartsWith("JetBrains", StringComparison.Ordinal));
        }

        /// <summary>Whether a type is the game's own (not Unity's, .NET's or this package's).</summary>
        public static bool IsGameType(Type t)
        {
            if (t == null) return false;
            var assembly = t.Assembly;
            if (!gameAssemblies.TryGetValue(assembly, out var game))
                gameAssemblies[assembly] = game = IsGameAssembly(assembly);
            return game;
        }

        /// <summary>A type by full name, or by short name when only one type has it (the game's own first).</summary>
        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (typeCache.TryGetValue(name, out var cached)) return cached;
            Type found = null;
#if AUTOPILOT_CURRENT_ASSEMBLIES
            var assemblies = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
#endif
            // Type.GetType would load the assembly an assembly-qualified name asks for: only what is loaded already.
            foreach (var a in assemblies)
            {
                found = a.GetType(name, false);
                if (found != null) break;
            }
            if (found == null)
            {
                // A short name: the game's types first, then UnityEngine's
                var game = new List<Type>();
                var engine = new List<Type>();
                foreach (var a in assemblies)
                {
                    Type[] types;
                    try
                    {
                        types = a.GetTypes();
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        types = e.Types.Where(x => x != null).ToArray();
                    }
                    catch
                    {
                        continue;
                    }
                    var isGame = IsGameAssembly(a);
                    var isEngine = a.GetName().Name.StartsWith("UnityEngine", StringComparison.Ordinal)
                                   || a.GetName().Name.StartsWith("Unity.", StringComparison.Ordinal);
                    if (!isGame && !isEngine) continue;
                    foreach (var t in types)
                    {
                        if (t.Name != name || t.IsNested && !t.IsNestedPublic) continue;
                        (isGame ? game : engine).Add(t);
                    }
                }
                found = game.Count == 1 ? game[0] : game.Count == 0 && engine.Count >= 1 ? engine.FirstOrDefault(t => t.Namespace == "UnityEngine") ?? (engine.Count == 1 ? engine[0] : null) : null;
            }
            typeCache[name] = found;
            return found;
        }

        /// <summary>Whether t is, or derives from, or implements a type of that name.</summary>
        public static bool IsA(Type t, string name)
        {
            for (var b = t; b != null; b = b.BaseType)
                if (b.Name == name || b.FullName == name)
                    return true;
            foreach (var i in t.GetInterfaces())
                if (i.Name == name || i.FullName == name)
                    return true;
            return false;
        }

        public static string TypeName(Type t)
        {
            if (t == null) return "";
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(long)) return "long";
            if (t == typeof(void)) return "void";
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (t.IsByRef) return TypeName(t.GetElementType());
            if (t.IsGenericType)
            {
                var n = t.Name;
                var tick = n.IndexOf('`');
                if (tick > 0) n = n.Substring(0, tick);
                return n + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
            }
            return t.Name;
        }

        // -- what rc refuses --------------------------------------------------------------------------------

        static readonly Dictionary<Type, bool> refusedTypes = new Dictionary<Type, bool>();

        static bool InNamespace(string name, string prefix)
        {
            return name != null && name.StartsWith(prefix, StringComparison.Ordinal)
                                && (name.Length == prefix.Length || name[prefix.Length] == '.');
        }

        /// <summary>Whether rc may reach members declared by t: the project's, or Unity's own.</summary>
        static bool Reachable(Type t)
        {
            var assembly = t.Assembly.GetName().Name;
            return IsGameType(t) || InNamespace(t.Namespace, "UnityEngine") || InNamespace(assembly, "UnityEngine")
                   || assembly.StartsWith("Unity.", StringComparison.Ordinal);
        }

        /// <summary>Whether t, anything it derives from, or anything it is made of is refused.</summary>
        public static bool RefusedType(Type t)
        {
            if (t == null) return false;
            if (refusedTypes.TryGetValue(t, out var known)) return known;
            var refused = t.HasElementType ? RefusedType(t.GetElementType()) : FindRefused(t);
            refusedTypes[t] = refused;
            return refused;
        }

        static bool FindRefused(Type t)
        {
            for (var b = t; b != null; b = b.BaseType)
                foreach (var ns in RefusedNamespaces)
                    if (InNamespace(b.Namespace, ns))
                        return true;
            return t.IsGenericType && t.GetGenericArguments().Any(RefusedType);
        }

        // GetType stays out: it is the way into reflection.
        static readonly HashSet<string> ObjectMembers = new HashSet<string> { nameof(ToString), nameof(Equals), nameof(GetHashCode) };

        static readonly Dictionary<Type, bool> materializedCollections = new Dictionary<Type, bool>();

        static bool IsMaterializedCollection(Type t)
        {
            if (materializedCollections.TryGetValue(t, out var known)) return known;
            known = typeof(ICollection).IsAssignableFrom(t)
                    || t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));
            materializedCollections[t] = known;
            return known;
        }

        static bool RefusedSignature(MemberInfo m)
        {
            if (RefusedType(m.DeclaringType)) return true;
            switch (m)
            {
                case FieldInfo f:
                    return RefusedType(f.FieldType);
                case PropertyInfo p:
                    return RefusedType(p.PropertyType) || p.GetIndexParameters().Any(i => RefusedType(i.ParameterType));
                case MethodInfo c:
                    return RefusedType(c.ReturnType) || c.GetParameters().Any(i => RefusedType(i.ParameterType));
            }
            return true;
        }

        /// <summary>Every member rc reads, writes, calls or lists comes through here, so a harness can't undo its container
        /// by naming File in one call (ADR 0004). It is no boundary: a project's own methods stay reachable.</summary>
        public static bool Refused(MemberInfo m)
        {
            if (m.DeclaringType == typeof(object)) return !ObjectMembers.Contains(m.Name);
            return !Reachable(m.DeclaringType) || RefusedSignature(m);
        }

        // -- members ----------------------------------------------------------------------------------------

        static readonly Dictionary<(Type, bool, bool), List<(string, Type, MemberInfo)>> memberLists = new Dictionary<(Type, bool, bool), List<(string, Type, MemberInfo)>>();
        static readonly Dictionary<(Type, bool, string, bool), MemberInfo> foundMembers = new Dictionary<(Type, bool, string, bool), MemberInfo>();
        static readonly Dictionary<(Type, bool), List<MethodInfo>> methodLists = new Dictionary<(Type, bool), List<MethodInfo>>();
        static readonly Dictionary<(Type, bool, string), List<MethodInfo>> candidates = new Dictionary<(Type, bool, string), List<MethodInfo>>();
        static readonly Dictionary<Type, FieldInfo[]> jsonFields = new Dictionary<Type, FieldInfo[]>();

        /// <summary>The types to read members from: the object's type up to (not including) Unity's base classes.</summary>
        static IEnumerable<Type> OwnTypes(Type t)
        {
            for (var b = t; b != null; b = b.BaseType)
            {
                if (b == typeof(MonoBehaviour) || b == typeof(Behaviour) || b == typeof(Component) || b == typeof(Object)
                    || b == typeof(ScriptableObject) || b == typeof(object))
                    yield break;
                yield return b;
            }
        }

        static string MemberName(FieldInfo f)
        {
            var n = f.Name;
            if (n.StartsWith("<", StringComparison.Ordinal))
            {
                var end = n.IndexOf('>');
                if (end > 1) return n.Substring(1, end - 1);
            }
            return n;
        }

        static bool Obsolete(MemberInfo m)
        {
            return m.IsDefined(typeof(ObsoleteAttribute), true);
        }

        static bool Skippable(FieldInfo f)
        {
            return f.FieldType == typeof(IntPtr) || f.FieldType.IsPointer || (f.IsDefined(typeof(CompilerGeneratedAttribute), false)
                && !f.Name.EndsWith("k__BackingField", StringComparison.Ordinal) && !typeof(Delegate).IsAssignableFrom(f.FieldType));
        }

        /// <summary>The readable members of a target: (name, type, getter) in declaration order.</summary>
        static List<(string name, Type type, MemberInfo member)> Members(Type t, bool isStatic, bool dump)
        {
            var key = (t, isStatic, dump);
            if (memberLists.TryGetValue(key, out var cached)) return cached;
            var list = new List<(string, Type, MemberInfo)>();
            memberLists[key] = list;
            var seen = new HashSet<string>();
            var flags = isStatic ? Static : Instance;
            var types = isStatic ? new[] { t } : OwnTypes(t).ToArray();
            foreach (var type in types)
            {
                var builtin = !IsGameType(type);
                if (!builtin || isStatic)
                {
                    foreach (var f in type.GetFields(flags))
                    {
                        if (Skippable(f) || Obsolete(f) || Refused(f)) continue;
                        var name = MemberName(f);
                        if (seen.Add(name)) list.Add((name, f.FieldType, f));
                    }
                }
                // Properties: Unity's own types keep their data in them (a dump reads them); the game's own are read by
                // name only (a getter may do anything), unless static
                if (builtin || !dump || isStatic)
                {
                    foreach (var p in type.GetProperties(flags))
                    {
                        if (p.GetIndexParameters().Length > 0 || !p.CanRead || Obsolete(p) || Unsafe.Contains(p.Name) || Refused(p)) continue;
                        if (builtin && p.GetMethod != null && !p.GetMethod.IsPublic) continue;
                        if (seen.Add(p.Name)) list.Add((p.Name, p.PropertyType, p));
                    }
                }
            }
            return list;
        }

        static MemberInfo FindMember(Type t, bool isStatic, string name, bool forWrite)
        {
            var key = (t, isStatic, name, forWrite);
            if (!foundMembers.TryGetValue(key, out var found))
            {
                found = SearchMember(t, isStatic, name, forWrite);
                foundMembers[key] = found;
            }
            return found;
        }

        static MemberInfo SearchMember(Type t, bool isStatic, string name, bool forWrite)
        {
            var flags = (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in isStatic ? new[] { t } : Hierarchy(t))
            {
                var f = type.GetField(name, flags | BindingFlags.DeclaredOnly)
                        ?? type.GetField("<" + name + ">k__BackingField", flags | BindingFlags.DeclaredOnly);
                if (f != null && !Obsolete(f) && !Refused(f)) return f;
                PropertyInfo p = null;
                try
                {
                    p = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                }
                catch (AmbiguousMatchException)
                {
                    p = type.GetProperties(flags | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name);
                }
                if (p != null && p.GetIndexParameters().Length == 0 && !Obsolete(p) && !Unsafe.Contains(p.Name)
                    && (forWrite ? p.CanWrite : p.CanRead) && !Refused(p))
                    return p;
            }
            // Case-insensitive, as Remote Control's names are
            foreach (var type in isStatic ? new[] { t } : Hierarchy(t))
            {
                foreach (var f in type.GetFields(flags | BindingFlags.DeclaredOnly))
                    if (string.Equals(MemberName(f), name, StringComparison.OrdinalIgnoreCase) && !Obsolete(f) && !Skippable(f) && !Refused(f))
                        return f;
                foreach (var p in type.GetProperties(flags | BindingFlags.DeclaredOnly))
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.GetIndexParameters().Length == 0
                        && !Obsolete(p) && !Unsafe.Contains(p.Name) && (forWrite ? p.CanWrite : p.CanRead) && !Refused(p))
                        return p;
            }
            return null;
        }

        static IEnumerable<Type> Hierarchy(Type t)
        {
            for (var b = t; b != null && b != typeof(object); b = b.BaseType)
                yield return b;
        }

        static object Get(MemberInfo m, object target)
        {
            if (m is FieldInfo f) return f.GetValue(target);
            return ((PropertyInfo)m).GetValue(target);
        }

        static Type TypeOf(MemberInfo m)
        {
            return m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
        }

        // -- targets ----------------------------------------------------------------------------------------

        sealed class Target
        {
            public object Instance;  // null for static
            public Type Type;
            public string Path;
        }

        static Target Resolve(string path)
        {
            var o = Paths.Resolve(path);
            if (o == null || (o is Object uo && uo == null))
                throw new RcError($"Object {path} does not exist");
            if (o is Type t)
            {
                if (!Reachable(t) || RefusedType(t)) throw new RcError($"{t.FullName} is refused", 403);
                return new Target { Type = t, Path = path };
            }
            return new Target { Instance = o, Type = o.GetType(), Path = path };
        }

        /// <summary>The components of a GameObject to look in for a member it doesn't have itself: the game's first.</summary>
        static IEnumerable<Component> ComponentsFor(GameObject go)
        {
            var all = go.GetComponents<Component>().Where(c => c != null).ToList();
            return all.Where(c => IsGameType(c.GetType())).Concat(all.Where(c => !IsGameType(c.GetType())));
        }

        // -- property ---------------------------------------------------------------------------------------

        public static Dictionary<string, object> Property(Dictionary<string, object> body)
        {
            var path = Json.Str(body, "objectPath");
            var target = Resolve(path);
            var name = Json.Str(body, "propertyName");
            var newValue = Json.Obj(body, "propertyValue");
            var write = newValue != null || Json.Str(body, "access").StartsWith("WRITE", StringComparison.Ordinal);
            if (string.IsNullOrEmpty(name))
            {
                if (write) throw new RcError("A write needs propertyName");
                return Dump(target);
            }
            var (owner, member) = FindOn(target, name, write);
            if (member == null)
                throw new RcError($"Property {name} does not exist on object {path}");
            if (write)
            {
                object value = null;
                var has = newValue != null && Json.TryGetIgnoreCase(newValue, name, out value);
                if (!has && newValue != null && newValue.Count == 1)
                {
                    value = newValue.Values.First();
                    has = true;
                }
                if (!has) throw new RcError($"No value given for {name}");
                SetMember(owner, member, value);
                return new Dictionary<string, object>();
            }
            object read;
            using (Detectors.Quiet())
            {
                try
                {
                    read = owner.Special != null ? owner.Special() : Get(member, owner.Instance);
                }
                catch (Exception e)
                {
                    throw new RcError($"Reading {name} on {path} failed: {Inner(e).Message}");
                }
            }
            return new Dictionary<string, object> { [name] = ToJson(read, 0) };
        }

        sealed class Owner
        {
            public object Instance;
            public Func<object> Special;  // a GameObject's own pseudo-properties
            public Action<object> SpecialSet;
        }

        static (Owner, MemberInfo) FindOn(Target target, string name, bool write)
        {
            if (target.Instance is GameObject go)
            {
                // A GameObject's own state first
                switch (name)
                {
                    case "activeSelf":
                    case "active":
                        return (new Owner { Instance = go, Special = () => go.activeSelf, SpecialSet = v => go.SetActive(Convert.ToBoolean(v, CultureInfo.InvariantCulture)) },
                            typeof(GameObject).GetProperty("activeSelf"));
                }
                var own = FindMember(typeof(GameObject), false, name, write);
                if (own != null) return (new Owner { Instance = go }, own);
                var transform = name == "position" || name == "rotation" || name == "localPosition" || name == "localRotation"
                                || name == "localScale" || name == "eulerAngles" || name == "localEulerAngles"
                    ? FindMember(typeof(Transform), false, name, write) : null;
                if (transform != null) return (new Owner { Instance = go.transform }, transform);
                // Then the members of its components (the game's scripts first): one name, one place, or say where
                var hits = new List<(Component, MemberInfo)>();
                foreach (var c in ComponentsFor(go))
                {
                    var m = FindMember(c.GetType(), false, name, write);
                    if (m != null) hits.Add((c, m));
                }
                if (hits.Count == 1 || hits.Count > 1 && IsGameType(hits[0].Item1.GetType()) && hits.Count(h => IsGameType(h.Item1.GetType())) == 1)
                    return (new Owner { Instance = hits[0].Item1 }, hits[0].Item2);
                if (hits.Count > 1)
                    throw new RcError($"Property {name} is on several components of {target.Path}: "
                                      + string.Join(", ", hits.Select(h => Paths.Of(h.Item1))) + " (name one)");
                return (null, null);
            }
            var member = FindMember(target.Type, target.Instance == null, name, write);
            return (new Owner { Instance = target.Instance }, member);
        }

        static void SetMember(Owner owner, MemberInfo member, object json)
        {
            if (owner.SpecialSet != null)
            {
                owner.SpecialSet(json);
                return;
            }
            var type = TypeOf(member);
            object current = null;
            try
            {
                current = Get(member, owner.Instance);
            }
            catch
            {
                // (write only)
            }
            var value = FromJson(json, type, current);
            try
            {
                if (member is FieldInfo f)
                {
                    if (f.IsInitOnly) throw new RcError($"{f.Name} is read-only");
                    f.SetValue(owner.Instance, value);
                }
                else
                {
                    var p = (PropertyInfo)member;
                    if (!p.CanWrite) throw new RcError($"{p.Name} is read-only");
                    p.SetValue(owner.Instance, value);
                }
            }
            catch (RcError)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new RcError($"Writing {member.Name} failed: {Inner(e).Message}");
            }
        }

        static Dictionary<string, object> Dump(Target target)
        {
            var result = new Dictionary<string, object>();
            using (Detectors.Quiet())
            {
                if (target.Instance is GameObject go)
                {
                    result["name"] = go.name;
                    result["tag"] = go.tag;
                    result["layer"] = LayerMask.LayerToName(go.layer) is var ln && !string.IsNullOrEmpty(ln) ? ln : go.layer.ToString(CultureInfo.InvariantCulture);
                    result["activeSelf"] = go.activeSelf;
                    result["activeInHierarchy"] = go.activeInHierarchy;
                    result["isStatic"] = go.isStatic;
                    result["scene"] = go.scene.name;
                    result["position"] = Frame.Native(go.transform.position);
                    result["eulerAngles"] = Frame.Native(go.transform.eulerAngles, 2);
                    result["localScale"] = Frame.Native(go.transform.localScale);
                    result["components"] = go.GetComponents<Component>().Where(c => c != null).Select(c => (object)Paths.Of(c)).ToList();
                    var children = new List<object>();
                    for (var i = 0; i < go.transform.childCount && i < MaxItems; ++i)
                    {
                        var child = go.transform.GetChild(i).gameObject;
                        if (!Paths.Hidden(child)) children.Add(Paths.Of(child));
                    }
                    result["children"] = children;
                    return result;
                }
                foreach (var (name, _, member) in Members(target.Type, target.Instance == null, true))
                {
                    try
                    {
                        result[name] = ToJson(Get(member, target.Instance), 0);
                    }
                    catch
                    {
                        // (a getter that throws: left out)
                    }
                }
            }
            return result;
        }

        // -- describe ---------------------------------------------------------------------------------------

        public static Dictionary<string, object> Describe(Dictionary<string, object> body)
        {
            var path = Json.Str(body, "objectPath");
            var target = Resolve(path);
            var result = new Dictionary<string, object>
            {
                ["Name"] = target.Instance is Object o ? o.name : target.Type.Name,
                ["Class"] = target.Type.FullName,
            };
            var props = new List<object>();
            foreach (var (name, type, member) in Members(target.Type, target.Instance == null, false))
            {
                props.Add(new Dictionary<string, object>
                {
                    ["Name"] = name,
                    ["Description"] = Tooltip(member),
                    ["Type"] = TypeName(type),
                });
            }
            result["Properties"] = props;
            var functions = new List<object>();
            var seen = new HashSet<string>();
            foreach (var m in Methods(target.Type, target.Instance == null))
            {
                var signature = m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";
                if (!seen.Add(signature)) continue;
                functions.Add(new Dictionary<string, object>
                {
                    ["Name"] = m.Name,
                    ["Description"] = TypeName(m.ReturnType) + (m.IsStatic && target.Instance != null ? " (static)" : ""),
                    ["Arguments"] = m.GetParameters().Where(p => !p.IsOut).Select(p => (object)new Dictionary<string, object>
                    {
                        ["Name"] = p.Name,
                        ["Type"] = TypeName(p.ParameterType) + (p.HasDefaultValue ? " = " + Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture) : ""),
                    }).ToList(),
                });
            }
            result["Functions"] = functions;
            if (target.Instance is GameObject go)
                result["Components"] = go.GetComponents<Component>().Where(c => c != null).Select(c => (object)Paths.Of(c)).ToList();
            return result;
        }

        static string Tooltip(MemberInfo m)
        {
            var field = m as FieldInfo;
            var attrs = m.GetCustomAttributes(true);
            foreach (var a in attrs)
                if (a is TooltipAttribute t)
                    return t.tooltip;
            if (field != null)
            {
                var serialized = field.IsPublic && !field.IsDefined(typeof(NonSerializedAttribute), true) || field.IsDefined(typeof(SerializeField), true);
                return serialized ? "serialized" : "";
            }
            return "";
        }

        static List<MethodInfo> Methods(Type t, bool isStatic)
        {
            var key = (t, isStatic);
            if (!methodLists.TryGetValue(key, out var list))
            {
                list = FindMethods(t, isStatic).Where(m => !Refused(m)).ToList();
                methodLists[key] = list;
            }
            return list;
        }

        static IEnumerable<MethodInfo> FindMethods(Type t, bool isStatic)
        {
            var flags = (isStatic ? Static : Instance);
            var types = isStatic ? new[] { t } : OwnTypes(t).ToArray();
            if (!isStatic && types.Length == 0) types = new[] { t };
            foreach (var type in types)
            {
                var builtin = !IsGameType(type);
                foreach (var m in type.GetMethods(flags))
                {
                    if (m.IsSpecialName || m.IsGenericMethodDefinition || Obsolete(m) || m.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
                    if (m.Name.Contains("<")) continue;
                    if (builtin && !m.IsPublic) continue;
                    yield return m;
                }
                if (!isStatic && !builtin)
                {
                    // and the game type's static methods
                    foreach (var m in type.GetMethods(Static))
                    {
                        if (m.IsSpecialName || m.IsGenericMethodDefinition || Obsolete(m) || m.Name.Contains("<")) continue;
                        yield return m;
                    }
                }
            }
        }

        // -- call -------------------------------------------------------------------------------------------

        public static Dictionary<string, object> Call(Dictionary<string, object> body, MonoBehaviour coroutineHost)
        {
            var path = Json.Str(body, "objectPath");
            var target = Resolve(path);
            var name = Json.Str(body, "functionName");
            var parameters = Json.Obj(body, "parameters") ?? new Dictionary<string, object>();
            object instance = target.Instance;
            var candidates = CandidateMethods(target.Type, instance == null, name);
            if (candidates.Count == 0 && instance is GameObject go)
            {
                // A GameObject's components: the game's scripts first
                foreach (var c in ComponentsFor(go))
                {
                    candidates = CandidateMethods(c.GetType(), false, name);
                    if (candidates.Count > 0)
                    {
                        instance = c;
                        break;
                    }
                }
            }
            if (candidates.Count == 0)
                throw new RcError($"Function: {name} does not exist on object: {path}");
            MethodInfo method = null;
            object[] args = null;
            string why = null;
            foreach (var m in candidates.OrderBy(m => Math.Abs(m.GetParameters().Length - parameters.Count)))
            {
                if (TryBind(m, parameters, out args, out why))
                {
                    method = m;
                    break;
                }
            }
            if (method == null)
                throw new RcError($"{name} on {path}: {why}");
            object returned;
            try
            {
                returned = method.Invoke(method.IsStatic ? null : instance, args);
            }
            catch (TargetInvocationException e)
            {
                var inner = Inner(e);
                throw new RcError($"{name} on {path} threw {inner.GetType().Name}: {inner.Message}", 500);
            }
            catch (Exception e)
            {
                throw new RcError($"{name} on {path}: {Inner(e).Message}");
            }
            var result = new Dictionary<string, object>();
            if (method.ReturnType != typeof(void))
            {
                if (returned is IEnumerator routine && instance is MonoBehaviour mb && mb != null && mb.isActiveAndEnabled)
                {
                    mb.StartCoroutine(routine);
                    result["ReturnValue"] = "coroutine started";
                }
                else if (returned is IEnumerator routine2 && coroutineHost != null)
                {
                    coroutineHost.StartCoroutine(routine2);
                    result["ReturnValue"] = "coroutine started";
                }
                else
                {
                    result["ReturnValue"] = ToJson(returned, 0);
                }
            }
            var ps = method.GetParameters();
            for (var i = 0; i < ps.Length; ++i)
                if (ps[i].ParameterType.IsByRef)
                    result[ps[i].Name] = ToJson(args[i], 0);
            return result;
        }

        static List<MethodInfo> CandidateMethods(Type t, bool isStatic, string name)
        {
            var key = (t, isStatic, name);
            if (candidates.TryGetValue(key, out var cached)) return cached;
            var list = new List<MethodInfo>();
            candidates[key] = list;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | (isStatic ? 0 : BindingFlags.Instance);
            foreach (var type in Hierarchy(t))
            {
                foreach (var m in type.GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    if (m.IsGenericMethodDefinition || Obsolete(m) || Refused(m)) continue;
                    if (m.Name == name) list.Add(m);
                }
                if (list.Count > 0) break;
            }
            if (list.Count == 0)
            {
                foreach (var type in Hierarchy(t))
                {
                    foreach (var m in type.GetMethods(flags | BindingFlags.DeclaredOnly))
                        if (!m.IsGenericMethodDefinition && !Obsolete(m) && !Refused(m) && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                            list.Add(m);
                    if (list.Count > 0) break;
                }
            }
            return list;
        }

        static bool TryBind(MethodInfo m, Dictionary<string, object> given, out object[] args, out string why)
        {
            var ps = m.GetParameters();
            args = new object[ps.Length];
            why = null;
            var used = 0;
            for (var i = 0; i < ps.Length; ++i)
            {
                var p = ps[i];
                var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (Json.TryGetIgnoreCase(given, p.Name, out var value))
                {
                    ++used;
                    try
                    {
                        args[i] = FromJson(value, type, null);
                    }
                    catch (RcError e)
                    {
                        why = $"{p.Name}: {e.Message}";
                        return false;
                    }
                    continue;
                }
                if (p.IsOut)
                {
                    args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
                    continue;
                }
                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue == DBNull.Value || p.DefaultValue == null
                        ? (type.IsValueType ? Activator.CreateInstance(type) : null) : p.DefaultValue;
                    continue;
                }
                // One unnamed argument: the single value given, whatever its key
                if (ps.Length == 1 && given.Count == 1)
                {
                    try
                    {
                        args[i] = FromJson(given.Values.First(), type, null);
                        ++used;
                        continue;
                    }
                    catch (RcError)
                    {
                        // fall through
                    }
                }
                why = $"missing argument {p.Name} ({TypeName(type)})";
                return false;
            }
            if (used < given.Count)
            {
                why = "unknown argument(s) " + string.Join(", ", given.Keys.Where(k => !ps.Any(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase))))
                      + "; it takes " + string.Join(", ", ps.Select(p => p.Name));
                return false;
            }
            return true;
        }

        static Exception Inner(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        // -- values: C# -> JSON ------------------------------------------------------------------------------

        public static object ToJson(object value, int depth)
        {
            return ToJson(value, depth, null);
        }

        static object ToJson(object value, int depth, HashSet<object> visiting)
        {
            switch (value)
            {
                case null:
                    return null;
                case Transform tr:
                    return tr == null ? "" : Paths.OfObject(tr.gameObject);  // (a Transform field names its object)
                case Object uo:
                    return uo == null ? "" : Paths.OfObject(uo);
                case string s:
                    return s;
                case bool b:
                    return b;
                case char c:
                    return c.ToString();
                case float f:
                    return Json.Round(f, 6);
                case double d:
                    return d;
                case int or long or short or byte or sbyte or uint or ushort or ulong or decimal:
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                case Enum e:
                    return e.ToString();
                case Vector3 v3:
                    return new Dictionary<string, object> { ["x"] = Json.Round(v3.x, 6), ["y"] = Json.Round(v3.y, 6), ["z"] = Json.Round(v3.z, 6) };
                case Vector2 v2:
                    return new Dictionary<string, object> { ["x"] = Json.Round(v2.x, 6), ["y"] = Json.Round(v2.y, 6) };
                case Vector4 v4:
                    return new Dictionary<string, object> { ["x"] = Json.Round(v4.x, 6), ["y"] = Json.Round(v4.y, 6), ["z"] = Json.Round(v4.z, 6), ["w"] = Json.Round(v4.w, 6) };
                case Vector3Int i3:
                    return new Dictionary<string, object> { ["x"] = i3.x, ["y"] = i3.y, ["z"] = i3.z };
                case Vector2Int i2:
                    return new Dictionary<string, object> { ["x"] = i2.x, ["y"] = i2.y };
                case Quaternion q:
                    return new Dictionary<string, object>
                    {
                        ["x"] = Json.Round(q.x, 6), ["y"] = Json.Round(q.y, 6), ["z"] = Json.Round(q.z, 6), ["w"] = Json.Round(q.w, 6),
                        ["euler"] = Frame.Native(q.eulerAngles, 3),
                    };
                case Color col:
                    return new Dictionary<string, object> { ["r"] = Json.Round(col.r, 4), ["g"] = Json.Round(col.g, 4), ["b"] = Json.Round(col.b, 4), ["a"] = Json.Round(col.a, 4) };
                case Color32 c32:
                    return new Dictionary<string, object> { ["r"] = c32.r, ["g"] = c32.g, ["b"] = c32.b, ["a"] = c32.a };
                case Rect r:
                    return new Dictionary<string, object> { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };
                case Bounds bo:
                    return new Dictionary<string, object> { ["center"] = ToJson(bo.center, depth + 1, visiting), ["extents"] = ToJson(bo.extents, depth + 1, visiting) };
                case LayerMask lm:
                    return lm.value;
                case Matrix4x4 mx:
                    return mx.ToString();
                case Type t:
                    return t.FullName;
                case UnityEventBase ev:
                    return UnityEventToJson(ev);
                case Delegate del:
                    return del.GetInvocationList().Select(x => (object)((x.Target is Object to && to != null ? to.name + "." : x.Target != null ? x.Target.GetType().Name + "." : "") + x.Method.Name)).ToList();
                case IntPtr _:
                    return null;
            }
            var type = value.GetType();
            if (RefusedType(type)) return TypeName(type);
            if (depth >= MaxDepth) return value.ToString();
            if (value is IDictionary dict)
            {
                var o = new Dictionary<string, object>();
                var n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (++n > MaxItems) break;
                    var key = e.Key is Object ko ? Paths.OfObject(ko) : Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "";
                    o[key] = ToJson(e.Value, depth + 1, visiting);
                }
                return o;
            }
            if (value is Array array)
            {
                var a = new List<object>();
                foreach (var item in array)
                {
                    if (a.Count >= MaxItems) break;
                    a.Add(ToJson(item, depth + 1, visiting));
                }
                return a;
            }
            // By index: an IEnumerable's enumerator can be a lazy iterator, which would run game code on a read.
            if (value is IList list)
            {
                var a = new List<object>();
                for (var i = 0; i < list.Count && i < MaxItems; ++i) a.Add(ToJson(list[i], depth + 1, visiting));
                return a;
            }
            if (IsMaterializedCollection(type))
            {
                var a = new List<object>();
                foreach (var item in (IEnumerable)value)
                {
                    if (a.Count >= MaxItems) break;
                    a.Add(ToJson(item, depth + 1, visiting));
                }
                return a;
            }
            // A struct or a class: its fields (public, and serialized private ones)
            if (!type.IsValueType)
            {
                visiting ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
                if (!visiting.Add(value)) return "(cycle)";
            }
            var fields = new Dictionary<string, object>();
            foreach (var f in JsonFields(type))
            {
                try
                {
                    fields[MemberName(f)] = ToJson(f.GetValue(value), depth + 1, visiting);
                }
                catch
                {
                    // left out
                }
            }
            if (fields.Count == 0 && type.IsValueType == false && type.Namespace != null && type.Namespace.StartsWith("System", StringComparison.Ordinal))
                return value.ToString();
            if (!type.IsValueType) visiting.Remove(value);
            return fields;
        }

        static FieldInfo[] JsonFields(Type type)
        {
            if (jsonFields.TryGetValue(type, out var known)) return known;
            known = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !Skippable(f) && !Obsolete(f) && !RefusedSignature(f)
                            && (f.IsPublic || f.IsDefined(typeof(SerializeField), true) || f.Name.EndsWith("k__BackingField", StringComparison.Ordinal)))
                .ToArray();
            jsonFields[type] = known;
            return known;
        }

        static object UnityEventToJson(UnityEventBase ev)
        {
            var calls = new List<object>();
            for (var i = 0; i < ev.GetPersistentEventCount(); ++i)
            {
                var target = ev.GetPersistentTarget(i);
                calls.Add((target != null ? Paths.OfObject(target) : "None") + "." + ev.GetPersistentMethodName(i));
            }
            return new Dictionary<string, object> { ["persistent"] = calls };
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        // -- values: JSON -> C# ------------------------------------------------------------------------------

        static float F(Dictionary<string, object> d, string key, float fallback)
        {
            return Json.TryGetIgnoreCase(d, key, out var v) && v != null ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : fallback;
        }

        static Dictionary<string, object> AsVectorDict(object json, params string[] names)
        {
            if (json is Dictionary<string, object> d) return d;
            if (json is List<object> l)
            {
                var o = new Dictionary<string, object>();
                for (var i = 0; i < l.Count && i < names.Length; ++i) o[names[i]] = l[i];
                return o;
            }
            return null;
        }

        public static object FromJson(object json, Type type, object current)
        {
            if (RefusedType(type)) throw new RcError($"{TypeName(type)} is refused", 403);
            if (type == typeof(object)) return json;
            if (json == null)
            {
                if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
                    throw new RcError($"null for {TypeName(type)}");
                return null;
            }
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) type = underlying;
            try
            {
                if (type == typeof(string)) return json is string s ? s : Convert.ToString(json, CultureInfo.InvariantCulture);
                if (type == typeof(bool)) return json is bool b ? b : json is string bs ? bool.Parse(bs) : Convert.ToDouble(json, CultureInfo.InvariantCulture) != 0;
                if (type.IsEnum)
                {
                    if (json is string es)
                    {
                        if (double.TryParse(es, NumberStyles.Float, CultureInfo.InvariantCulture, out var en))
                            return Enum.ToObject(type, (long)en);
                        return Enum.Parse(type, es, true);
                    }
                    return Enum.ToObject(type, Convert.ToInt64(json, CultureInfo.InvariantCulture));
                }
                if (type.IsPrimitive || type == typeof(decimal))
                {
                    if (json is bool jb) json = jb ? 1.0 : 0.0;
                    if (json is string ns) json = double.Parse(ns, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (type == typeof(char)) return Convert.ToString(json, CultureInfo.InvariantCulture)[0];
                    var d = Convert.ToDouble(json, CultureInfo.InvariantCulture);
                    if (type == typeof(float)) return (float)d;
                    if (type == typeof(double)) return d;
                    return Convert.ChangeType(Math.Round(d), type, CultureInfo.InvariantCulture);
                }
                if (type == typeof(Vector3))
                {
                    var d = AsVectorDict(json, "x", "y", "z") ?? throw new RcError("expected {\"x\",\"y\",\"z\"}");
                    var c = current is Vector3 cv ? cv : Vector3.zero;
                    return new Vector3(F(d, "x", c.x), F(d, "y", c.y), F(d, "z", c.z));
                }
                if (type == typeof(Vector2))
                {
                    var d = AsVectorDict(json, "x", "y") ?? throw new RcError("expected {\"x\",\"y\"}");
                    var c = current is Vector2 cv ? cv : Vector2.zero;
                    return new Vector2(F(d, "x", c.x), F(d, "y", c.y));
                }
                if (type == typeof(Vector4))
                {
                    var d = AsVectorDict(json, "x", "y", "z", "w") ?? throw new RcError("expected {\"x\",\"y\",\"z\",\"w\"}");
                    return new Vector4(F(d, "x", 0), F(d, "y", 0), F(d, "z", 0), F(d, "w", 0));
                }
                if (type == typeof(Vector3Int))
                {
                    var d = AsVectorDict(json, "x", "y", "z") ?? throw new RcError("expected {\"x\",\"y\",\"z\"}");
                    return new Vector3Int((int)F(d, "x", 0), (int)F(d, "y", 0), (int)F(d, "z", 0));
                }
                if (type == typeof(Vector2Int))
                {
                    var d = AsVectorDict(json, "x", "y") ?? throw new RcError("expected {\"x\",\"y\"}");
                    return new Vector2Int((int)F(d, "x", 0), (int)F(d, "y", 0));
                }
                if (type == typeof(Quaternion))
                {
                    var d = AsVectorDict(json, "x", "y", "z", "w") ?? throw new RcError("expected {\"x\",\"y\",\"z\",\"w\"} or {\"euler\"}");
                    if (Json.TryGetIgnoreCase(d, "w", out _))
                        return new Quaternion(F(d, "x", 0), F(d, "y", 0), F(d, "z", 0), F(d, "w", 1));
                    var e = Json.TryGetIgnoreCase(d, "euler", out var ev) ? ev as Dictionary<string, object> : d;
                    return Quaternion.Euler(F(e, "x", 0), F(e, "y", 0), F(e, "z", 0));
                }
                if (type == typeof(Color))
                {
                    var d = AsVectorDict(json, "r", "g", "b", "a") ?? throw new RcError("expected {\"r\",\"g\",\"b\",\"a\"}");
                    return new Color(F(d, "r", 0), F(d, "g", 0), F(d, "b", 0), F(d, "a", 1));
                }
                if (type == typeof(Color32))
                {
                    var d = AsVectorDict(json, "r", "g", "b", "a") ?? throw new RcError("expected {\"r\",\"g\",\"b\",\"a\"}");
                    return new Color32((byte)F(d, "r", 0), (byte)F(d, "g", 0), (byte)F(d, "b", 0), (byte)F(d, "a", 255));
                }
                if (type == typeof(Rect))
                {
                    var d = AsVectorDict(json, "x", "y", "width", "height") ?? throw new RcError("expected {\"x\",\"y\",\"width\",\"height\"}");
                    return new Rect(F(d, "x", 0), F(d, "y", 0), F(d, "width", 0), F(d, "height", 0));
                }
                if (type == typeof(LayerMask))
                    return (LayerMask)Convert.ToInt32(json, CultureInfo.InvariantCulture);
                if (typeof(Object).IsAssignableFrom(type))
                {
                    var p = json as string ?? throw new RcError($"expected a path for {TypeName(type)}");
                    if (p.Length == 0) return null;
                    var o = Paths.Resolve(p);
                    if (o == null || o is Object uo && uo == null) throw new RcError($"Object {p} does not exist");
                    if (type.IsInstanceOfType(o)) return o;
                    if (o is GameObject go && typeof(Component).IsAssignableFrom(type))
                        return go.GetComponent(type) ?? throw new RcError($"{p} has no {TypeName(type)}");
                    if (o is Component comp && type == typeof(GameObject)) return comp.gameObject;
                    if (o is Component comp2 && typeof(Component).IsAssignableFrom(type))
                        return comp2.GetComponent(type) ?? throw new RcError($"{p} has no {TypeName(type)}");
                    throw new RcError($"{p} is not a {TypeName(type)}");
                }
                if (type.IsArray)
                {
                    var l = json as List<object> ?? throw new RcError($"expected a list for {TypeName(type)}");
                    var element = type.GetElementType();
                    var a = Array.CreateInstance(element, l.Count);
                    for (var i = 0; i < l.Count; ++i) a.SetValue(FromJson(l[i], element, null), i);
                    return a;
                }
                if (type.IsGenericType && typeof(IDictionary).IsAssignableFrom(type))
                {
                    var o = json as Dictionary<string, object> ?? throw new RcError($"expected an object for {TypeName(type)}");
                    var args = type.GetGenericArguments();
                    var dict = (IDictionary)Activator.CreateInstance(type);
                    foreach (var pair in o) dict[FromJson(pair.Key, args[0], null)] = FromJson(pair.Value, args[1], null);
                    return dict;
                }
                if (type.IsGenericType && typeof(IList).IsAssignableFrom(type))
                {
                    var l = json as List<object> ?? throw new RcError($"expected a list for {TypeName(type)}");
                    var element = type.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(type);
                    foreach (var item in l) list.Add(FromJson(item, element, null));
                    return list;
                }
                if (json is Dictionary<string, object> fields)
                {
                    var instance = current ?? (type.IsValueType ? Activator.CreateInstance(type)
                        : type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type)
                        : FormatterServices.GetUninitializedObject(type));
                    if (type.IsValueType && current != null) instance = current;  // (a boxed copy: set below, returned)
                    foreach (var pair in fields)
                    {
                        var f = FindMember(type, false, pair.Key, true);
                        if (f == null) throw new RcError($"{TypeName(type)} has no {pair.Key}");
                        var fieldType = TypeOf(f);
                        object cur = null;
                        try
                        {
                            cur = Get(f, instance);
                        }
                        catch
                        {
                            // (write only)
                        }
                        var v = FromJson(pair.Value, fieldType, cur);
                        if (f is FieldInfo fi) fi.SetValue(instance, v);
                        else ((PropertyInfo)f).SetValue(instance, v);
                    }
                    return instance;
                }
            }
            catch (RcError)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new RcError($"can't make a {TypeName(type)} of {Json.Write(json)}: {Inner(e).Message}");
            }
            throw new RcError($"can't make a {TypeName(type)} of {Json.Write(json)}");
        }
    }

    static class FormatterServices
    {
        public static object GetUninitializedObject(Type t)
        {
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
        }
    }
}
