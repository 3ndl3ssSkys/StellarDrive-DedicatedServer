// Shared helpers, compiled INTO every mod under that mod's own namespace.
// build.sh passes -define:MOD_<NAME>; the block below picks the namespace, so
// e.g. Graves gets Graves.Common.Svc and GameTweaks gets GameTweaks.Common.Svc
// - two separate types that can never collide at runtime.
#if MOD_GRAVES
namespace Graves.Common
#elif MOD_CHESS
namespace Chess.Common
#elif MOD_PERF
namespace PerformancePlus.Common
#elif MOD_TWEAKS
namespace GameTweaks.Common
#elif MOD_HARNESS
namespace DemoVerifyHarness.Common
#elif MOD_MOMENTUM
namespace Momentum.Common
#elif MOD_CAMERAS
namespace ExtraCameras.Common
#elif MOD_GRAVITY
namespace Gravity.Common
#elif MOD_RAILS
namespace SafetyRails.Common
#elif MOD_SOLAR
namespace SolarPanels.Common
#elif MOD_STATION
namespace StationModels.Common
#elif MOD_VR
namespace StellarDriveVR.Common
#else
namespace SDMod.Common
#endif
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using Core.Services;
    using FishNet;
    using FishNet.Managing;
    using MelonLoader;
    using UnityEngine;

    /// <summary>Logging that never throws, with repeat suppression.</summary>
    public static class Log
    {
        public static MelonLogger.Instance Logger;
        private static readonly Dictionary<string, int> _repeats = new Dictionary<string, int>();

        public static void Msg(string s) { try { Logger?.Msg(s); } catch { } }
        public static void Warn(string s) { try { Logger?.Warning(s); } catch { } }
        public static void Error(string s) { try { Logger?.Error(s); } catch { } }

        /// <summary>
        /// Logs a failure the first time and then only at powers of ten. An
        /// error raised every frame otherwise buries the first occurrence, which
        /// is the only one with any context around it.
        /// </summary>
        public static void Once(string key, string s)
        {
            int n;
            _repeats.TryGetValue(key, out n);
            n++;
            _repeats[key] = n;
            if (n == 1 || n == 10 || n == 100 || n == 1000 || n == 10000)
                Warn(n == 1 ? s : s + "  (x" + n + ")");
        }
    }

    /// <summary>
    /// Service lookup. The game's ServiceLocator scans every object tagged
    /// ServiceObject on EVERY call, so results are cached for the life of the
    /// scene and re-resolved only when the cached object has been destroyed.
    /// </summary>
    public static class Svc
    {
        private static readonly Dictionary<Type, object> _cache = new Dictionary<Type, object>();

        public static T Get<T>() where T : class
        {
            object o;
            if (_cache.TryGetValue(typeof(T), out o))
            {
                UnityEngine.Object uo = o as UnityEngine.Object;
                if (o != null && (!(o is UnityEngine.Object) || uo != null)) return (T)o;
                _cache.Remove(typeof(T));
            }
            T found = null;
            try { found = ServiceLocator.GetService<T>(); } catch { }
            if (found != null) _cache[typeof(T)] = found;
            return found;
        }

        public static void Clear() { _cache.Clear(); }
    }

    /// <summary>Who is in charge. Never infer this from a service being non-null
    /// (a guest has every server service too - they are just empty).</summary>
    public static class Net
    {
        public static NetworkManager Manager { get { try { return InstanceFinder.NetworkManager; } catch { return null; } } }

        public static bool IsServer
        {
            get
            {
                NetworkManager m = Manager;
                return m != null && m.ServerManager != null && m.ServerManager.Started;
            }
        }

        public static bool IsClient
        {
            get
            {
                NetworkManager m = Manager;
                return m != null && m.ClientManager != null && m.ClientManager.Started;
            }
        }

        public static bool IsGuest { get { return IsClient && !IsServer; } }
    }

    public static class Refl
    {
        public const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static object Get(object o, string field)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(field, Any | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
                PropertyInfo p = t.GetProperty(field, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            return null;
        }

        public static T Get<T>(object o, string field) where T : class { return Get(o, field) as T; }

        public static bool Set(object o, string field, object value)
        {
            if (o == null) return false;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(field, Any | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(o, value); return true; }
                PropertyInfo p = t.GetProperty(field, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite) { p.SetValue(o, value, null); return true; }
            }
            return false;
        }

        public static object Call(object o, string method, params object[] args)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(Any | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != method || m.GetParameters().Length != (args == null ? 0 : args.Length)) continue;
                    return m.Invoke(m.IsStatic ? null : o, args);
                }
            }
            return null;
        }

        public static MethodInfo Method(Type t, string name, params Type[] args)
        {
            if (t == null) return null;
            if (args == null || args.Length == 0)
            {
                foreach (MethodInfo m in t.GetMethods(Any))
                    if (m.Name == name) return m;
                return null;
            }
            return t.GetMethod(name, Any, null, args, null);
        }
    }

    /// <summary>Where a mod keeps per-world side files.</summary>
    public static class ModFiles
    {
        public static string UserDataDir(string mod)
        {
            string d = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, mod);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>
        /// The world's own folder. Every current save is called world.json, so
        /// the world's NAME is its directory - never the file name.
        /// </summary>
        public static string CurrentWorldDir()
        {
            try
            {
                string full = Managers.Interface.MainGameSceneLoadData.SaveFileFullName;
                if (string.IsNullOrEmpty(full)) return null;
                return Path.GetDirectoryName(full);
            }
            catch { return null; }
        }
    }
}
