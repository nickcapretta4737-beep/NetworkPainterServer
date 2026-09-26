using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "4.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    private static ManualLogSource Log;
    private readonly Dictionary<int, int> lastColors = new Dictionary<int, int>();
    private Type structureType;
    private float nextScan;
    private bool baselineReady;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 4.0.0 starting");
        structureType = FindType("Assets.Scripts.Objects.Structure");
        if (structureType == null)
        {
            Log.LogError("Could not find Assets.Scripts.Objects.Structure");
            return;
        }
        Log.LogInfo("Structure type: " + structureType.FullName);
        Log.LogInfo("Network Painter Server ready. Watching server-side color state...");
    }

    private void Update()
    {
        if (structureType == null) return;
        if (Time.realtimeSinceStartup < nextScan) return;
        nextScan = Time.realtimeSinceStartup + 0.25f;
        ScanStructures();
    }

    private void ScanStructures()
    {
        UnityEngine.Object[] objs;
        try { objs = UnityEngine.Object.FindObjectsOfType(structureType); }
        catch (Exception e)
        {
            Log.LogError("FindObjectsOfType failed: " + e);
            return;
        }

        int readable = 0;
        if (!baselineReady)
        {
            lastColors.Clear();
            foreach (UnityEngine.Object obj in objs)
            {
                int color;
                if (!TryGetColor(obj, out color)) continue;
                readable++;
                lastColors[obj.GetInstanceID()] = color;
            }
            baselineReady = true;
            Log.LogInfo("Color baseline ready. Found " + objs.Length + " structures; tracking " + readable + " color-readable structures.");
            return;
        }

        foreach (UnityEngine.Object obj in objs)
        {
            int color;
            if (!TryGetColor(obj, out color)) continue;
            int id = obj.GetInstanceID();
            int old;
            if (!lastColors.TryGetValue(id, out old))
            {
                lastColors[id] = color;
                continue;
            }
            if (old == color) continue;
            lastColors[id] = color;
            Log.LogInfo("Detected color change: " + obj.GetType().FullName + " '" + obj.name + "' " + old + " -> " + color);
            Propagate(obj, color);
        }
    }

    private void Propagate(object source, int color)
    {
        object network;
        string networkKind;
        if (!TryGetNetwork(source, out network, out networkKind))
        {
            Log.LogInfo("Changed structure has no readable cable/pipe/chute/structure network.");
            return;
        }
        Log.LogInfo("Found " + networkKind + ": " + network.GetType().FullName);

        IEnumerable members;
        if (!TryGetMembers(network, out members))
        {
            Log.LogWarning("Could not read network member list from " + network.GetType().FullName);
            DumpCandidateMembers(network.GetType(), "network");
            return;
        }

        int found = 0, changed = 0, failed = 0;
        foreach (object member in members)
        {
            if (member == null) continue;
            found++;
            int current;
            if (TryGetColor(member, out current) && current == color) continue;
            if (TrySetColor(member, color))
            {
                changed++;
                UnityEngine.Object uo = member as UnityEngine.Object;
                if (uo != null) lastColors[uo.GetInstanceID()] = color;
            }
            else failed++;
        }
        Log.LogInfo("Network paint complete: found " + found + " member(s), changed " + changed + ", failed " + failed + ".");
    }

    private bool TryGetColor(object obj, out int color)
    {
        color = -1;
        try
        {
            MethodInfo m = FindZeroArgMethod(obj.GetType(), "get_CustomColorIndex");
            if (m == null) return false;
            object v = m.Invoke(obj, null);
            color = Convert.ToInt32(v);
            return true;
        }
        catch { return false; }
    }

    private bool TrySetColor(object obj, int color)
    {
        Type t = obj.GetType();
        try
        {
            foreach (MethodInfo m in GetAllMethods(t, "SetCustomColor"))
            {
                ParameterInfo[] p = m.GetParameters();
                if (p.Length < 1 || p.Length > 2) continue;
                object c;
                if (!TryConvert(color, p[0].ParameterType, out c)) continue;
                object[] args;
                if (p.Length == 1) args = new object[] { c };
                else
                {
                    object second = p[1].HasDefaultValue ? p[1].DefaultValue : DefaultValue(p[1].ParameterType);
                    args = new object[] { c, second };
                }
                m.Invoke(obj, args);
                return true;
            }

            MethodInfo setter = FindOneArgMethod(t, "set_CustomColorIndex");
            if (setter != null)
            {
                ParameterInfo p = setter.GetParameters()[0];
                object c;
                if (TryConvert(color, p.ParameterType, out c))
                {
                    setter.Invoke(obj, new object[] { c });
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Set color failed on " + t.FullName + ": " + e.GetType().Name + " - " + e.Message);
        }
        return false;
    }

    private bool TryGetNetwork(object obj, out object network, out string kind)
    {
        string[] names = { "get_CableNetwork", "get_PipeNetwork", "get_ChuteNetwork", "get_StructureNetwork" };
        foreach (string name in names)
        {
            try
            {
                MethodInfo m = FindZeroArgMethod(obj.GetType(), name);
                if (m == null) continue;
                object n = m.Invoke(obj, null);
                if (n == null) continue;
                network = n;
                kind = name.Substring(4);
                return true;
            }
            catch { }
        }
        network = null;
        kind = null;
        return false;
    }

    private bool TryGetMembers(object network, out IEnumerable members)
    {
        members = null;
        try
        {
            MethodInfo getter = FindZeroArgMethod(network.GetType(), "get_StructureList");
            if (getter != null)
            {
                object v = getter.Invoke(network, null);
                members = v as IEnumerable;
                if (members != null) return true;
            }
        }
        catch { }

        string[] methodNames = { "get_Structures", "get_ConnectedStructures", "get_Members" };
        foreach (string name in methodNames)
        {
            try
            {
                MethodInfo m = FindZeroArgMethod(network.GetType(), name);
                if (m == null) continue;
                IEnumerable e = m.Invoke(network, null) as IEnumerable;
                if (e != null) { members = e; return true; }
            }
            catch { }
        }
        return false;
    }

    private static Type FindType(string fullName)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = a.GetType(fullName, false);
            if (t != null) return t;
        }
        return null;
    }

    private static MethodInfo FindZeroArgMethod(Type t, string name)
    {
        while (t != null)
        {
            MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m != null) return m;
            t = t.BaseType;
        }
        return null;
    }

    private static MethodInfo FindOneArgMethod(Type t, string name)
    {
        while (t != null)
        {
            MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (MethodInfo m in methods)
                if (m.Name == name && m.GetParameters().Length == 1) return m;
            t = t.BaseType;
        }
        return null;
    }

    private static IEnumerable<MethodInfo> GetAllMethods(Type t, string name)
    {
        while (t != null)
        {
            MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (MethodInfo m in methods)
                if (m.Name == name) yield return m;
            t = t.BaseType;
        }
    }

    private static bool TryConvert(int value, Type type, out object result)
    {
        try
        {
            if (type.IsEnum) result = Enum.ToObject(type, value);
            else result = Convert.ChangeType(value, type);
            return true;
        }
        catch { result = null; return false; }
    }

    private static object DefaultValue(Type t)
    {
        if (!t.IsValueType) return null;
        return Activator.CreateInstance(t);
    }

    private void DumpCandidateMembers(Type t, string label)
    {
        try
        {
            List<string> hits = new List<string>();
            Type cur = t;
            while (cur != null)
            {
                foreach (MethodInfo m in cur.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    string n = m.Name.ToLowerInvariant();
                    if (n.Contains("struct") || n.Contains("network") || n.Contains("member")) hits.Add(m.Name);
                }
                cur = cur.BaseType;
            }
            Log.LogInfo("Candidate " + label + " methods: " + string.Join(", ", hits.ToArray()));
        }
        catch { }
    }
}
