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
    using System.Reflection;
    using FishNet.Broadcast;
    using FishNet.Connection;
    using FishNet.Managing;
    using FishNet.Serializing;
    using FishNet.Transporting;

    /// <summary>
    /// A two-way FishNet broadcast channel for one message type, needing no
    /// NetworkObject. FishNet's code generator never sees a mod assembly, so the
    /// serializer is registered by hand. Broadcast ids are a hash of the message
    /// type's full name, so every mod's message types are distinct as long as
    /// they live in the mod's own namespace.
    ///
    /// Call Update() every frame: it (re)hooks when a server or client starts
    /// or the NetworkManager is replaced, and unhooks when they stop.
    /// </summary>
    public class ModChannel<T> where T : struct, IBroadcast
    {
        public Action<NetworkConnection, T> OnServer;   // a client (or the host) sent us T
        public Action<T> OnClient;                      // the server sent us T
        public bool Hooked { get { return _hooked; } }

        private bool _hooked, _asServer, _asClient;
        private NetworkManager _manager;
        private static bool _registered;

        public ModChannel(Action<Writer, T> write, Func<Reader, T> read)
        {
            if (!_registered)
            {
                _registered = true;
                GenericWriter<T>.SetWrite(write);
                GenericReader<T>.SetRead(read);
            }
        }

        public void Update()
        {
            NetworkManager m = Net.Manager;
            bool server = m != null && m.ServerManager != null && m.ServerManager.Started;
            bool client = m != null && m.ClientManager != null && m.ClientManager.Started;
            if (!server && !client) { Unhook(); return; }
            if (_hooked && ReferenceEquals(m, _manager) && _asServer == server && _asClient == client) return;
            Unhook();
            try
            {
                if (server) m.ServerManager.RegisterBroadcast<T>(ServerHandler, true);
                if (client) m.ClientManager.RegisterBroadcast<T>(ClientHandler);
                _hooked = true; _asServer = server; _asClient = client; _manager = m;
                Log.Msg("network channel " + typeof(T).Name + " ready (" + (server ? "host" : "client") + ")");
            }
            catch (Exception e) { Log.Once("chan-" + typeof(T).Name, "could not hook " + typeof(T).Name + ": " + e.Message); }
        }

        public void Unhook()
        {
            if (!_hooked) return;
            try
            {
                if (_manager != null)
                {
                    if (_asServer && _manager.ServerManager != null) _manager.ServerManager.UnregisterBroadcast<T>(ServerHandler);
                    if (_asClient && _manager.ClientManager != null) _manager.ClientManager.UnregisterBroadcast<T>(ClientHandler);
                }
            }
            catch { }
            _hooked = false; _manager = null;
        }

        private void ServerHandler(NetworkConnection conn, T msg, Channel ch)
        {
            try { OnServer?.Invoke(conn, msg); }
            catch (Exception e) { Log.Once("srv-" + typeof(T).Name, typeof(T).Name + " server handler: " + e); }
        }

        private void ClientHandler(T msg, Channel ch)
        {
            try { OnClient?.Invoke(msg); }
            catch (Exception e) { Log.Once("cli-" + typeof(T).Name, typeof(T).Name + " client handler: " + e); }
        }

        /// <summary>Client (or host-as-client) -> server.</summary>
        public bool ToServer(T msg)
        {
            NetworkManager m = Net.Manager;
            if (m == null || m.ClientManager == null || !m.ClientManager.Started) return false;
            m.ClientManager.Broadcast(msg);
            return true;
        }

        public void ToClient(NetworkConnection conn, T msg)
        {
            NetworkManager m = Net.Manager;
            if (m == null || m.ServerManager == null || !m.ServerManager.Started || conn == null) return;
            m.ServerManager.Broadcast(conn, msg);
        }

        public void ToAll(T msg)
        {
            NetworkManager m = Net.Manager;
            if (m == null || m.ServerManager == null || !m.ServerManager.Started) return;
            m.ServerManager.Broadcast(msg, false);
        }
    }

    /// <summary>
    /// The game's part states are C# records with init-only properties, which
    /// the Mono compiler cannot write. These read/clone/write them by
    /// reflection - always producing a NEW instance, because the game tells old
    /// from new state by reference when it raises change events.
    /// </summary>
    public static class Rec
    {
        public static T With<T>(T rec, params object[] nameValuePairs) where T : class
        {
            MethodInfo clone = rec.GetType().GetMethod("<Clone>$", Refl.Any);
            T copy = clone != null ? (T)clone.Invoke(rec, null) : rec;
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
                Set(copy, (string)nameValuePairs[i], nameValuePairs[i + 1]);
            return copy;
        }

        public static void Set(object rec, string prop, object value)
        {
            for (Type t = rec.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(prop, Refl.Any | BindingFlags.DeclaredOnly);
                if (p != null)
                {
                    MethodInfo setter = p.GetSetMethod(true);
                    if (setter != null) { setter.Invoke(rec, new[] { value }); return; }
                }
                FieldInfo f = t.GetField("<" + prop + ">k__BackingField", Refl.Any | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(rec, value); return; }
            }
            throw new MissingMemberException(rec.GetType().Name, prop);
        }

        public static object Get(object rec, string prop)
        {
            for (Type t = rec.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(prop, Refl.Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetGetMethod(true) != null) return p.GetGetMethod(true).Invoke(rec, null);
                FieldInfo f = t.GetField("<" + prop + ">k__BackingField", Refl.Any | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(rec);
            }
            throw new MissingMemberException(rec.GetType().Name, prop);
        }

        public static T New<T>(params object[] nameValuePairs) where T : class, new()
        {
            T r = new T();
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2) Set(r, (string)nameValuePairs[i], nameValuePairs[i + 1]);
            return r;
        }
    }
}
