using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "12.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Assembly GameAssembly;
    internal static Type ThingColorMessageType;
    internal static readonly List<RegistryAccessor> Registries = new List<RegistryAccessor>();
    internal static readonly Dictionary<long, int> LastColors = new Dictionary<long, int>();
    internal static bool BaselineReady;
    internal static bool Propagating;
    internal static bool ProcessHookSeen;
    internal static long NextScanAt;
    internal static string LastApplyMethod = "";
    private Harmony harmony;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 12.0.0 starting");

        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (a.GetType("Assets.Scripts.Objects.Structure", false) == null)
                    continue;

                GameAssembly = a;
                ThingColorMessageType = a.GetType(
                    "Assets.Scripts.Networking.ThingColorMessage",
                    false);
                break;
            }
            catch { }
        }

        if (GameAssembly == null)
        {
            Log.LogError("Could not locate Assembly-CSharp.");
            return;
        }

        DiscoverRegistries();

        harmony = new Harmony("dominic.networkpainterserver.v12");

        int processHooks = PatchNetworkingProcessMethods();
        int colorHooks = PatchColorMethods();

        Log.LogInfo(
            "Network Painter Server ready. Process hooks=" +
            processHooks +
            ", color hooks=" +
            colorHooks +
            ", registries=" +
            Registries.Count);
    }

    private void OnDestroy()
    {
        try
        {
            if (harmony != null)
                harmony.UnpatchSelf();
        }
        catch { }
    }

    private int PatchNetworkingProcessMethods()
    {
        int patched = 0;

        foreach (Type t in SafeTypes(GameAssembly))
        {
            string ns = t.Namespace ?? "";

            if (!ns.StartsWith("Assets.Scripts.Networking", StringComparison.Ordinal))
                continue;

            foreach (MethodInfo m in SafeMethods(t))
            {
                if (m.Name != "Process")
                    continue;

                if (m.IsAbstract || m.ContainsGenericParameters)
                    continue;

                ParameterInfo[] p;

                try
                {
                    p = m.GetParameters();
                }
                catch
                {
                    continue;
                }

                if (p.Length != 1)
                    continue;

                if (p[0].ParameterType != typeof(long))
                    continue;

                try
                {
                    harmony.Patch(
                        m,
                        postfix: new HarmonyMethod(
                            typeof(NetworkPainterServer),
                            nameof(NetworkProcessPostfix)));

                    patched++;
                }
                catch { }
            }
        }

        Log.LogInfo("Patched networking Process(Int64) methods: " + patched);
        return patched;
    }

    private int PatchColorMethods()
    {
        int patched = 0;

        foreach (Type t in SafeTypes(GameAssembly))
        {
            foreach (MethodInfo m in SafeMethods(t))
            {
                if (m.Name != "SetCustomColor" &&
                    m.Name != "OnColourChange" &&
                    m.Name != "OnColourChangeEvent")
                    continue;

                if (m.IsAbstract || m.ContainsGenericParameters)
                    continue;

                try
                {
                    harmony.Patch(
                        m,
                        postfix: new HarmonyMethod(
                            typeof(NetworkPainterServer),
                            nameof(ColorMethodPostfix)));

                    patched++;
                }
                catch { }
            }
        }

        Log.LogInfo("Patched color-change methods: " + patched);
        return patched;
    }

    public static void NetworkProcessPostfix(MethodBase __originalMethod)
    {
        if (Propagating)
            return;

        if (!ProcessHookSeen)
        {
            ProcessHookSeen = true;

            Log.LogInfo(
                "NETWORK PROCESS HOOK ACTIVE: " +
                Describe(__originalMethod));
        }

        MaybeScan();
    }

    public static void ColorMethodPostfix(
        object __instance,
        MethodBase __originalMethod)
    {
        if (Propagating)
            return;

        if (__instance != null)
        {
            Log.LogDebug(
                "Color method fired: " +
                Describe(__originalMethod) +
                " on " +
                __instance.GetType().FullName);
        }

        MaybeScan(true);
    }

    private static void MaybeScan(bool force)
    {
        long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        if (!force && now < NextScanAt)
            return;

        NextScanAt = now + 50;

        try
        {
            ScanNetworks();
        }
        catch (Exception e)
        {
            Log.LogError("Network scan failed: " + e);
        }
    }

    private static void MaybeScan()
    {
        MaybeScan(false);
    }

    private static void DiscoverRegistries()
    {
        Registries.Clear();

        string[] names =
        {
            "AllCableNetworks",
            "AllPipeNetworks",
            "AllChuteNetworks"
        };

        foreach (Type t in SafeTypes(GameAssembly))
        {
            foreach (string name in names)
            {
                FieldInfo f = null;
                PropertyInfo p = null;

                try
                {
                    f = t.GetField(
                        name,
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                }
                catch { }

                if (f != null)
                {
                    Registries.Add(
                        new RegistryAccessor(
                            t,
                            name,
                            f,
                            null));

                    Log.LogInfo(
                        "NETWORK REGISTRY: " +
                        t.FullName +
                        "." +
                        name);

                    continue;
                }

                try
                {
                    p = t.GetProperty(
                        name,
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                }
                catch { }

                if (p != null &&
                    p.GetIndexParameters().Length == 0)
                {
                    MethodInfo getter = p.GetGetMethod(true);

                    if (getter != null && getter.IsStatic)
                    {
                        Registries.Add(
                            new RegistryAccessor(
                                t,
                                name,
                                null,
                                p));

                        Log.LogInfo(
                            "NETWORK REGISTRY: " +
                            t.FullName +
                            "." +
                            name);
                    }
                }
            }
        }
    }

    private static void ScanNetworks()
    {
        if (Registries.Count == 0)
        {
            DiscoverRegistries();

            if (Registries.Count == 0)
                return;
        }

        int networks = 0;
        int members = 0;
        int readable = 0;

        foreach (RegistryAccessor registry in Registries)
        {
            object collection = registry.GetValue();

            if (collection == null)
                continue;

            List<object> networkSnapshot =
                SnapshotCollection(collection);

            foreach (object network in networkSnapshot)
            {
                if (network == null)
                    continue;

                networks++;

                List<object> networkMembers =
                    SnapshotMembers(network);

                if (networkMembers.Count == 0)
                    continue;

                members += networkMembers.Count;

                int? newColor = null;
                object changedObject = null;
                bool conflict = false;

                foreach (object member in networkMembers)
                {
                    int? color = ReadColor(member);

                    if (!color.HasValue)
                        continue;

                    readable++;

                    long key = StableKey(member);
                    int oldColor;

                    if (!LastColors.TryGetValue(key, out oldColor))
                    {
                        LastColors[key] = color.Value;
                        continue;
                    }

                    if (oldColor == color.Value)
                        continue;

                    LastColors[key] = color.Value;

                    if (!BaselineReady)
                        continue;

                    if (!newColor.HasValue)
                    {
                        newColor = color.Value;
                        changedObject = member;
                    }
                    else if (newColor.Value != color.Value)
                    {
                        conflict = true;
                    }
                }

                if (!BaselineReady ||
                    !newColor.HasValue)
                    continue;

                if (conflict)
                {
                    Log.LogWarning(
                        "Different colors changed inside one network in the same scan; skipped.");
                    continue;
                }

                Log.LogInfo(
                    "COLOR CHANGE DETECTED: " +
                    (changedObject == null
                        ? "unknown"
                        : changedObject.GetType().FullName) +
                    " -> " +
                    newColor.Value +
                    " | network=" +
                    network.GetType().FullName +
                    " | members=" +
                    networkMembers.Count);

                PaintNetwork(
                    network,
                    networkMembers,
                    newColor.Value);
            }
        }

        if (!BaselineReady &&
            networks > 0 &&
            members > 0 &&
            readable > 0)
        {
            BaselineReady = true;

            Log.LogInfo(
                "NETWORK BASELINE READY: networks=" +
                networks +
                " members=" +
                members +
                " color-readable=" +
                readable);
        }
    }

    private static List<object> SnapshotCollection(object collection)
    {
        List<object> result = new List<object>();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            result.Clear();

            try
            {
                IDictionary dictionary =
                    collection as IDictionary;

                if (dictionary != null)
                {
                    foreach (object value in dictionary.Values)
                        result.Add(value);

                    return new List<object>(result);
                }

                IEnumerable enumerable =
                    collection as IEnumerable;

                if (enumerable != null)
                {
                    foreach (object value in enumerable)
                        result.Add(value);

                    return new List<object>(result);
                }

                object values =
                    ReadMember(
                        collection,
                        "Values");

                enumerable =
                    values as IEnumerable;

                if (enumerable != null)
                {
                    foreach (object value in enumerable)
                        result.Add(value);

                    return new List<object>(result);
                }

                return result;
            }
            catch
            {
                System.Threading.Thread.Sleep(1);
            }
        }

        return result;
    }

    private static List<object> SnapshotMembers(object network)
    {
        List<object> result = new List<object>();
        IEnumerable enumerable = null;

        MethodInfo getter =
            FindMethod(
                network.GetType(),
                "get_StructureList",
                0);

        if (getter != null)
        {
            try
            {
                enumerable =
                    getter.Invoke(
                        network,
                        null)
                    as IEnumerable;
            }
            catch { }
        }

        if (enumerable == null)
        {
            string[] names =
            {
                "StructureList",
                "<StructureList>k__BackingField",
                "CableList",
                "Structures",
                "Members"
            };

            foreach (string name in names)
            {
                object value =
                    ReadMember(
                        network,
                        name);

                enumerable =
                    value as IEnumerable;

                if (enumerable != null)
                    break;
            }
        }

        if (enumerable == null)
            return result;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            result.Clear();

            try
            {
                foreach (object member in enumerable)
                {
                    if (member != null)
                        result.Add(member);
                }

                return new List<object>(result);
            }
            catch
            {
                System.Threading.Thread.Sleep(1);
            }
        }

        return result;
    }

    private static int? ReadColor(object obj)
    {
        if (obj == null)
            return null;

        MethodInfo getter =
            FindMethod(
                obj.GetType(),
                "get_CustomColorIndex",
                0);

        if (getter != null)
        {
            try
            {
                return Convert.ToInt32(
                    getter.Invoke(
                        obj,
                        null));
            }
            catch { }
        }

        string[] names =
        {
            "CustomColorIndex",
            "<CustomColorIndex>k__BackingField",
            "ColorIndex",
            "ColourIndex"
        };

        foreach (string name in names)
        {
            object value =
                ReadMember(
                    obj,
                    name);

            if (value == null)
                continue;

            try
            {
                return Convert.ToInt32(value);
            }
            catch { }
        }

        return null;
    }

    private static void PaintNetwork(
        object network,
        List<object> members,
        int color)
    {
        int changed = 0;
        int already = 0;
        int failed = 0;
        int broadcasts = 0;

        Propagating = true;

        try
        {
            foreach (object member in members)
            {
                int? current =
                    ReadColor(member);

                if (current.HasValue &&
                    current.Value == color)
                {
                    already++;
                    LastColors[StableKey(member)] = color;
                    continue;
                }

                if (ApplyColor(member, color))
                {
                    changed++;
                    LastColors[StableKey(member)] = color;
                    continue;
                }

                if (WriteMember(
                        member,
                        "CustomColorIndex",
                        color) ||
                    WriteMember(
                        member,
                        "<CustomColorIndex>k__BackingField",
                        color))
                {
                    changed++;
                    LastColors[StableKey(member)] = color;

                    if (BroadcastColor(
                            member,
                            color))
                    {
                        broadcasts++;
                    }

                    continue;
                }

                failed++;
            }
        }
        finally
        {
            Propagating = false;
        }

        Log.LogInfo(
            "NETWORK PAINT COMPLETE: type=" +
            network.GetType().FullName +
            " members=" +
            members.Count +
            " changed=" +
            changed +
            " already=" +
            already +
            " failed=" +
            failed +
            " fallback-broadcasts=" +
            broadcasts +
            " color=" +
            color);
    }

    private static bool ApplyColor(
        object target,
        int color)
    {
        List<MethodInfo> candidates =
            new List<MethodInfo>();

        foreach (MethodInfo method in
                 FindMethods(
                     target.GetType(),
                     "SetCustomColor"))
        {
            candidates.Add(method);
        }

        candidates.Sort(
            delegate(MethodInfo a, MethodInfo b)
            {
                ParameterInfo[] ap =
                    SafeParameters(a);

                ParameterInfo[] bp =
                    SafeParameters(b);

                bool aInt =
                    ap.Length > 0 &&
                    ap[0].ParameterType == typeof(int);

                bool bInt =
                    bp.Length > 0 &&
                    bp[0].ParameterType == typeof(int);

                if (aInt && !bInt) return -1;
                if (!aInt && bInt) return 1;

                return ap.Length.CompareTo(bp.Length);
            });

        foreach (MethodInfo m in candidates)
        {
            ParameterInfo[] p =
                SafeParameters(m);

            if (p.Length < 1 ||
                p.Length > 4)
                continue;

            try
            {
                object[] args =
                    new object[p.Length];

                args[0] =
                    ConvertForType(
                        color,
                        p[0].ParameterType);

                for (int i = 1;
                     i < p.Length;
                     i++)
                {
                    if (p[i].ParameterType ==
                        typeof(bool))
                    {
                        args[i] = true;
                    }
                    else if (p[i].HasDefaultValue)
                    {
                        args[i] =
                            p[i].DefaultValue;
                    }
                    else if (p[i].ParameterType.IsValueType)
                    {
                        args[i] =
                            Activator.CreateInstance(
                                p[i].ParameterType);
                    }
                    else
                    {
                        args[i] = null;
                    }
                }

                m.Invoke(
                    target,
                    args);

                string description =
                    Describe(m);

                if (description != LastApplyMethod)
                {
                    LastApplyMethod = description;

                    Log.LogInfo(
                        "Using color apply method: " +
                        description);
                }

                return true;
            }
            catch { }
        }

        MethodInfo setter =
            FindMethod(
                target.GetType(),
                "set_CustomColorIndex",
                1);

        if (setter != null)
        {
            try
            {
                ParameterInfo[] p =
                    SafeParameters(setter);

                setter.Invoke(
                    target,
                    new object[]
                    {
                        ConvertForType(
                            color,
                            p[0].ParameterType)
                    });

                return true;
            }
            catch { }
        }

        return false;
    }

    private static bool BroadcastColor(
        object thing,
        int color)
    {
        if (ThingColorMessageType == null ||
            thing == null)
            return false;

        object id =
            ReadMember(
                thing,
                "ReferenceId",
                "ReferenceID",
                "<ReferenceId>k__BackingField",
                "ThingId");

        if (id == null)
            return false;

        object message = null;

        try
        {
            message =
                Activator.CreateInstance(
                    ThingColorMessageType,
                    true);
        }
        catch { }

        if (message == null)
            return false;

        if (!WriteMember(
                message,
                "ThingId",
                id))
            return false;

        if (!WriteMember(
                message,
                "ColorIndex",
                color))
            return false;

        MethodInfo send =
            FindMethod(
                ThingColorMessageType,
                "SendToClients",
                0);

        if (send == null)
            return false;

        try
        {
            send.Invoke(
                message,
                null);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long StableKey(object obj)
    {
        object id =
            ReadMember(
                obj,
                "ReferenceId",
                "ReferenceID",
                "<ReferenceId>k__BackingField",
                "ThingId");

        if (id != null)
        {
            try
            {
                long value =
                    Convert.ToInt64(id);

                if (value != 0)
                    return value;
            }
            catch { }
        }

        return
            ((long)obj.GetHashCode()) +
            long.MinValue / 2;
    }

    private static object ReadMember(
        object obj,
        params string[] names)
    {
        if (obj == null)
            return null;

        Type start =
            obj.GetType();

        foreach (string name in names)
        {
            Type t = start;

            while (t != null)
            {
                try
                {
                    FieldInfo f =
                        t.GetField(
                            name,
                            BindingFlags.Instance |
                            BindingFlags.Static |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);

                    if (f != null)
                        return f.GetValue(
                            f.IsStatic
                                ? null
                                : obj);
                }
                catch { }

                try
                {
                    PropertyInfo p =
                        t.GetProperty(
                            name,
                            BindingFlags.Instance |
                            BindingFlags.Static |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);

                    if (p != null &&
                        p.GetIndexParameters().Length == 0)
                    {
                        MethodInfo getter =
                            p.GetGetMethod(true);

                        if (getter != null)
                        {
                            return p.GetValue(
                                getter.IsStatic
                                    ? null
                                    : obj,
                                null);
                        }
                    }
                }
                catch { }

                t = t.BaseType;
            }
        }

        return null;
    }

    private static bool WriteMember(
        object obj,
        string name,
        object value)
    {
        if (obj == null)
            return false;

        Type t =
            obj.GetType();

        while (t != null)
        {
            try
            {
                FieldInfo f =
                    t.GetField(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                if (f != null)
                {
                    f.SetValue(
                        f.IsStatic
                            ? null
                            : obj,
                        ConvertObject(
                            value,
                            f.FieldType));

                    return true;
                }
            }
            catch { }

            try
            {
                PropertyInfo p =
                    t.GetProperty(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                if (p != null &&
                    p.CanWrite)
                {
                    MethodInfo setter =
                        p.GetSetMethod(true);

                    if (setter != null)
                    {
                        p.SetValue(
                            setter.IsStatic
                                ? null
                                : obj,
                            ConvertObject(
                                value,
                                p.PropertyType),
                            null);

                        return true;
                    }
                }
            }
            catch { }

            t = t.BaseType;
        }

        return false;
    }

    private static MethodInfo FindMethod(
        Type type,
        string name,
        int argCount)
    {
        Type t = type;

        while (t != null)
        {
            foreach (MethodInfo m in
                     SafeMethods(t))
            {
                ParameterInfo[] p =
                    SafeParameters(m);

                if (m.Name == name &&
                    p.Length == argCount)
                {
                    return m;
                }
            }

            t = t.BaseType;
        }

        return null;
    }

    private static IEnumerable<MethodInfo>
        FindMethods(
            Type type,
            string name)
    {
        Type t = type;

        while (t != null)
        {
            foreach (MethodInfo m in
                     SafeMethods(t))
            {
                if (m.Name == name)
                    yield return m;
            }

            t = t.BaseType;
        }
    }

    private static object ConvertForType(
        int value,
        Type target)
    {
        if (target.IsByRef)
            target =
                target.GetElementType();

        if (target == typeof(int))
            return value;

        if (target.IsEnum)
            return Enum.ToObject(
                target,
                value);

        return Convert.ChangeType(
            value,
            target);
    }

    private static object ConvertObject(
        object value,
        Type target)
    {
        if (target.IsByRef)
            target =
                target.GetElementType();

        if (value == null)
        {
            return target.IsValueType
                ? Activator.CreateInstance(target)
                : null;
        }

        Type source =
            value.GetType();

        if (target.IsAssignableFrom(source))
            return value;

        if (target.IsEnum)
        {
            return Enum.ToObject(
                target,
                Convert.ToInt32(value));
        }

        if (source.IsEnum)
        {
            return Convert.ChangeType(
                Convert.ToInt32(value),
                target);
        }

        return Convert.ChangeType(
            value,
            target);
    }

    private static IEnumerable<Type>
        SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            List<Type> result =
                new List<Type>();

            foreach (Type t in e.Types)
            {
                if (t != null)
                    result.Add(t);
            }

            return result;
        }
        catch
        {
            return new Type[0];
        }
    }

    private static MethodInfo[]
        SafeMethods(Type type)
    {
        try
        {
            return type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
        }
        catch
        {
            return new MethodInfo[0];
        }
    }

    private static ParameterInfo[]
        SafeParameters(MethodBase method)
    {
        try
        {
            return method.GetParameters();
        }
        catch
        {
            return new ParameterInfo[0];
        }
    }

    private static string Describe(
        MethodBase method)
    {
        if (method == null)
            return "?";

        ParameterInfo[] p =
            SafeParameters(method);

        string text = "";

        for (int i = 0;
             i < p.Length;
             i++)
        {
            if (i > 0)
                text += ", ";

            text +=
                p[i].ParameterType.Name +
                " " +
                p[i].Name;
        }

        return
            (method.DeclaringType == null
                ? "?"
                : method.DeclaringType.FullName) +
            "." +
            method.Name +
            "(" +
            text +
            ")";
    }

    internal sealed class RegistryAccessor
    {
        internal readonly Type Owner;
        internal readonly string Name;
        private readonly FieldInfo field;
        private readonly PropertyInfo property;

        internal RegistryAccessor(
            Type owner,
            string name,
            FieldInfo field,
            PropertyInfo property)
        {
            Owner = owner;
            Name = name;
            this.field = field;
            this.property = property;
        }

        internal object GetValue()
        {
            try
            {
                if (field != null)
                    return field.GetValue(null);

                if (property != null)
                    return property.GetValue(
                        null,
                        null);
            }
            catch { }

            return null;
        }
    }
}
