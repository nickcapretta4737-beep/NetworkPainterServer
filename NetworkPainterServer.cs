using BepInEx;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NetworkPainterServer
{
    [BepInPlugin("net.dominic.stationeers.networkpainter.server", "Network Painter Server", "2.0.0")]
    public sealed class NetworkPainterServerPlugin : BaseUnityPlugin
    {
        internal static NetworkPainterServerPlugin Instance;
        private Harmony _harmony;
        private Type _structureType;
        private readonly Queue<object> _pending = new Queue<object>();
        private readonly HashSet<int> _queued = new HashSet<int>();
        private bool _propagating;
        private float _nextProcess;

        private static readonly string[] NetworkNames = { "CableNetwork", "PipeNetwork", "ChuteNetwork" };

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo("Network Painter Server 2.0.0 starting");
            _structureType = FindType("Assets.Scripts.Objects.Structure") ?? FindTypeByName("Structure");
            if (_structureType == null)
            {
                Logger.LogError("Could not find Stationeers Structure type; plugin disabled.");
                enabled = false;
                return;
            }

            _harmony = new Harmony("net.dominic.stationeers.networkpainter.server");
            int patched = PatchColorMethods();
            if (patched == 0)
            {
                Logger.LogError("No color-change methods could be patched; plugin disabled.");
                enabled = false;
                return;
            }

            Logger.LogInfo("Network Painter Server ready. Patched " + patched + " color method(s). Vanilla clients require no mod.");
        }

        private int PatchColorMethods()
        {
            var seen = new HashSet<MethodBase>();
            var targets = new List<MethodBase>();
            for (Type t = _structureType; t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == "SetCustomColor" || m.Name == "SetCustomColour" || m.Name == "set_CustomColorIndex" || m.Name == "set_CustomColourIndex")
                    {
                        if (seen.Add(m)) targets.Add(m);
                    }
                }
            }

            var postfix = new HarmonyMethod(typeof(NetworkPainterServerPlugin).GetMethod(nameof(ColorChangedPostfix), BindingFlags.Static | BindingFlags.NonPublic));
            int count = 0;
            foreach (var m in targets)
            {
                try
                {
                    _harmony.Patch(m, postfix: postfix);
                    Logger.LogInfo("Patched color hook: " + m.DeclaringType.FullName + "." + m.Name);
                    count++;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("Could not patch " + m.DeclaringType.FullName + "." + m.Name + ": " + e.Message);
                }
            }
            return count;
        }

        private static void ColorChangedPostfix(object __instance)
        {
            var p = Instance;
            if (p == null || p._propagating || __instance == null) return;
            if (!p._structureType.IsInstanceOfType(__instance)) return;
            p.Queue(__instance);
        }

        private void Queue(object structure)
        {
            var uo = structure as UnityEngine.Object;
            if (uo == null) return;
            int id = uo.GetInstanceID();
            if (_queued.Add(id)) _pending.Enqueue(structure);
        }

        private void Update()
        {
            if (_propagating || _pending.Count == 0 || Time.unscaledTime < _nextProcess) return;
            _nextProcess = Time.unscaledTime + 0.05f;

            object source = _pending.Dequeue();
            var uo = source as UnityEngine.Object;
            if (uo != null) _queued.Remove(uo.GetInstanceID());
            if (uo == null) return;

            object color;
            if (!TryGetColor(source, out color)) return;
            Propagate(source, color);
        }

        private void Propagate(object source, object color)
        {
            var networks = GetNetworks(source).Where(n => n != null).Distinct().ToArray();
            if (networks.Length == 0) return;

            _propagating = true;
            try
            {
                int touched = 0;
                var done = new HashSet<int>();
                foreach (var network in networks)
                {
                    foreach (var member in GetStructureList(network))
                    {
                        var uo = member as UnityEngine.Object;
                        if (uo == null) continue;
                        int id = uo.GetInstanceID();
                        if (!done.Add(id)) continue;

                        object existing;
                        if (TryGetColor(member, out existing) && ColorsEqual(existing, color)) continue;
                        if (ApplyColor(member, color)) touched++;
                    }
                }
                if (touched > 0) Logger.LogInfo("Network paint propagated to " + touched + " connected structure(s).");
            }
            catch (Exception e)
            {
                Logger.LogError("Network paint failed: " + e);
            }
            finally
            {
                _propagating = false;
                _pending.Clear();
                _queued.Clear();
            }
        }

        private IEnumerable<object> GetNetworks(object structure)
        {
            Type t = structure.GetType();
            foreach (string name in NetworkNames)
            {
                object network = GetMemberValue(t, structure, name);
                if (network != null) yield return network;
            }
        }

        private IEnumerable<object> GetStructureList(object network)
        {
            object list = GetMemberValue(network.GetType(), network, "StructureList");
            var enumerable = list as IEnumerable;
            if (enumerable == null) yield break;
            foreach (object item in enumerable) if (item != null) yield return item;
        }

        private static bool TryGetColor(object obj, out object color)
        {
            Type t = obj.GetType();
            color = GetMemberValue(t, obj, "CustomColorIndex") ??
                    GetMemberValue(t, obj, "CustomColourIndex") ??
                    GetMemberValue(t, obj, "customColourIndex") ??
                    GetMemberValue(t, obj, "ColorIndex") ??
                    GetMemberValue(t, obj, "ColourIndex");
            return color != null;
        }

        private static bool ApplyColor(object obj, object color)
        {
            Type t = obj.GetType();
            foreach (var m in EnumerateMethods(t, "SetCustomColor", "SetCustomColour").OrderBy(x => x.GetParameters().Length))
            {
                var p = m.GetParameters();
                if (p.Length == 0) continue;
                object first;
                if (!TryConvert(color, p[0].ParameterType, out first)) continue;

                var args = new object[p.Length];
                args[0] = first;
                for (int i = 1; i < p.Length; i++)
                {
                    if (p[i].HasDefaultValue) args[i] = p[i].DefaultValue;
                    else if (p[i].ParameterType == typeof(bool)) args[i] = true;
                    else if (p[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(p[i].ParameterType);
                    else args[i] = null;
                }
                try
                {
                    m.Invoke(obj, args);
                    return true;
                }
                catch { }
            }

            return SetMemberValue(t, obj, "CustomColorIndex", color) ||
                   SetMemberValue(t, obj, "CustomColourIndex", color) ||
                   SetMemberValue(t, obj, "customColourIndex", color);
        }

        private static IEnumerable<MethodInfo> EnumerateMethods(Type t, params string[] names)
        {
            var set = new HashSet<string>(names);
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                foreach (var m in cur.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (set.Contains(m.Name)) yield return m;
            }
        }

        private static object GetMemberValue(Type t, object instance, string name)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                try
                {
                    var p = cur.GetProperty(name, f);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(instance, null);
                    var field = cur.GetField(name, f);
                    if (field != null) return field.GetValue(instance);
                }
                catch { }
            }
            return null;
        }

        private static bool SetMemberValue(Type t, object instance, string name, object value)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                try
                {
                    var p = cur.GetProperty(name, f);
                    if (p != null && p.CanWrite)
                    {
                        object converted;
                        if (TryConvert(value, p.PropertyType, out converted)) { p.SetValue(instance, converted, null); return true; }
                    }
                    var field = cur.GetField(name, f);
                    if (field != null && !field.IsInitOnly)
                    {
                        object converted;
                        if (TryConvert(value, field.FieldType, out converted)) { field.SetValue(instance, converted); return true; }
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TryConvert(object value, Type target, out object converted)
        {
            converted = null;
            if (value == null) return !target.IsValueType;
            Type src = value.GetType();
            if (target.IsAssignableFrom(src)) { converted = value; return true; }
            try
            {
                if (target.IsEnum) { converted = Enum.ToObject(target, Convert.ToInt32(value)); return true; }
                if (src.IsEnum) { converted = Convert.ChangeType(Convert.ToInt32(value), target); return true; }
                converted = Convert.ChangeType(value, target);
                return true;
            }
            catch { return false; }
        }

        private static bool ColorsEqual(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            try { return Convert.ToInt32(a) == Convert.ToInt32(b); }
            catch { return a.Equals(b); }
        }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType(fullName, false); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        private static Type FindTypeByName(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    foreach (var t in a.GetTypes()) if (t.Name == name && typeof(UnityEngine.Object).IsAssignableFrom(t)) return t;
                }
                catch { }
            }
            return null;
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }
}
