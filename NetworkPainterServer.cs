using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "8.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    private static ManualLogSource Log;
    private static Type StructureType;
    private readonly Dictionary<int, int> lastColors = new Dictionary<int, int>();
    private float nextScan;
    private bool baselineReady;
    private bool propagating;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 8.0.0 starting");

        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                StructureType = a.GetType("Assets.Scripts.Objects.Structure", false);
                if (StructureType != null) break;
            }
            catch { }
        }

        if (StructureType == null)
        {
            Log.LogError("Could not find Assets.Scripts.Objects.Structure");
            return;
        }

        Log.LogInfo("Structure type found: " + StructureType.FullName);
        Log.LogInfo("Network scan mode enabled. Scan interval: 0.25 seconds.");
    }

    private void Update()
    {
        if (StructureType == null || propagating) return;
        if (Time.realtimeSinceStartup < nextScan) return;

        nextScan = Time.realtimeSinceStartup + 0.25f;
        Scan();
    }

    private void Scan()
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

        if (!baselineReady)
        {
            lastColors.Clear();

            foreach (UnityEngine.Object obj in objects)
            {
                int? color = ReadColor(obj);
                if (!color.HasValue) continue;

                lastColors[obj.GetInstanceID()] = color.Value;
                readable++;
            }

            baselineReady = true;
            Log.LogInfo("NETWORK SCAN BASELINE: structures=" + objects.Length +
                        " color-readable=" + readable);
            return;
        }

        HashSet<int> alive = new HashSet<int>();

        foreach (UnityEngine.Object obj in objects)
        {
            if (obj == null) continue;

            int id = obj.GetInstanceID();
            alive.Add(id);

            int? color = ReadColor(obj);
            if (!color.HasValue) continue;

            if (!lastColors.TryGetValue(id, out int oldColor))
            {
                lastColors[id] = color.Value;
                continue;
            }

            if (oldColor == color.Value) continue;

            lastColors[id] = color.Value;

            Log.LogInfo("COLOR CHANGE: " + obj.GetType().FullName +
                        " " + oldColor + " -> " + color.Value);

            Propagate(obj, color.Value);
        }

        if (lastColors.Count > alive.Count + 100)
        {
            List<int> dead = new List<int>();
            foreach (int id in lastColors.Keys)
                if (!alive.Contains(id)) dead.Add(id);

            foreach (int id in dead)
                lastColors.Remove(id);
        }
    }

    private int? ReadColor(object obj)
    {
        if (obj == null) return null;

        MethodInfo getter = FindMethod(obj.GetType(), "get_CustomColorIndex", 0);
        if (getter != null)
        {
            try
            {
                object v = getter.Invoke(obj, null);
                return Convert.ToInt32(v);
            }
            catch { }
        }

        object direct = ReadMember(obj,
            "CustomColorIndex",
            "CustomColourIndex",
            "ColorIndex",
            "ColourIndex");

        if (direct != null)
        {
            try { return Convert.ToInt32(direct); }
            catch { }
        }

        return null;
    }

    private void Propagate(object source, int color)
    {
        object network = GetBestNetwork(source);

        if (network == null)
        {
            Log.LogInfo("NO NETWORK: " + source.GetType().FullName);
            return;
        }

        IEnumerable members = GetStructureList(network);

        if (members == null)
        {
            Log.LogWarning("NETWORK FOUND BUT NO StructureList: " +
                           network.GetType().FullName);
            DumpNetworkMethods(network.GetType());
            return;
        }

        int found = 0;
        int changed = 0;
        int failed = 0;

        propagating = true;

        try
        {
            foreach (object member in members)
            {
                if (member == null) continue;

                found++;

                int? current = ReadColor(member);
                if (current.HasValue && current.Value == color)
                {
                    RememberColor(member, color);
                    continue;
                }

                if (ApplyColor(member, color))
                {
                    changed++;
                    RememberColor(member, color);
                }
                else
                {
                    failed++;
                }
            }
        }
        finally
        {
            propagating = false;
        }

        Log.LogInfo("NETWORK PAINT: type=" + network.GetType().FullName +
                    " members=" + found +
                    " changed=" + changed +
                    " failed=" + failed +
                    " color=" + color);
    }

    private object GetBestNetwork(object source)
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
            MethodInfo m = FindMethod(source.GetType(), name, 0);
            if (m == null) continue;

            try
            {
                object network = m.Invoke(source, null);
                if (network != null)
                {
                    Log.LogInfo("NETWORK FOUND: " + name +
                                " -> " + network.GetType().FullName);
                    return network;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(name + " failed on " +
                               source.GetType().FullName +
                               ": " + e.Message);
            }
        }

        return null;
    }

    private IEnumerable GetStructureList(object network)
    {
        MethodInfo getter = FindMethod(network.GetType(), "get_StructureList", 0);

        if (getter != null)
        {
            try
            {
                object v = getter.Invoke(network, null);
                if (v is IEnumerable enumerable) return enumerable;
            }
            catch { }
        }

        object direct = ReadMember(network, "StructureList");

        return direct as IEnumerable;
    }

    private bool ApplyColor(object target, int color)
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
                Type t = setter.GetParameters()[0].ParameterType;
                setter.Invoke(target, new object[] { ConvertValue(color, t) });
                return true;
            }
            catch { }
        }

        return false;
    }

    private void RememberColor(object obj, int color)
    {
        UnityEngine.Object unityObj = obj as UnityEngine.Object;
        if (unityObj == null) return;

        lastColors[unityObj.GetInstanceID()] = color;
    }

    private object ReadMember(object obj, params string[] names)
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
                    PropertyInfo p = t.GetProperty(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (p != null && p.GetIndexParameters().Length == 0)
                        return p.GetValue(obj, null);
                }
                catch { }

                t = t.BaseType;
            }
        }

        return null;
    }

    private MethodInfo FindMethod(Type type, string name, int argCount)
    {
        Type t = type;

        while (t != null)
        {
            MethodInfo[] methods;

            try
            {
                methods = t.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
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

    private IEnumerable<MethodInfo> FindMethods(Type type, string name)
    {
        Type t = type;

        while (t != null)
        {
            MethodInfo[] methods;

            try
            {
                methods = t.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
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

    private object ConvertValue(int color, Type target)
    {
        if (target.IsByRef)
            target = target.GetElementType();

        if (target == typeof(int))
            return color;

        if (target.IsEnum)
            return Enum.ToObject(target, color);

        return Convert.ChangeType(color, target);
    }

    private void DumpNetworkMethods(Type type)
    {
        Log.LogInfo("Network-related methods on " + type.FullName + ":");

        Type t = type;

        while (t != null)
        {
            MethodInfo[] methods;

            try
            {
                methods = t.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
            }
            catch
            {
                methods = new MethodInfo[0];
            }

            foreach (MethodInfo m in methods)
            {
                string n = m.Name;

                if (n.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log.LogInfo("  " + t.FullName + "." + n);
                }
            }

            t = t.BaseType;
        }
    }
}
