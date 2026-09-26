using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "3.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    private static ManualLogSource Log;
    private readonly Dictionary<int, int> lastColors = new Dictionary<int, int>();
    private float nextScan;
    private bool baselineReady;
    private Type structureType;
    private PropertyInfo customColorProp;
    private PropertyInfo cableNetworkProp;
    private PropertyInfo pipeNetworkProp;
    private PropertyInfo chuteNetworkProp;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 3.0.0 starting");
        structureType = AccessTools.TypeByName("Assets.Scripts.Objects.Structure");
        if (structureType == null)
        {
            Log.LogError("Could not find Assets.Scripts.Objects.Structure");
            return;
        }
        customColorProp = AccessTools.Property(structureType, "CustomColorIndex") ?? AccessTools.Property(structureType.BaseType, "CustomColorIndex");
        cableNetworkProp = AccessTools.Property(structureType, "CableNetwork") ?? AccessTools.Property(structureType.BaseType, "CableNetwork");
        pipeNetworkProp = AccessTools.Property(structureType, "PipeNetwork") ?? AccessTools.Property(structureType.BaseType, "PipeNetwork");
        chuteNetworkProp = AccessTools.Property(structureType, "ChuteNetwork") ?? AccessTools.Property(structureType.BaseType, "ChuteNetwork");
        Log.LogInfo("Structure type: " + structureType.FullName);
        Log.LogInfo("Network Painter Server ready. Watching server-side color state...");
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextScan) return;
        nextScan = Time.realtimeSinceStartup + 0.25f;
        ScanStructures();
    }

    private void ScanStructures()
    {
        if (structureType == null || customColorProp == null) return;
        UnityEngine.Object[] objs;
        try { objs = UnityEngine.Object.FindObjectsOfType(structureType); }
        catch (Exception e)
        {
            Log.LogError("FindObjectsOfType failed: " + e.Message);
            return;
        }
        if (!baselineReady)
        {
            lastColors.Clear();
            foreach (var o in objs) lastColors[o.GetInstanceID()] = ReadColor(o);
            baselineReady = true;
            Log.LogInfo("Color baseline ready. Tracking " + lastColors.Count + " structures.");
            return;
        }
        foreach (var o in objs)
        {
            int id = o.GetInstanceID();
            int color = ReadColor(o);
            if (!lastColors.TryGetValue(id, out int oldColor))
            {
                lastColors[id] = color;
                continue;
            }
            if (color == oldColor) continue;
            lastColors[id] = color;
            Log.LogInfo("Detected painted network piece: " + o.name + " color " + oldColor + " -> " + color);
            PropagateNetworkColor(o, color);
        }
    }

    private int ReadColor(object obj)
    {
        try { return Convert.ToInt32(customColorProp.GetValue(obj, null)); }
        catch { return -999; }
    }

    private void PropagateNetworkColor(object source, int color)
    {
        object network = GetNetwork(source);
        if (network == null)
        {
            Log.LogInfo("Painted piece has no cable/pipe/chute network.");
            return;
        }
        IEnumerable members = GetMembers(network);
        if (members == null)
        {
            Log.LogWarning("Could not read network members from " + network.GetType().FullName);
            return;
        }
        int found = 0;
        int changed = 0;
        foreach (object member in members)
        {
            if (member == null) continue;
            found++;
            int current = ReadColor(member);
            if (current == color) continue;
            if (ApplyColor(member, color))
            {
                changed++;
                if (member is UnityEngine.Object uo) lastColors[uo.GetInstanceID()] = color;
            }
        }
        Log.LogInfo("Network paint: found " + found + " member(s), changed " + changed + ".");
    }

    private object GetNetwork(object obj)
    {
        try { object n = cableNetworkProp?.GetValue(obj, null); if (n != null) return n; } catch { }
        try { object n = pipeNetworkProp?.GetValue(obj, null); if (n != null) return n; } catch { }
        try { object n = chuteNetworkProp?.GetValue(obj, null); if (n != null) return n; } catch { }
        return null;
    }

    private IEnumerable GetMembers(object network)
    {
        Type t = network.GetType();
        string[] names = { "StructureList", "Structures", "NetworkList", "ConnectedStructures", "Members" };
        foreach (string name in names)
        {
            try
            {
                PropertyInfo p = AccessTools.Property(t, name);
                if (p != null && p.GetValue(network, null) is IEnumerable pe) return pe;
                FieldInfo f = AccessTools.Field(t, name);
                if (f != null && f.GetValue(network) is IEnumerable fe) return fe;
            }
            catch { }
        }
        return null;
    }

    private bool ApplyColor(object obj, int color)
    {
        try
        {
            MethodInfo m = AccessTools.Method(obj.GetType(), "SetCustomColor") ?? AccessTools.Method(obj.GetType().BaseType, "SetCustomColor");
            if (m != null)
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1)
                {
                    m.Invoke(obj, new object[] { ConvertArgument(color, ps[0].ParameterType) });
                    return true;
                }
                if (ps.Length == 2)
                {
                    object arg0 = ConvertArgument(color, ps[0].ParameterType);
                    object arg1 = ps[1].ParameterType == typeof(bool) ? (object)true : ps[1].HasDefaultValue ? ps[1].DefaultValue : Activator.CreateInstance(ps[1].ParameterType);
                    m.Invoke(obj, new object[] { arg0, arg1 });
                    return true;
                }
            }
            PropertyInfo p = AccessTools.Property(obj.GetType(), "CustomColorIndex") ?? AccessTools.Property(obj.GetType().BaseType, "CustomColorIndex");
            if (p != null && p.CanWrite)
            {
                p.SetValue(obj, ConvertArgument(color, p.PropertyType), null);
                return true;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Failed to apply color: " + e.GetType().Name + ": " + e.Message);
        }
        return false;
    }

    private object ConvertArgument(int color, Type targetType)
    {
        if (targetType == typeof(int)) return color;
        if (targetType.IsEnum) return Enum.ToObject(targetType, color);
        return Convert.ChangeType(color, targetType);
    }
}
