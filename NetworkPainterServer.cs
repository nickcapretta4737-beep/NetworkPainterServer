using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

[BepInPlugin("dominic.networkpainterserver", "Network Painter Server", "5.0.0")]
public class NetworkPainterServer : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static bool Propagating;
    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("Network Painter Server 5.0.0 starting");
        _harmony = new Harmony("dominic.networkpainterserver.v5");

        Assembly game = typeof(Assets.Scripts.Objects.Thing).Assembly;
        int patched = 0;

        foreach (Type t in SafeTypes(game))
        {
            foreach (MethodInfo m in SafeMethods(t))
            {
                bool wanted =
                    m.Name == "set_CustomColorIndex" ||
                    m.Name == "OnColourChange" ||
                    m.Name == "OnColourChangeEvent" ||
                    HasThingColorMessage(m);

                if (!wanted) continue;

                try
                {
                    HarmonyMethod postfix = new HarmonyMethod(
                        typeof(NetworkPainterServer),
                        m.IsStatic ? nameof(StaticPostfix) : nameof(InstancePostfix));

                    _harmony.Patch(m, postfix: postfix);
                    patched++;
                    Log.LogInfo("Patched event hook: " + Describe(m));
                }
                catch (Exception e)
                {
                    Log.LogWarning("Could not patch " + Describe(m) + ": " + e.Message);
                }
            }
        }

        Log.LogInfo("Network Painter Server ready. Event hooks patched: " + patched);
    }

    private void OnDestroy()
    {
        try
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
        catch { }
    }

    public static void InstancePostfix(object __instance, object[] __args)
    {
        if (Propagating) return;
        try
        {
            object source = FindStructure(__instance, __args);
            if (source == null) return;
            HandleColorChange(source);
        }
        catch (Exception e)
        {
            if (Log != null) Log.LogError("Paint event error: " + e);
        }
    }

    public static void StaticPostfix(object[] __args)
    {
        if (Propagating) return;
        try
        {
            object source = FindStructure(null, __args);
            if (source == null)
            {
                DumpMessageArgs(__args);
                return;
            }
            HandleColorChange(source);
        }
        catch (Exception e)
        {
            if (Log != null) Log.LogError("Static paint event error: " + e);
        }
    }

    private static void HandleColorChange(object source)
    {
        MethodInfo colorGetter = FindMethod(source.GetType(), "get_CustomColorIndex", 0);
        if (colorGetter == null)
        {
            Log.LogWarning("Paint event hit " + source.GetType().FullName + " but get_CustomColorIndex was not found.");
            return;
        }

        object color;
        try
        {
            color = colorGetter.Invoke(source, null);
        }
        catch (Exception e)
        {
            Log.LogWarning("Could not read source color: " + e.Message);
            return;
        }

        object network = GetNetwork(source);
        if (network == null)
        {
            Log.LogInfo("Color changed on " + source.GetType().Name + ", but no structure network was found.");
            return;
        }

        IEnumerable members = GetStructureList(network);
        if (members == null)
        {
            Log.LogWarning("Found network " + network.GetType().FullName + " but could not read StructureList.");
            DumpUsefulMembers(network.GetType(), "network");
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
                if (ReferenceEquals(member, source)) continue;

                object current = ReadColor(member);
                if (ColorsEqual(current, color)) continue;

                if (ApplyColor(member, color)) changed++;
                else failed++;
            }
        }
        finally
        {
            Propagating = false;
        }

        Log.LogInfo(
            "NETWORK PAINT: source=" + source.GetType().Name +
            " network=" + network.GetType().Name +
            " color=" + SafeValue(color) +
            " members=" + found +
            " changed=" + changed +
            " failed=" + failed);
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
                object network = m.Invoke(source, null);
                if (network != null)
                {
                    Log.LogInfo("Found network using " + name + ": " + network.GetType().FullName);
                    return network;
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
                IEnumerable enumerable = value as IEnumerable;
                if (enumerable != null) return enumerable;
            }
            catch { }
        }

        Type t = network.GetType();
        while (t != null)
        {
            try
            {
                FieldInfo f = t.GetField(
                    "StructureList",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    IEnumerable enumerable = f.GetValue(network) as IEnumerable;
                    if (enumerable != null) return enumerable;
                }
            }
            catch { }
            t = t.BaseType;
        }

        return null;
    }

    private static object ReadColor(object obj)
    {
        MethodInfo getter = FindMethod(obj.GetType(), "get_CustomColorIndex", 0);
        if (getter == null) return null;
        try { return getter.Invoke(obj, null); }
        catch { return null; }
    }

    private static bool ApplyColor(object target, object sourceColor)
    {
        Type t = target.GetType();

        foreach (MethodInfo m in FindMethods(t, "SetCustomColor"))
        {
            ParameterInfo[] p = m.GetParameters();
            if (p.Length < 1 || p.Length > 3) continue;

            try
            {
                object[] args = new object[p.Length];
                args[0] = ConvertValue(sourceColor, p[0].ParameterType);

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

        MethodInfo setter = FindMethod(t, "set_CustomColorIndex", 1);
        if (setter != null)
        {
            try
            {
                Type pt = setter.GetParameters()[0].ParameterType;
                setter.Invoke(target, new object[] { ConvertValue(sourceColor, pt) });
                return true;
            }
            catch { }
        }

        return false;
    }

    private static object ConvertValue(object value, Type targetType)
    {
        if (value == null)
        {
            if (targetType.IsValueType) return Activator.CreateInstance(targetType);
            return null;
        }

        Type sourceType = value.GetType();
        if (targetType.IsAssignableFrom(sourceType)) return value;

        if (targetType.IsEnum)
        {
            int n = Convert.ToInt32(value);
            return Enum.ToObject(targetType, n);
        }

        if (sourceType.IsEnum)
        {
            int n = Convert.ToInt32(value);
            return Convert.ChangeType(n, targetType);
        }

        return Convert.ChangeType(value, targetType);
    }

    private static bool ColorsEqual(object a, object b)
    {
        if (a == null || b == null) return false;
        try { return Convert.ToInt32(a) == Convert.ToInt32(b); }
        catch { return a.Equals(b); }
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

                s = FindStructureInsideMessage(a);
                if (s != null) return s;
            }
        }

        return null;
    }

    private static object AsStructure(object obj)
    {
        if (obj == null) return null;
        Type t = obj.GetType();
        while (t != null)
        {
            if (t.FullName == "Assets.Scripts.Objects.Structure") return obj;
            t = t.BaseType;
        }
        return null;
    }

    private static object FindStructureInsideMessage(object msg)
    {
        if (msg == null) return null;
        Type t = msg.GetType();
        if (t.Name.IndexOf("Color", StringComparison.OrdinalIgnoreCase) < 0 &&
            t.Name.IndexOf("Colour", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        foreach (FieldInfo f in SafeFields(t))
        {
            try
            {
                object value = f.GetValue(msg);
                object s = AsStructure(value);
                if (s != null) return s;
            }
            catch { }
        }

        foreach (PropertyInfo p in SafeProperties(t))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            try
            {
                object value = p.GetValue(msg, null);
                object s = AsStructure(value);
                if (s != null) return s;
            }
            catch { }
        }

        return null;
    }

    private static bool HasThingColorMessage(MethodInfo m)
    {
        try
        {
            foreach (ParameterInfo p in m.GetParameters())
            {
                string n = p.ParameterType.FullName ?? p.ParameterType.Name;
                if (n.IndexOf("ThingColorMessage", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static MethodInfo FindMethod(Type type, string name, int parameterCount)
    {
        Type t = type;
        while (t != null)
        {
            try
            {
                foreach (MethodInfo m in t.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name && m.GetParameters().Length == parameterCount)
                        return m;
                }
            }
            catch { }
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
                if (m.Name == name) yield return m;

            t = t.BaseType;
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null); }
        catch { return new Type[0]; }
    }

    private static IEnumerable<MethodInfo> SafeMethods(Type t)
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

    private static IEnumerable<FieldInfo> SafeFields(Type t)
    {
        try
        {
            return t.GetFields(
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic);
        }
        catch { return new FieldInfo[0]; }
    }

    private static IEnumerable<PropertyInfo> SafeProperties(Type t)
    {
        try
        {
            return t.GetProperties(
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic);
        }
        catch { return new PropertyInfo[0]; }
    }

    private static void DumpMessageArgs(object[] args)
    {
        if (Log == null || args == null) return;

        foreach (object a in args)
        {
            if (a == null) continue;
            Type t = a.GetType();
            string n = t.FullName ?? t.Name;

            if (n.IndexOf("Color", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("Colour", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            Log.LogInfo("Color message seen: " + n);

            foreach (FieldInfo f in SafeFields(t))
            {
                try
                {
                    Log.LogInfo("  field " + f.Name + "=" + SafeValue(f.GetValue(a)));
                }
                catch { }
            }
        }
    }

    private static void DumpUsefulMembers(Type t, string label)
    {
        if (Log == null) return;
        Log.LogInfo("Useful members on " + label + " type " + t.FullName + ":");

        Type cur = t;
        while (cur != null)
        {
            foreach (MethodInfo m in SafeMethods(cur))
            {
                string n = m.Name;
                if (n.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Colour", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log.LogInfo("  " + Describe(m));
            }
            cur = cur.BaseType;
        }
    }

    private static string Describe(MethodInfo m)
    {
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

    private static string SafeValue(object value)
    {
        if (value == null) return "null";
        try { return value.ToString(); }
        catch { return "<unprintable>"; }
    }
}
