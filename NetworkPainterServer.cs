using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "6.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static bool Propagating;
    private Harmony harmony;
    private static Type thingType;
    private static Type structureType;
    private static Type colorMessageType;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 6.0.0 starting");

        Assembly game = typeof(BaseUnityPlugin).Assembly;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type probe = SafeGetType(a, "Assets.Scripts.Networking.ThingColorMessage");
            if (probe != null)
            {
                game = a;
                colorMessageType = probe;
                break;
            }
        }

        thingType = SafeGetType(game, "Assets.Scripts.Objects.Thing");
        structureType = SafeGetType(game, "Assets.Scripts.Objects.Structure");

        if (colorMessageType == null)
        {
            Log.LogError("ThingColorMessage type not found.");
            return;
        }

        Log.LogInfo("ThingColorMessage type: " + colorMessageType.FullName);
        DumpColorMessageMembers();

        harmony = new Harmony("dominic.networkpainterserver.v6");

        int messageHooks = PatchMessageHandlers(game);
        int sprayHooks = PatchSprayHooks(game);

        Log.LogInfo("Network Painter Server ready. Message hooks=" + messageHooks + ", spray hooks=" + sprayHooks);
    }

    private void OnDestroy()
    {
        try
        {
            if (harmony != null) harmony.UnpatchSelf();
        }
        catch { }
    }

    private int PatchMessageHandlers(Assembly game)
    {
        int count = 0;

        foreach (Type t in SafeTypes(game))
        {
            foreach (MethodInfo m in SafeMethods(t))
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;

                bool match = false;
                foreach (ParameterInfo p in SafeParameters(m))
                {
                    Type pt = p.ParameterType;
                    if (pt.IsByRef) pt = pt.GetElementType();
                    if (pt == null) continue;

                    if (pt == colorMessageType)
                    {
                        match = true;
                        break;
                    }

                    string pn = pt.FullName ?? pt.Name;
                    if (pn.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            if (pt.IsAssignableFrom(colorMessageType))
                            {
                                match = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (!match) continue;

                try
                {
                    harmony.Patch(
                        m,
                        postfix: new HarmonyMethod(typeof(NetworkPainterServer), nameof(MessagePostfix)));
                    count++;
                    Log.LogInfo("Patched message handler: " + Describe(m));
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not patch message handler " + Describe(m) + ": " + e.Message);
                }
            }
        }

        return count;
    }

    private int PatchSprayHooks(Assembly game)
    {
        int count = 0;

        foreach (Type t in SafeTypes(game))
        {
            string tn = t.FullName ?? t.Name;
            bool interestingType =
                tn.IndexOf("SprayGun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("ISprayer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn == "Assets.Scripts.Objects.Thing" ||
                tn == "Assets.Scripts.Objects.Structure";

            if (!interestingType) continue;

            foreach (MethodInfo m in SafeMethods(t))
            {
                string n = m.Name;
                if (n.IndexOf("Spray", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Paint", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (m.IsAbstract || m.ContainsGenericParameters) continue;

                try
                {
                    harmony.Patch(
                        m,
                        postfix: new HarmonyMethod(typeof(NetworkPainterServer), nameof(SprayPostfix)));
                    count++;
                    Log.LogInfo("Patched spray hook: " + Describe(m));
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not patch spray hook " + Describe(m) + ": " + e.Message);
                }
            }
        }

        return count;
    }

    public static void MessagePostfix(object __instance, object[] __args)
    {
        if (Propagating) return;

        try
        {
            object msg = FindColorMessage(__instance, __args);
            if (msg == null) return;

            Log.LogInfo("ThingColorMessage observed by server.");
            HandleMessage(msg, __instance, __args);
        }
        catch (Exception e)
        {
            Log.LogError("ThingColorMessage hook error: " + e);
        }
    }

    public static void SprayPostfix(object __instance, object[] __args)
    {
        if (Propagating) return;

        try
        {
            object source = FindStructure(__instance, __args);
            if (source == null) return;

            object color = ReadColor(source);
            if (color == null) return;

            Log.LogInfo("Spray hook observed structure " + source.GetType().FullName + ", color=" + SafeValue(color));
            Propagate(source, color);
        }
        catch (Exception e)
        {
            Log.LogError("Spray hook error: " + e);
        }
    }

    private static void HandleMessage(object msg, object instance, object[] args)
    {
        object source = FindStructure(instance, args);
        object id = ReadNamedValue(msg,
            "ThingId", "ReferenceId", "ReferenceID", "Id", "ID", "TargetId", "TargetID");

        object color = ReadNamedValue(msg,
            "ColorIndex", "ColourIndex", "CustomColorIndex", "colorIndex",
            "colourIndex", "customColourIndex", "Color", "Colour");

        Log.LogInfo("ThingColorMessage values: id=" + SafeValue(id) + ", color=" + SafeValue(color));

        if (source == null && id != null)
            source = FindThingByReferenceId(id);

        if (source == null)
        {
            Log.LogWarning("ThingColorMessage received, but painted Thing could not be resolved.");
            DumpObject(msg, "ThingColorMessage");
            return;
        }

        if (color == null)
            color = ReadColor(source);

        if (color == null)
        {
            Log.LogWarning("Painted Thing resolved, but color could not be read.");
            return;
        }

        Propagate(source, color);
    }

    private static void Propagate(object source, object color)
    {
        object network = GetNetwork(source);
        if (network == null)
        {
            Log.LogInfo("Painted object " + source.GetType().Name + " has no supported structure network.");
            return;
        }

        IEnumerable members = GetStructureList(network);
        if (members == null)
        {
            Log.LogWarning("Network found (" + network.GetType().FullName + ") but StructureList could not be read.");
            DumpObject(network, "network");
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
                if (member == null) continue;
                found++;

                object old = ReadColor(member);
                if (ColorsEqual(old, color)) continue;

                if (ApplyColor(member, color)) changed++;
                else failed++;
            }
        }
        finally
        {
            Propagating = false;
        }

        Log.LogInfo(
            "NETWORK PAINT: network=" + network.GetType().Name +
            " color=" + SafeValue(color) +
            " members=" + found +
            " changed=" + changed +
            " failed=" + failed);
    }

    private static object FindColorMessage(object instance, object[] args)
    {
        if (IsColorMessage(instance)) return instance;

        if (args != null)
            foreach (object a in args)
                if (IsColorMessage(a)) return a;

        return null;
    }

    private static bool IsColorMessage(object obj)
    {
        if (obj == null || colorMessageType == null) return false;
        return colorMessageType.IsInstanceOfType(obj);
    }

    private static object FindStructure(object instance, object[] args)
    {
        object s = AsStructure(instance);
        if (s != null) return s;

        if (args != null)
        {
            foreach (object a in args)
            {
                s = AsStructure(a);
                if (s != null) return s;
            }
        }

        return null;
    }

    private static object AsStructure(object obj)
    {
        if (obj == null || structureType == null) return null;
        return structureType.IsInstanceOfType(obj) ? obj : null;
    }

    private static object FindThingByReferenceId(object wanted)
    {
        if (thingType == null || wanted == null) return null;

        long target;
        try { target = Convert.ToInt64(wanted); }
        catch { return null; }

        UnityEngine.Object[] things;
        try { things = UnityEngine.Object.FindObjectsOfType(thingType); }
        catch (Exception e)
        {
            Log.LogWarning("FindObjectsOfType(Thing) failed: " + e.Message);
            return null;
        }

        foreach (UnityEngine.Object thing in things)
        {
            object id = ReadNamedValue(thing,
                "ReferenceId", "ReferenceID", "ThingId", "Id", "ID");

            if (id == null) continue;

            try
            {
                if (Convert.ToInt64(id) == target)
                {
                    Log.LogInfo("Resolved painted Thing by id: " + thing.GetType().FullName);
                    return thing;
                }
            }
            catch { }
        }

        Log.LogWarning("No live Thing matched paint id " + target + " among " + things.Length + " objects.");
        return null;
    }

    private static object GetNetwork(object source)
    {
        string[] names =
        {
            "get_StructureNetwork",
            "get_CableNetwork",
            "get_PipeNetwork",
            "get_ChuteNetwork",
            "get_RoboticArmNetwork"
        };

        foreach (string name in names)
        {
            MethodInfo m = FindMethod(source.GetType(), name, 0);
            if (m == null) continue;

            try
            {
                object n = m.Invoke(source, null);
                if (n != null)
                {
                    Log.LogInfo("Found network with " + name + ": " + n.GetType().FullName);
                    return n;
                }
            }
            catch { }
        }

        return null;
    }

    private static IEnumerable GetStructureList(object network)
    {
        MethodInfo getter = FindMethod(network.GetType(), "get_StructureList", 0);
        if (getter != null)
        {
            try
            {
                object value = getter.Invoke(network, null);
                if (value is IEnumerable e) return e;
            }
            catch { }
        }

        object direct = ReadNamedValue(network, "StructureList");
        return direct as IEnumerable;
    }

    private static object ReadColor(object obj)
    {
        return ReadNamedValue(obj,
            "CustomColorIndex", "CustomColourIndex",
            "ColorIndex", "ColourIndex",
            "customColourIndex", "colourIndex", "_colorIndex");
    }

    private static bool ApplyColor(object target, object color)
    {
        foreach (MethodInfo m in FindMethods(target.GetType(), "SetCustomColor"))
        {
            ParameterInfo[] p = SafeParameters(m);
            if (p.Length < 1 || p.Length > 3) continue;

            try
            {
                object[] a = new object[p.Length];
                a[0] = ConvertValue(color, p[0].ParameterType);

                for (int i = 1; i < p.Length; i++)
                {
                    if (p[i].ParameterType == typeof(bool)) a[i] = true;
                    else if (p[i].HasDefaultValue) a[i] = p[i].DefaultValue;
                    else if (p[i].ParameterType.IsValueType) a[i] = Activator.CreateInstance(p[i].ParameterType);
                    else a[i] = null;
                }

                m.Invoke(target, a);
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

    private static object ReadNamedValue(object obj, params string[] names)
    {
        if (obj == null) return null;

        Type t = obj.GetType();

        foreach (string name in names)
        {
            MethodInfo getter = FindMethod(t, "get_" + name, 0);
            if (getter != null)
            {
                try { return getter.Invoke(obj, null); }
                catch { }
            }

            Type cur = t;
            while (cur != null)
            {
                try
                {
                    FieldInfo f = cur.GetField(
                        name,
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (f != null) return f.GetValue(obj);
                }
                catch { }

                try
                {
                    PropertyInfo p = cur.GetProperty(
                        name,
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (p != null && p.GetIndexParameters().Length == 0)
                        return p.GetValue(obj, null);
                }
                catch { }

                cur = cur.BaseType;
            }
        }

        return null;
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

    private static bool ColorsEqual(object a, object b)
    {
        if (a == null || b == null) return false;
        try { return Convert.ToInt32(a) == Convert.ToInt32(b); }
        catch { return a.Equals(b); }
    }

    private static MethodInfo FindMethod(Type type, string name, int argc)
    {
        Type cur = type;

        while (cur != null)
        {
            foreach (MethodInfo m in SafeMethods(cur))
                if (m.Name == name && SafeParameters(m).Length == argc)
                    return m;

            cur = cur.BaseType;
        }

        return null;
    }

    private static IEnumerable<MethodInfo> FindMethods(Type type, string name)
    {
        Type cur = type;

        while (cur != null)
        {
            foreach (MethodInfo m in SafeMethods(cur))
                if (m.Name == name) yield return m;

            cur = cur.BaseType;
        }
    }

    private static Type SafeGetType(Assembly a, string name)
    {
        try { return a.GetType(name, false); }
        catch { return null; }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null); }
        catch { return new Type[0]; }
    }

    private static MethodInfo[] SafeMethods(Type t)
    {
        try
        {
            return t.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
        }
        catch { return new MethodInfo[0]; }
    }

    private static ParameterInfo[] SafeParameters(MethodInfo m)
    {
        try { return m.GetParameters(); }
        catch { return new ParameterInfo[0]; }
    }

    private static string Describe(MethodInfo m)
    {
        string[] p = SafeParameters(m)
            .Select(x => x.ParameterType.Name + " " + x.Name)
            .ToArray();

        return (m.DeclaringType != null ? m.DeclaringType.FullName : "?") +
               "." + m.Name + "(" + string.Join(", ", p) + ")";
    }

    private static void DumpColorMessageMembers()
    {
        Log.LogInfo("ThingColorMessage base type: " +
            (colorMessageType.BaseType != null ? colorMessageType.BaseType.FullName : "none"));

        foreach (FieldInfo f in SafeFields(colorMessageType))
            Log.LogInfo("ThingColorMessage field: " + f.FieldType.Name + " " + f.Name);

        foreach (PropertyInfo p in SafeProperties(colorMessageType))
            Log.LogInfo("ThingColorMessage property: " + p.PropertyType.Name + " " + p.Name);
    }

    private static FieldInfo[] SafeFields(Type t)
    {
        try
        {
            return t.GetFields(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch { return new FieldInfo[0]; }
    }

    private static PropertyInfo[] SafeProperties(Type t)
    {
        try
        {
            return t.GetProperties(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch { return new PropertyInfo[0]; }
    }

    private static void DumpObject(object obj, string label)
    {
        if (obj == null) return;

        Type t = obj.GetType();
        Log.LogInfo(label + " runtime type: " + t.FullName);

        foreach (FieldInfo f in SafeFields(t))
        {
            try { Log.LogInfo("  field " + f.Name + "=" + SafeValue(f.GetValue(obj))); }
            catch { }
        }

        foreach (PropertyInfo p in SafeProperties(t))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            try { Log.LogInfo("  property " + p.Name + "=" + SafeValue(p.GetValue(obj, null))); }
            catch { }
        }
    }

    private static string SafeValue(object v)
    {
        if (v == null) return "null";
        try { return v.ToString(); }
        catch { return "<unprintable>"; }
    }
}
