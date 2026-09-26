using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "11.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Assembly GameAssembly;
    internal static Type StructureType;
    internal static Type ThingColorMessageType;
    internal static readonly Dictionary<long, int> LastColors = new Dictionary<long, int>();
    internal static readonly List<RegistryAccessor> Registries = new List<RegistryAccessor>();
    internal static bool BaselineReady;
    internal static bool Propagating;
    internal static bool TickSeen;
    internal static long NextScanMs;
    private Harmony harmony;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 11.0.0 starting");

        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type s = a.GetType("Assets.Scripts.Objects.Structure", false);
                if (s == null) continue;
                GameAssembly = a;
                StructureType = s;
                ThingColorMessageType = a.GetType("Assets.Scripts.Networking.ThingColorMessage", false);
                break;
            }
            catch { }
        }

        if (GameAssembly == null || StructureType == null)
        {
            Log.LogError("Could not locate the Stationeers game assembly.");
            return;
        }

        Log.LogInfo("Game assembly: " + GameAssembly.FullName);
        Log.LogInfo("Structure type: " + StructureType.FullName);
        Log.LogInfo("ThingColorMessage: " + (ThingColorMessageType == null ? "NOT FOUND" : ThingColorMessageType.FullName));

        DiscoverNetworkRegistries();

        harmony = new Harmony("dominic.networkpainterserver.v11");

        int tickHooks = PatchRealGameTick();
        Log.LogInfo("Network Painter Server ready. GameTick hooks=" + tickHooks +
                    ", network registries=" + Registries.Count);
    }

    private void OnDestroy()
    {
        try
        {
            if (harmony != null) harmony.UnpatchSelf();
        }
        catch { }
    }

    private int PatchRealGameTick()
    {
        int count = 0;
        Type gm = null;

        try { gm = GameAssembly.GetType("Assets.Scripts.GameManager", false); }
        catch { }

        if (gm == null)
        {
            Log.LogError("Assets.Scripts.GameManager was not found.");
            return 0;
        }

        Type[] nested;
        try
        {
            nested = gm.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            nested = new Type[0];
        }

        foreach (Type t in nested)
        {
            if (t == null || !t.Name.StartsWith("<GameTick>d__", StringComparison.Ordinal))
                continue;

            MethodInfo moveNext = null;
            try
            {
                moveNext = t.GetMethod(
                    "MoveNext",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch { }

            if (moveNext == null) continue;

            try
            {
                harmony.Patch(
                    moveNext,
                    postfix: new HarmonyMethod(typeof(NetworkPainterServer), nameof(GameTickPostfix)));

                count++;
                Log.LogInfo("PATCHED REAL GAME TICK: " + t.FullName + ".MoveNext()");
            }
            catch (Exception e)
            {
                Log.LogError("Could not patch GameTick state machine: " + e);
            }
        }

        if (count == 0)
        {
            MethodInfo gameTick = FindDeclaredMethod(gm, "GameTick");
            if (gameTick != null)
            {
                try
                {
                    harmony.Patch(
                        gameTick,
                        postfix: new HarmonyMethod(typeof(NetworkPainterServer), nameof(GameTickPostfix)));

                    count++;
                    Log.LogInfo("PATCHED FALLBACK GAME TICK: " + gm.FullName + ".GameTick()");
                }
                catch (Exception e)
                {
                    Log.LogError("Could not patch GameManager.GameTick: " + e);
                }
            }
        }

        return count;
    }

    public static void GameTickPostfix()
    {
        if (Propagating || GameAssembly == null) return;

        if (!TickSeen)
        {
            TickSeen = true;
            Log.LogInfo("GAME TICK HOOK ACTIVE");
        }

        long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        if (now < NextScanMs) return;
        NextScanMs = now + 250;

        try
        {
            if (Registries.Count == 0)
                DiscoverNetworkRegistries();

            ScanNetworkRegistries();
        }
        catch (Exception e)
        {
            Log.LogError("Network registry scan exception: " + e);
        }
    }

    private static void DiscoverNetworkRegistries()
    {
        Registries.Clear();

        string[] wanted =
        {
            "AllCableNetworks",
            "AllPipeNetworks",
            "AllChuteNetworks",
            "AllStructureNetworks"
        };

        foreach (Type t in SafeTypes(GameAssembly))
        {
            foreach (string name in wanted)
            {
                FieldInfo f = null;
                PropertyInfo p = null;

                try
                {
                    f = t.GetField(
                        name,
                        BindingFlags.Static | BindingFlags.Instance |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                }
                catch { }

                if (f != null)
                {
                    AddRegistry(new RegistryAccessor(t, name, f, null));
                    continue;
                }

                try
                {
                    p = t.GetProperty(
                        name,
                        BindingFlags.Static | BindingFlags.Instance |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                }
                catch { }

                if (p != null && p.GetIndexParameters().Length == 0)
                    AddRegistry(new RegistryAccessor(t, name, null, p));
            }
        }

        bool hasSpecific = false;
        foreach (RegistryAccessor r in Registries)
        {
            if (r.Name == "AllCableNetworks" ||
                r.Name == "AllPipeNetworks" ||
                r.Name == "AllChuteNetworks")
            {
                hasSpecific = true;
                break;
            }
        }

        if (hasSpecific)
            Registries.RemoveAll(r => r.Name == "AllStructureNetworks");

        foreach (RegistryAccessor r in Registries)
            Log.LogInfo("NETWORK REGISTRY FOUND: " + r.OwnerType.FullName + "." + r.Name);
    }

    private static void AddRegistry(RegistryAccessor candidate)
    {
        foreach (RegistryAccessor r in Registries)
        {
            if (r.OwnerType == candidate.OwnerType && r.Name == candidate.Name)
                return;
        }

        Registries.Add(candidate);
    }

    private static void ScanNetworkRegistries()
    {
        int networkCount = 0;
        int memberCount = 0;
        int readableCount = 0;

        foreach (RegistryAccessor registry in Registries)
        {
            object collection = registry.GetValue();
            if (collection == null) continue;

            foreach (object network in EnumerateCollection(collection))
            {
                if (network == null) continue;

                networkCount++;

                IEnumerable membersEnumerable = GetNetworkMembers(network);
                if (membersEnumerable == null) continue;

                List<object> members = new List<object>();
                foreach (object member in membersEnumerable)
                {
                    if (member != null) members.Add(member);
                }

                if (members.Count == 0) continue;

                memberCount += members.Count;

                int? changedColor = null;
                object changedMember = null;
                bool conflict = false;

                foreach (object member in members)
                {
                    int? color = ReadColor(member);
                    if (!color.HasValue) continue;

                    readableCount++;

                    long key = GetStableKey(member);

                    int oldColor;
                    if (!LastColors.TryGetValue(key, out oldColor))
                    {
                        LastColors[key] = color.Value;
                        continue;
                    }

                    if (oldColor == color.Value) continue;

                    LastColors[key] = color.Value;

                    if (!BaselineReady)
                        continue;

                    if (!changedColor.HasValue)
                    {
                        changedColor = color.Value;
                        changedMember = member;
                    }
                    else if (changedColor.Value != color.Value)
                    {
                        conflict = true;
                    }
                }

                if (!BaselineReady || !changedColor.HasValue)
                    continue;

                if (conflict)
                {
                    Log.LogWarning("MULTIPLE DIFFERENT COLOR CHANGES detected in one network; skipped this scan for safety.");
                    continue;
                }

                Log.LogInfo(
                    "COLOR CHANGE DETECTED: " +
                    (changedMember == null ? "unknown" : changedMember.GetType().FullName) +
                    " -> " + changedColor.Value +
                    " | network=" + network.GetType().FullName +
                    " | members=" + members.Count);

                PaintWholeNetwork(network, members, changedColor.Value);
            }
        }

        if (!BaselineReady && memberCount > 0)
        {
            BaselineReady = true;

            Log.LogInfo(
                "NETWORK BASELINE READY: networks=" + networkCount +
                " members=" + memberCount +
                " color-readable=" + readableCount);
        }
        else if (!BaselineReady && TickSeen)
        {
            Log.LogInfo(
                "NETWORK BASELINE WAITING: registries=" + Registries.Count +
                " networks=" + networkCount +
                " members=" + memberCount);
        }
    }

    private static void PaintWholeNetwork(object network, List<object> members, int color)
    {
        int changed = 0;
        int already = 0;
        int failed = 0;
        int broadcast = 0;

        Propagating = true;

        try
        {
            foreach (object member in members)
            {
                int? current = ReadColor(member);

                if (current.HasValue && current.Value == color)
                {
                    already++;
                    LastColors[GetStableKey(member)] = color;
                    continue;
                }

                if (!ApplyColor(member, color))
                {
                    failed++;
                    continue;
                }

                changed++;
                LastColors[GetStableKey(member)] = color;

                if (BroadcastThingColor(member, color))
                    broadcast++;
            }
        }
        finally
        {
            Propagating = false;
        }

        Log.LogInfo(
            "NETWORK PAINT COMPLETE: type=" + network.GetType().FullName +
            " members=" + members.Count +
            " changed=" + changed +
            " already=" + already +
            " failed=" + failed +
            " client-updates=" + broadcast +
            " color=" + color);
    }

    private static IEnumerable GetNetworkMembers(object network)
    {
        MethodInfo getter = FindMethod(network.GetType(), "get_StructureList", 0);

        if (getter != null)
        {
            try
            {
                object value = getter.Invoke(network, null);
                IEnumerable e = AsEnumerable(value);
                if (e != null) return e;
            }
            catch { }
        }

        string[] names =
        {
            "StructureList",
            "Structures",
            "Members",
            "<StructureList>k__BackingField"
        };

        foreach (string name in names)
        {
            object value = ReadMember(network, name);
            IEnumerable e = AsEnumerable(value);
            if (e != null) return e;
        }

        Type t = network.GetType();
        while (t != null)
        {
            FieldInfo[] fields;
            PropertyInfo[] props;

            try
            {
                fields = t.GetFields(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                fields = new FieldInfo[0];
            }

            foreach (FieldInfo f in fields)
            {
                if (f.Name.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) < 0 &&
                    f.Name.IndexOf("Member", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    IEnumerable e = AsEnumerable(f.GetValue(network));
                    if (e != null) return e;
                }
                catch { }
            }

            try
            {
                props = t.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                props = new PropertyInfo[0];
            }

            foreach (PropertyInfo p in props)
            {
                if (p.GetIndexParameters().Length != 0) continue;

                if (p.Name.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) < 0 &&
                    p.Name.IndexOf("Member", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    IEnumerable e = AsEnumerable(p.GetValue(network, null));
                    if (e != null) return e;
                }
                catch { }
            }

            t = t.BaseType;
        }

        Log.LogWarning("Could not find member list on network type " + network.GetType().FullName);
        return null;
    }

    private static IEnumerable AsEnumerable(object value)
    {
        if (value == null || value is string) return null;

        IDictionary dict = value as IDictionary;
        if (dict != null) return dict.Values;

        IEnumerable enumerable = value as IEnumerable;
        if (enumerable != null) return enumerable;

        object values = ReadMember(value, "Values");
        if (values != null && !ReferenceEquals(values, value))
            return values as IEnumerable;

        return null;
    }

    private static IEnumerable<object> EnumerateCollection(object collection)
    {
        IDictionary dict = collection as IDictionary;

        if (dict != null)
        {
            foreach (object value in dict.Values)
                yield return value;

            yield break;
        }

        IEnumerable enumerable = collection as IEnumerable;

        if (enumerable != null)
        {
            foreach (object value in enumerable)
                yield return value;

            yield break;
        }

        object values = ReadMember(collection, "Values");
        IEnumerable valuesEnumerable = values as IEnumerable;

        if (valuesEnumerable != null)
        {
            foreach (object value in valuesEnumerable)
                yield return value;
        }
    }

    private static int? ReadColor(object obj)
    {
        if (obj == null) return null;

        MethodInfo getter = FindMethod(obj.GetType(), "get_CustomColorIndex", 0);

        if (getter != null)
        {
            try
            {
                return Convert.ToInt32(getter.Invoke(obj, null));
            }
            catch { }
        }

        string[] names =
        {
            "CustomColorIndex",
            "<CustomColorIndex>k__BackingField",
            "CustomColourIndex",
            "ColorIndex",
            "ColourIndex"
        };

        foreach (string name in names)
        {
            object value = ReadMember(obj, name);

            if (value == null) continue;

            try { return Convert.ToInt32(value); }
            catch { }
        }

        return null;
    }

    private static bool ApplyColor(object target, int color)
    {
        foreach (MethodInfo m in FindMethods(target.GetType(), "SetCustomColor"))
        {
            ParameterInfo[] p;

            try { p = m.GetParameters(); }
            catch { continue; }

            if (p.Length < 1 || p.Length > 3) continue;

            try
            {
                object[] args = new object[p.Length];
                args[0] = ConvertValue(color, p[0].ParameterType);

                for (int i = 1; i < p.Length; i++)
                {
                    if (p[i].ParameterType == typeof(bool))
                        args[i] = true;
                    else if (p[i].HasDefaultValue)
                        args[i] = p[i].DefaultValue;
                    else if (p[i].ParameterType.IsValueType)
                        args[i] = Activator.CreateInstance(p[i].ParameterType);
                    else
                        args[i] = null;
                }

                m.Invoke(target, args);
                return true;
            }
            catch { }
        }

        MethodInfo setter = FindMethod(target.GetType(), "set_CustomColorIndex", 1);

        if (setter != null)
        {
            try
            {
                Type type = setter.GetParameters()[0].ParameterType;
                setter.Invoke(target, new object[] { ConvertValue(color, type) });
                return true;
            }
            catch { }
        }

        return WriteMember(target, "CustomColorIndex", color) ||
               WriteMember(target, "<CustomColorIndex>k__BackingField", color);
    }

    private static bool BroadcastThingColor(object thing, int color)
    {
        if (ThingColorMessageType == null || thing == null) return false;

        object reference = ReadMember(
            thing,
            "ReferenceId",
            "ReferenceID",
            "<ReferenceId>k__BackingField",
            "ThingId");

        if (reference == null) return false;

        object message = null;

        try
        {
            message = Activator.CreateInstance(ThingColorMessageType, true);
        }
        catch { }

        if (message == null)
        {
            MethodInfo singleton = FindMethod(ThingColorMessageType, "get_Singleton", 0);

            if (singleton != null)
            {
                try
                {
                    message = singleton.Invoke(singleton.IsStatic ? null : message, null);
                }
                catch { }
            }
        }

        if (message == null) return false;

        if (!WriteMember(message, "ThingId", reference)) return false;
        if (!WriteMember(message, "ColorIndex", color)) return false;

        MethodInfo send = FindMethod(message.GetType(), "SendToClients", 0);
        if (send == null) return false;

        try
        {
            send.Invoke(message, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long GetStableKey(object obj)
    {
        object reference = ReadMember(
            obj,
            "ReferenceId",
            "ReferenceID",
            "<ReferenceId>k__BackingField");

        if (reference != null)
        {
            try
            {
                long id = Convert.ToInt64(reference);
                if (id != 0) return id;
            }
            catch { }
        }

        UnityEngine.Object unityObject = obj as UnityEngine.Object;

        if (unityObject != null)
            return long.MinValue + (long)unityObject.GetInstanceID();

        return long.MinValue / 2 + RuntimeHelpers.GetHashCode(obj);
    }

    private static object ReadMember(object obj, params string[] names)
    {
        if (obj == null) return null;

        Type start = obj.GetType();

        foreach (string name in names)
        {
            Type t = start;

            while (t != null)
            {
                try
                {
                    FieldInfo f = t.GetField(
                        name,
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (f != null)
                        return f.GetValue(f.IsStatic ? null : obj);
                }
                catch { }

                try
                {
                    PropertyInfo p = t.GetProperty(
                        name,
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (p != null && p.GetIndexParameters().Length == 0)
                    {
                        MethodInfo getter = p.GetGetMethod(true);

                        if (getter != null)
                            return p.GetValue(getter.IsStatic ? null : obj, null);
                    }
                }
                catch { }

                t = t.BaseType;
            }
        }

        return null;
    }

    private static bool WriteMember(object obj, string name, object value)
    {
        if (obj == null) return false;

        Type t = obj.GetType();

        while (t != null)
        {
            try
            {
                FieldInfo f = t.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (f != null)
                {
                    f.SetValue(f.IsStatic ? null : obj, ConvertObject(value, f.FieldType));
                    return true;
                }
            }
            catch { }

            try
            {
                PropertyInfo p = t.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (p != null && p.CanWrite)
                {
                    MethodInfo setter = p.GetSetMethod(true);

                    if (setter != null)
                    {
                        p.SetValue(setter.IsStatic ? null : obj, ConvertObject(value, p.PropertyType), null);
                        return true;
                    }
                }
            }
            catch { }

            t = t.BaseType;
        }

        return false;
    }

    private static MethodInfo FindDeclaredMethod(Type t, string name)
    {
        if (t == null) return null;

        MethodInfo[] methods;

        try
        {
            methods = t.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
        }
        catch
        {
            return null;
        }

        foreach (MethodInfo m in methods)
            if (m.Name == name)
                return m;

        return null;
    }

    private static MethodInfo FindMethod(Type type, string name, int argCount)
    {
        Type t = type;

        while (t != null)
        {
            MethodInfo[] methods;

            try
            {
                methods = t.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
            }
            catch
            {
                methods = new MethodInfo[0];
            }

            foreach (MethodInfo m in methods)
            {
                try
                {
                    if (m.Name == name && m.GetParameters().Length == argCount)
                        return m;
                }
                catch { }
            }

            t = t.BaseType;
        }

        return null;
    }

    private static IEnumerable<MethodInfo> FindMethods(Type type, string name)
    {
        Type t = type;

        while (t != null)
        {
            MethodInfo[] methods;

            try
            {
                methods = t.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
            }
            catch
            {
                methods = new MethodInfo[0];
            }

            foreach (MethodInfo m in methods)
                if (m.Name == name)
                    yield return m;

            t = t.BaseType;
        }
    }

    private static object ConvertValue(int value, Type target)
    {
        if (target.IsByRef) target = target.GetElementType();

        if (target == typeof(int)) return value;
        if (target.IsEnum) return Enum.ToObject(target, value);

        return Convert.ChangeType(value, target);
    }

    private static object ConvertObject(object value, Type target)
    {
        if (target.IsByRef) target = target.GetElementType();

        if (value == null)
            return target.IsValueType ? Activator.CreateInstance(target) : null;

        Type source = value.GetType();

        if (target.IsAssignableFrom(source))
            return value;

        if (target.IsEnum)
            return Enum.ToObject(target, Convert.ToInt32(value));

        if (source.IsEnum)
            return Convert.ChangeType(Convert.ToInt32(value), target);

        return Convert.ChangeType(value, target);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            List<Type> result = new List<Type>();

            foreach (Type t in e.Types)
                if (t != null) result.Add(t);

            return result;
        }
        catch
        {
            return new Type[0];
        }
    }

    internal sealed class RegistryAccessor
    {
        internal readonly Type OwnerType;
        internal readonly string Name;
        private readonly FieldInfo field;
        private readonly PropertyInfo property;

        internal RegistryAccessor(Type ownerType, string name, FieldInfo fieldInfo, PropertyInfo propertyInfo)
        {
            OwnerType = ownerType;
            Name = name;
            field = fieldInfo;
            property = propertyInfo;
        }

        internal object GetValue()
        {
            try
            {
                if (field != null)
                {
                    object owner = field.IsStatic ? null : FindOwner();
                    if (!field.IsStatic && owner == null) return null;
                    return field.GetValue(owner);
                }

                if (property != null)
                {
                    MethodInfo getter = property.GetGetMethod(true);
                    if (getter == null) return null;

                    object owner = getter.IsStatic ? null : FindOwner();
                    if (!getter.IsStatic && owner == null) return null;

                    return property.GetValue(owner, null);
                }
            }
            catch { }

            return null;
        }

        private object FindOwner()
        {
            try
            {
                MethodInfo getInstance = FindMethod(OwnerType, "get_Instance", 0);

                if (getInstance != null && getInstance.IsStatic)
                {
                    object instance = getInstance.Invoke(null, null);
                    if (instance != null) return instance;
                }
            }
            catch { }

            try
            {
                UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(OwnerType);
                if (objects != null && objects.Length > 0)
                    return objects[0];
            }
            catch { }

            return null;
        }
    }
}
