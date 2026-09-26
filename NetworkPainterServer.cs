using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "10.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Type StructureType;
    internal static readonly Dictionary<int, int> LastColors = new Dictionary<int, int>();
    internal static bool BaselineReady;
    internal static bool Propagating;
    internal static long NextScanMs;
    internal static bool FirstTickLogged;
    private Harmony harmony;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 10.0.0 starting");

        Assembly game = null;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type s = a.GetType("Assets.Scripts.Objects.Structure", false);
                if (s != null)
                {
                    StructureType = s;
                    game = a;
                    break;
                }
            }
            catch { }
        }

        if (StructureType == null || game == null)
        {
            Log.LogError("Could not find Assets.Scripts.Objects.Structure");
            return;
        }

        Log.LogInfo("Structure type found: " + StructureType.FullName);

        harmony = new Harmony("dominic.networkpainterserver.v10");

        string[] tickNames =
        {
            "OnMainTick",
            "ServerTick",
            "AllTick",
            "OnLifeTick",
            "ThingUpdate",
            "SlowUpdate"
        };

        int patched = 0;

        foreach (Type t in SafeTypes(game))
        {
            foreach (MethodInfo m in SafeMethods(t))
            {
                bool wanted = false;

                foreach (string name in tickNames)
                {
                    if (m.Name == name)
                    {
                        wanted = true;
                        break;
                    }
                }

                if (!wanted || m.IsAbstract || m.ContainsGenericParameters)
                    continue;

                if (m.DeclaringType != null &&
                    m.DeclaringType.Name.StartsWith("<"))
                    continue;

                try
                {
                    harmony.Patch(
                        m,
                        postfix: new HarmonyMethod(
                            typeof(NetworkPainterServer),
                            nameof(TickPostfix)));

                    patched++;
                    Log.LogInfo("Patched tick hook: " +
                        m.DeclaringType.FullName + "." + m.Name);
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not patch tick hook " +
                        m.DeclaringType.FullName + "." + m.Name +
                        ": " + e.Message);
                }
            }
        }

        Log.LogInfo("Network Painter Server ready. Tick hooks patched=" + patched);
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

    public static void TickPostfix(MethodBase __originalMethod)
    {
        if (Propagating || StructureType == null)
            return;

        long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        if (!FirstTickLogged)
        {
            FirstTickLogged = true;
            Log.LogInfo("TICK HOOK ACTIVE: " +
                (__originalMethod.DeclaringType != null
                    ? __originalMethod.DeclaringType.FullName
                    : "?") +
                "." + __originalMethod.Name);
        }

        if (now < NextScanMs)
            return;

        NextScanMs = now + 250;

        try
        {
            Scan();
        }
        catch (Exception e)
        {
            Log.LogError("Network scan exception: " + e);
        }
    }

    private static void Scan()
    {
        UnityEngine.Object[] objects;

        try
        {
            objects = UnityEngine.Object.FindObjectsOfType(StructureType);
        }
        catch (Exception e)
        {
            Log.LogError("Structure scan failed: " + e.Message);
            return;
        }

        int readable = 0;

        if (!BaselineReady)
        {
            LastColors.Clear();

            foreach (UnityEngine.Object obj in objects)
            {
                int? color = ReadColor(obj);
                if (!color.HasValue)
                    continue;

                LastColors[obj.GetInstanceID()] = color.Value;
                readable++;
            }

            BaselineReady = true;

            Log.LogInfo(
                "NETWORK SCAN BASELINE: structures=" + objects.Length +
                " color-readable=" + readable);

            return;
        }

        foreach (UnityEngine.Object obj in objects)
        {
            if (obj == null)
                continue;

            int? color = ReadColor(obj);
            if (!color.HasValue)
                continue;

            int id = obj.GetInstanceID();

            if (!LastColors.TryGetValue(id, out int oldColor))
            {
                LastColors[id] = color.Value;
                continue;
            }

            if (oldColor == color.Value)
                continue;

            LastColors[id] = color.Value;

            Log.LogInfo(
                "COLOR CHANGE: " + obj.GetType().FullName +
                " " + oldColor + " -> " + color.Value);

            Propagate(obj, color.Value);
        }
    }

    private static int? ReadColor(object obj)
    {
        if (obj == null)
            return null;

        MethodInfo getter = FindMethod(
            obj.GetType(),
            "get_CustomColorIndex",
            0);

        if (getter != null)
        {
            try
            {
                object value = getter.Invoke(obj, null);
                return Convert.ToInt32(value);
            }
            catch { }
        }

        object direct = ReadMember(
            obj,
            "CustomColorIndex",
            "CustomColourIndex",
            "ColorIndex",
            "ColourIndex");

        if (direct != null)
        {
            try
            {
                return Convert.ToInt32(direct);
            }
            catch { }
        }

        return null;
    }

    private static void Propagate(object source, int color)
    {
        object network = GetNetwork(source);

        if (network == null)
        {
            Log.LogInfo("NO NETWORK: " + source.GetType().FullName);
            DumpNetworkMethods(source.GetType());
            return;
        }

        IEnumerable members = GetStructureList(network);

        if (members == null)
        {
            Log.LogWarning(
                "NETWORK FOUND BUT NO StructureList: " +
                network.GetType().FullName);

            DumpNetworkMethods(network.GetType());
            return;
        }

        int found = 0;
        int changed = 0;
        int failed = 0;

        Propagating = true;

        try
        {
            foreach (object member in members)
            {
                if (member == null)
                    continue;

                found++;

                int? current = ReadColor(member);

                if (current.HasValue &&
                    current.Value == color)
                {
                    Remember(member, color);
                    continue;
                }

                if (ApplyColor(member, color))
                {
                    changed++;
                    Remember(member, color);
                }
                else
                {
                    failed++;
                }
            }
        }
        finally
        {
            Propagating = false;
        }

        Log.LogInfo(
            "NETWORK PAINT: type=" + network.GetType().FullName +
            " members=" + found +
            " changed=" + changed +
            " failed=" + failed +
            " color=" + color);
    }

    private static object GetNetwork(object source)
    {
        string[] getters =
        {
            "get_CableNetwork",
            "get_PipeNetwork",
            "get_ChuteNetwork",
            "get_StructureNetwork"
        };

        foreach (string name in getters)
        {
            MethodInfo m = FindMethod(
                source.GetType(),
                name,
                0);

            if (m == null)
                continue;

            try
            {
                object network = m.Invoke(source, null);

                if (network != null)
                {
                    Log.LogInfo(
                        "NETWORK FOUND: " + name +
                        " -> " + network.GetType().FullName);

                    return network;
                }
            }
            catch { }
        }

        return null;
    }

    private static IEnumerable GetStructureList(object network)
    {
        MethodInfo getter = FindMethod(
            network.GetType(),
            "get_StructureList",
            0);

        if (getter != null)
        {
            try
            {
                object value = getter.Invoke(network, null);

                if (value is IEnumerable enumerable)
                    return enumerable;
            }
            catch { }
        }

        object direct = ReadMember(
            network,
            "StructureList");

        return direct as IEnumerable;
    }

    private static bool ApplyColor(object target, int color)
    {
        foreach (MethodInfo m in FindMethods(
            target.GetType(),
            "SetCustomColor"))
        {
            ParameterInfo[] p;

            try
            {
                p = m.GetParameters();
            }
            catch
            {
                continue;
            }

            if (p.Length < 1 || p.Length > 3)
                continue;

            try
            {
                object[] args = new object[p.Length];

                args[0] =
                    ConvertValue(
                        color,
                        p[0].ParameterType);

                for (int i = 1; i < p.Length; i++)
                {
                    if (p[i].ParameterType == typeof(bool))
                        args[i] = true;
                    else if (p[i].HasDefaultValue)
                        args[i] = p[i].DefaultValue;
                    else if (p[i].ParameterType.IsValueType)
                        args[i] = Activator.CreateInstance(
                            p[i].ParameterType);
                    else
                        args[i] = null;
                }

                m.Invoke(target, args);
                return true;
            }
            catch { }
        }

        MethodInfo setter = FindMethod(
            target.GetType(),
            "set_CustomColorIndex",
            1);

        if (setter != null)
        {
            try
            {
                Type t =
                    setter.GetParameters()[0]
                    .ParameterType;

                setter.Invoke(
                    target,
                    new object[]
                    {
                        ConvertValue(color, t)
                    });

                return true;
            }
            catch { }
        }

        return false;
    }

    private static void Remember(object obj, int color)
    {
        UnityEngine.Object unityObj =
            obj as UnityEngine.Object;

        if (unityObj == null)
            return;

        LastColors[unityObj.GetInstanceID()] = color;
    }

    private static object ReadMember(
        object obj,
        params string[] names)
    {
        if (obj == null)
            return null;

        Type start = obj.GetType();

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
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);

                    if (f != null)
                        return f.GetValue(obj);
                }
                catch { }

                try
                {
                    PropertyInfo p =
                        t.GetProperty(
                            name,
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);

                    if (p != null &&
                        p.GetIndexParameters().Length == 0)
                        return p.GetValue(obj, null);
                }
                catch { }

                t = t.BaseType;
            }
        }

        return null;
    }

    private static MethodInfo FindMethod(
        Type type,
        string name,
        int argCount)
    {
        Type t = type;

        while (t != null)
        {
            foreach (MethodInfo m in SafeMethods(t))
            {
                try
                {
                    if (m.Name == name &&
                        m.GetParameters().Length == argCount)
                        return m;
                }
                catch { }
            }

            t = t.BaseType;
        }

        return null;
    }

    private static IEnumerable<MethodInfo> FindMethods(
        Type type,
        string name)
    {
        Type t = type;

        while (t != null)
        {
            foreach (MethodInfo m in SafeMethods(t))
                if (m.Name == name)
                    yield return m;

            t = t.BaseType;
        }
    }

    private static object ConvertValue(
        int color,
        Type target)
    {
        if (target.IsByRef)
            target = target.GetElementType();

        if (target == typeof(int))
            return color;

        if (target.IsEnum)
            return Enum.ToObject(target, color);

        return Convert.ChangeType(color, target);
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try
        {
            return a.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            List<Type> types = new List<Type>();

            foreach (Type t in e.Types)
                if (t != null)
                    types.Add(t);

            return types;
        }
        catch
        {
            return new Type[0];
        }
    }

    private static MethodInfo[] SafeMethods(Type t)
    {
        try
        {
            return t.GetMethods(
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

    private static void DumpNetworkMethods(Type type)
    {
        Log.LogInfo(
            "Network-related methods on " +
            type.FullName + ":");

        Type t = type;

        while (t != null)
        {
            foreach (MethodInfo m in SafeMethods(t))
            {
                string n = m.Name;

                if (n.IndexOf(
                        "Network",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf(
                        "Cable",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf(
                        "Pipe",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf(
                        "Chute",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log.LogInfo(
                        "  " +
                        t.FullName +
                        "." +
                        n);
                }
            }

            t = t.BaseType;
        }
    }
}
