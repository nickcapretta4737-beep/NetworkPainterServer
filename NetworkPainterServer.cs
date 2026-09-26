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
