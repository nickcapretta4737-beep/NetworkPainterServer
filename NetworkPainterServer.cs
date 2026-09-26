using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "7.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Type ColorMessageType;
    internal static Type ThingType;
    internal static Type StructureType;
    internal static bool Propagating;
    private Harmony harmony;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 7.0.0 starting");

        Assembly game = null;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = SafeType(a, "Assets.Scripts.Networking.ThingColorMessage");
            if (t != null)
            {
                game = a;
                ColorMessageType = t;
                break;
            }
        }

        if (game == null || ColorMessageType == null)
        {
            Log.LogError("ThingColorMessage could not be found.");
            return;
        }

        ThingType = SafeType(game, "Assets.Scripts.Objects.Thing");
        StructureType = SafeType(game, "Assets.Scripts.Objects.Structure");

        Log.LogInfo("ThingColorMessage: " + ColorMessageType.FullName);
        Log.LogInfo("ThingColorMessage fields:");
        foreach (FieldInfo f in SafeFields(ColorMessageType))
            Log.LogInfo("  " + f.FieldType.FullName + " " + f.Name);

        harmony = new Harmony("dominic.networkpainterserver.v7");

        int messageMethodHooks = PatchMessageTypeChain();
        int directHandlers = PatchExactMessageHandlers(game);

        Log.LogInfo("Network Painter Server ready. Message-type hooks=" +
            messageMethodHooks + ", direct handlers=" + directHandlers);
    }

    private void OnDestroy()
    {
        try
        {
            if (harmony != null) harmony.UnpatchSelf();
        }
        catch { }
    }

    private int PatchMessageTypeChain()
    {
        int count = 0;
        HashSet<MethodBase> seen = new HashSet<MethodBase>();

        Type t = ColorMessageType;
        while (t != null && t != typeof(object))
        {
            Log.LogInfo("Inspecting message type: " + t.FullName);

            foreach (MethodInfo m in SafeDeclaredMethods(t))
            {
                Log.LogInfo("  method: " + Describe(m));

                if (!ShouldPatchMessageMethod(m)) continue;
                if (!seen.Add(m)) continue;

                try
                {
                    harmony.Patch(
                        m,
                        prefix: new HarmonyMethod(typeof(NetworkPainterServer), nameof(MessagePrefix)));
                    count++;
                    Log.LogInfo("PATCHED MESSAGE METHOD: " + Describe(m));
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not patch message method " + Describe(m) +
                        ": " + e.GetType().Name + " - " + e.Message);
                }
            }

            t = t.BaseType;
        }

        return count;
    }

    private int PatchExactMessageHandlers(Assembly game)
    {
        int count = 0;

        foreach (Type t in SafeTypes(game))
        {
            foreach (MethodInfo m in SafeDeclaredMethods(t))
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;

                bool exact = false;
                foreach (ParameterInfo p in SafeParameters(m))
                {
                    Type pt = p.ParameterType;
                    if (pt.IsByRef) pt = pt.GetElementType();
                    if (pt == ColorMessageType)
                    {
                        exact = true;
                        break;
                    }
                }

                if (!exact) continue;

                try
                {
                    harmony.Patch(
                        m,
                        prefix: new HarmonyMethod(typeof(NetworkPainterServer), nameof(HandlerPrefix)));
                    count++;
                    Log.LogInfo("PATCHED DIRECT HANDLER: " + Describe(m));
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not patch direct handler " + Describe(m) +
                        ": " + e.GetType().Name + " - " + e.Message);
                }
            }
        }

        return count;
    }

    private static bool ShouldPatchMessageMethod(MethodInfo m)
    {
        if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.IsSpecialName)
            return false;

        string n = m.Name.ToLowerInvariant();

        return n.Contains("process") ||
               n.Contains("handle") ||
               n.Contains("receive") ||
               n.Contains("execute") ||
               n.Contains("apply") ||
               n.Contains("invoke") ||
               n.Contains("run") ||
               n.Contains("dispatch") ||
               n.Contains("read") ||
               n.Contains("write") ||
               n.Contains("serialize") ||
               n.Contains("deserialize");
    }

    public static void MessagePrefix(object __instance, object[] __args, MethodBase __originalMethod)
    {
        if (Propagating) return;

        try
        {
            Log.LogInfo("MESSAGE METHOD FIRED: " + Describe(__originalMethod));

            object msg = null;
            if (__instance != null && ColorMessageType.IsInstanceOfType(__instance))
                msg = __instance;

            if (msg == null && __args != null)
            {
                foreach (object a in __args)
                {
                    if (a != null && ColorMessageType.IsInstanceOfType(a))
                    {
                        msg = a;
                        break;
                    }
                }
            }

            if (msg != null)
                HandleThingColorMessage(msg);
        }
        catch (Exception e)
        {
            Log.LogError("MessagePrefix error: " + e);
        }
    }

    public static void HandlerPrefix(object[] __args, MethodBase __originalMethod)
    {
        if (Propagating) return;

        try
        {
            Log.LogInfo("DIRECT HANDLER FIRED: " + Describe(__originalMethod));

            if (__args == null) return;

            foreach (object a in __args)
            {
                if (a != null && ColorMessageType.IsInstanceOfType(a))
                {
                    HandleThingColorMessage(a);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Log.LogError("HandlerPrefix error: " + e);
        }
    }

    private static void HandleThingColorMessage(object msg)
    {
        object thingId = ReadFieldOrProperty(msg, "ThingId");
        object colorIndex = ReadFieldOrProperty(msg, "ColorIndex");

        Log.LogInfo("THING COLOR MESSAGE: ThingId=" + SafeValue(thingId) +
            " ColorIndex=" + SafeValue(colorIndex));

        if (thingId == null || colorIndex == null)
        {
            Log.LogWarning("ThingColorMessage missing ThingId or ColorIndex.");
            DumpObject(msg, "ThingColorMessage");
            return;
        }

        object source = FindThingById(thingId);
        if (source == null)
        {
            Log.LogWarning("Could not resolve ThingId " + SafeValue(thingId));
            return;
        }

        Log.LogInfo("Resolved painted object: " + source.GetType().FullName);
        Propagate(source, colorIndex);
    }

    private static object FindThingById(object wanted)
    {
        if (ThingType == null) return null;

        long target;
        try { target = Convert.ToInt64(wanted); }
        catch { return null; }

        UnityEngine.Object[] things;
        try
        {
            things = UnityEngine.Object.FindObjectsOfType(ThingType);
        }
        catch (Exception e)
        {
            Log.LogWarning("FindObjectsOfType failed: " + e.Message);
            return null;
        }

        string[] idNames =
        {
            "ReferenceId",
            "ReferenceID",
            "ThingId",
            "Id",
            "ID"
        };

        foreach (UnityEngine.Object thing in things)
        {
            foreach (string name in idNames)
            {
                object id = ReadFieldOrProperty(thing, name);
                if (id == null) continue;

                try
                {
                    if (Convert.ToInt64(id) == target)
                        return thing;
                }
                catch { }
            }
        }

        return null;
    }

    private static void Propagate(object source, object color)
    {
        object network = GetNetwork(source);
        if (network == null)
        {
            Log.LogInfo("Painted object has no supported network: " +
                source.GetType().FullName);
            DumpNetworkLikeMethods(source.GetType());
            return;
        }

        IEnumerable list = GetStructureList(network);
        if (list == null)
        {
            Log.LogWarning("Found network " + network.GetType().FullName +
                " but could not get StructureList.");
            DumpObject(network, "network");
            return;
        }

        int members = 0;
        int changed = 0;
        int failed = 0;

        Propagating = true;
        try
        {
            foreach (object member in list)
            {
                if (member == null) continue;
                members++;

                object old = ReadColor(member);
                if (SameColor(old, color)) continue;

                if (ApplyColor(member, color)) changed++;
                else failed++;
            }
        }
        finally
        {
            Propagating = false;
        }

        Log.LogInfo("NETWORK PAINT COMPLETE: members=" + members +
            " changed=" + changed + " failed=" + failed +
            " color=" + SafeValue(color));
    }

    private static object GetNetwork(object source)
    {
        string[] getters =
        {
            "get_StructureNetwork",
            "get_CableNetwork",
            "get_PipeNetwork",
            "get_ChuteNetwork"
        };

        foreach (string name in getters)
        {
            MethodInfo m = FindMethod(source.GetType(), name, 0);
            if (m == null) continue;

            try
            {
                object n = m.Invoke(source, null);
                if (n != null)
                {
                    Log.LogInfo("Network resolved by " + name + ": " +
                        n.GetType().FullName);
                    return n;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(name + " failed: " + e.Message);
            }
        }

        return null;
    }

    private static IEnumerable GetStructureList(object network)
    {
        MethodInfo m = FindMethod(network.GetType(), "get_StructureList", 0);
        if (m != null)
        {
            try
            {
                object v = m.Invoke(network, null);
                if (v is IEnumerable e) return e;
            }
            catch { }
        }

        object direct = ReadFieldOrProperty(network, "StructureList");
        return direct as IEnumerable;
    }

    private static object ReadColor(object obj)
    {
        string[] names =
        {
            "CustomColorIndex",
            "CustomColourIndex",
            "ColorIndex",
            "ColourIndex"
        };

        foreach (string n in names)
        {
            object v = ReadFieldOrProperty(obj, n);
            if (v != null) return v;
        }

        return null;
    }

    private static bool ApplyColor(object target, object color)
    {
        foreach (MethodInfo m in FindMethods(target.GetType(), "SetCustomColor"))
        {
            ParameterInfo[] p = SafeParameters(m);
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
                Type pt = SafeParameters(setter)[0].ParameterType;
                setter.Invoke(target, new object[] { ConvertValue(color, pt) });
                return true;
            }
            catch { }
        }

        return false;
    }

    private static object ReadFieldOrProperty(object obj, string name)
    {
        if (obj == null) return null;

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

                if (f != null) return f.GetValue(obj);
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
                    return p.GetValue(obj, null);
            }
            catch { }

            MethodInfo getter = FindDeclaredMethod(t, "get_" + name, 0);
            if (getter != null)
            {
                try { return getter.Invoke(obj, null); }
                catch { }
            }

            t = t.BaseType;
        }

        return null;
    }

    private static MethodInfo FindMethod(Type t, string name, int argc)
    {
        Type cur = t;
        while (cur != null)
        {
            MethodInfo m = FindDeclaredMethod(cur, name, argc);
            if (m != null) return m;
            cur = cur.BaseType;
        }
        return null;
    }

    private static MethodInfo FindDeclaredMethod(Type t, string name, int argc)
    {
        foreach (MethodInfo m in SafeDeclaredMethods(t))
            if (m.Name == name && SafeParameters(m).Length == argc)
                return m;
        return null;
    }

    private static IEnumerable<MethodInfo> FindMethods(Type t, string name)
    {
        Type cur = t;
        while (cur != null)
        {
            foreach (MethodInfo m in SafeDeclaredMethods(cur))
                if (m.Name == name) yield return m;
            cur = cur.BaseType;
        }
    }

    private static object ConvertValue(object value, Type target)
    {
        if (target.IsByRef) target = target.GetElementType();

        if (value == null)
            return target.IsValueType ? Activator.CreateInstance(target) : null;

        Type source = value.GetType();
        if (target.IsAssignableFrom(source)) return value;

        if (target.IsEnum)
            return Enum.ToObject(target, Convert.ToInt32(value));

        if (source.IsEnum)
            return Convert.ChangeType(Convert.ToInt32(value), target);

        return Convert.ChangeType(value, target);
    }

    private static bool SameColor(object a, object b)
    {
        if (a == null || b == null) return false;
        try { return Convert.ToInt32(a) == Convert.ToInt32(b); }
        catch { return a.Equals(b); }
    }

    private static Type SafeType(Assembly a, string name)
    {
        try { return a.GetType(name, false); }
        catch { return null; }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(x => x != null);
        }
        catch
        {
            return new Type[0];
        }
    }

    private static MethodInfo[] SafeDeclaredMethods(Type t)
    {
        try
        {
            return t.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
        }
        catch
        {
            return new MethodInfo[0];
        }
    }

    private static FieldInfo[] SafeFields(Type t)
    {
        try
        {
            return t.GetFields(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            return new FieldInfo[0];
        }
    }

    private static ParameterInfo[] SafeParameters(MethodInfo m)
    {
        try { return m.GetParameters(); }
        catch { return new ParameterInfo[0]; }
    }

    private static string Describe(MethodBase m)
    {
        if (m == null) return "?";

        string[] p;
        try
        {
            p = m.GetParameters()
                .Select(x => x.ParameterType.Name + " " + x.Name)
                .ToArray();
        }
        catch
        {
            p = new string[0];
        }

        return (m.DeclaringType != null ? m.DeclaringType.FullName : "?") +
            "." + m.Name + "(" + string.Join(", ", p) + ")";
    }

    private static void DumpObject(object obj, string label)
    {
        if (obj == null) return;

        Type t = obj.GetType();
        Log.LogInfo(label + " type: " + t.FullName);

        foreach (FieldInfo f in SafeFields(t))
        {
            try
            {
                Log.LogInfo("  field " + f.Name + "=" +
                    SafeValue(f.GetValue(obj)));
            }
            catch { }
        }
    }

    private static void DumpNetworkLikeMethods(Type t)
    {
        Log.LogInfo("Network-like methods on " + t.FullName + ":");
        Type cur = t;

        while (cur != null)
        {
            foreach (MethodInfo m in SafeDeclaredMethods(cur))
            {
                string n = m.Name;
                if (n.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Cable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Chute", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log.LogInfo("  " + Describe(m));
            }
            cur = cur.BaseType;
        }
    }

    private static string SafeValue(object v)
    {
        if (v == null) return "null";
        try { return v.ToString(); }
        catch { return "<unprintable>"; }
    }
}
