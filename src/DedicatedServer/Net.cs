// The messages the client and server halves of this mod swap, over FishNet broadcasts (no
// NetworkObject needed).
//
//   client -> server  DsHello      "I have the DedicatedServer mod, and these mods (and this game version)"
//   server -> client  DsChallenge  "this is server <id>; prove who you are (and the password, if any)"
//   client -> server  DsProof      the proof: HMAC(this game's identity key, server id) and HMAC(password, nonce)
//   server -> client  DsVerdict    "you're in" / "you're turned away, because ..."
//   server -> client  DsResearch   with individual research: this player's own research list
//   client -> server  DsInputs     every tick, UNRELIABLE: this player's last few movement inputs (InputRelay)
//   server -> client  DsEvent      everything else the server tells a player: Kind says what (respawn, announce,
//                                  ping, markers), Text and Data carry it
//
// FishNet's code generator never sees a mod assembly, so the serializers are registered by hand.
// Broadcast ids are a hash of the type's full name, and these live in the DedicatedServer
// namespace, so no other mod can collide. A server without this mod skips messages it doesn't know.
using System;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Serializing;
using FishNet.Transporting;

namespace DedicatedServer
{
    public struct DsHello : IBroadcast
    {
        public string ModVersion;
        public string Mods;          // "name\tversion" per line, plus "@game\t<version>"
    }

    public struct DsChallenge : IBroadcast
    {
        public string ServerId;
        public string Nonce;
        public bool NeedPassword;
    }

    public struct DsProof : IBroadcast
    {
        public string Token;         // hex HMAC-SHA256(identity key, server id): the same every time for this game + server
        public string Password;      // hex HMAC-SHA256(password, nonce): never the password itself
    }

    public struct DsVerdict : IBroadcast
    {
        public bool Accepted;
        public string Message;       // the reason when turned away; the welcome when accepted
        public string Missing;       // "name\tversion" per line the client lacks or has the wrong version of
    }

    public struct DsResearch : IBroadcast
    {
        public bool Individual;
        public string Ids;           // comma separated research ids this player has
    }

    public struct DsInputs : IBroadcast
    {
        public Players.LocalClient.Controls.Model.PlayerControlInput[] Inputs;   // oldest first
    }

    public struct DsEvent : IBroadcast
    {
        public string Kind;
        public string Text;
        public string Data;
    }

    internal static class Net
    {
        public static Action<NetworkConnection, DsInputs> OnInputs;
        public static Action<NetworkConnection, DsHello> OnHello;
        public static Action<NetworkConnection, DsProof> OnProof;
        public static Action<DsChallenge> OnChallenge;
        public static Action<DsVerdict> OnVerdict;
        public static Action<DsResearch> OnResearch;
        public static Action<DsServerInfo> OnServerInfo;
        public static Action<DsEvent> OnEvent;

        private static bool _registered, _hookedServer, _hookedClient;
        private static NetworkManager _manager;

        public static NetworkManager Manager { get { return InstanceFinder.NetworkManager; } }

        public static bool ServerStarted
        {
            get { NetworkManager m = Manager; return m != null && m.ServerManager != null && m.ServerManager.Started; }
        }

        public static bool ClientStarted
        {
            get { NetworkManager m = Manager; return m != null && m.ClientManager != null && m.ClientManager.Started; }
        }

        private static void Register()
        {
            if (_registered) return;
            _registered = true;
            GenericWriter<DsHello>.SetWrite((w, v) => { w.WriteString(v.ModVersion ?? ""); w.WriteString(v.Mods ?? ""); });
            GenericReader<DsHello>.SetRead(r => new DsHello { ModVersion = r.ReadStringAllocated(), Mods = r.ReadStringAllocated() });
            GenericWriter<DsChallenge>.SetWrite((w, v) => { w.WriteString(v.ServerId ?? ""); w.WriteString(v.Nonce ?? ""); w.WriteBoolean(v.NeedPassword); });
            GenericReader<DsChallenge>.SetRead(r => new DsChallenge { ServerId = r.ReadStringAllocated(), Nonce = r.ReadStringAllocated(), NeedPassword = r.ReadBoolean() });
            GenericWriter<DsProof>.SetWrite((w, v) => { w.WriteString(v.Token ?? ""); w.WriteString(v.Password ?? ""); });
            GenericReader<DsProof>.SetRead(r => new DsProof { Token = r.ReadStringAllocated(), Password = r.ReadStringAllocated() });
            GenericWriter<DsVerdict>.SetWrite((w, v) => { w.WriteBoolean(v.Accepted); w.WriteString(v.Message ?? ""); w.WriteString(v.Missing ?? ""); });
            GenericReader<DsVerdict>.SetRead(r => new DsVerdict { Accepted = r.ReadBoolean(), Message = r.ReadStringAllocated(), Missing = r.ReadStringAllocated() });
            // PlayerControlInput goes with the game's own (generated) serializer
            GenericWriter<DsInputs>.SetWrite((w, v) =>
            {
                int n = v.Inputs != null ? v.Inputs.Length : 0;
                w.WriteInt32(n);
                for (int i = 0; i < n; i++) w.Write(v.Inputs[i]);
            });
            GenericReader<DsInputs>.SetRead(r =>
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 64) n = 0;
                Players.LocalClient.Controls.Model.PlayerControlInput[] a = new Players.LocalClient.Controls.Model.PlayerControlInput[n];
                for (int i = 0; i < n; i++) a[i] = r.Read<Players.LocalClient.Controls.Model.PlayerControlInput>();
                return new DsInputs { Inputs = a };
            });
            GenericWriter<DsServerInfo>.SetWrite((w, v) =>
            {
                w.WriteString(v.Name ?? ""); w.WriteString(v.Desc ?? ""); w.WriteString(v.NameStyle ?? ""); w.WriteString(v.IconHash ?? "");
                w.WriteInt32(v.Players); w.WriteInt32(v.Max); w.WriteBytesAndSize(v.Icon ?? new byte[0]);
            });
            GenericReader<DsServerInfo>.SetRead(r => new DsServerInfo
            {
                Name = r.ReadStringAllocated(), Desc = r.ReadStringAllocated(), NameStyle = r.ReadStringAllocated(), IconHash = r.ReadStringAllocated(),
                Players = r.ReadInt32(), Max = r.ReadInt32(), Icon = r.ReadBytesAndSizeAllocated()
            });
            GenericWriter<DsEvent>.SetWrite((w, v) => { w.WriteString(v.Kind ?? ""); w.WriteString(v.Text ?? ""); w.WriteString(v.Data ?? ""); });
            GenericReader<DsEvent>.SetRead(r => new DsEvent { Kind = r.ReadStringAllocated(), Text = r.ReadStringAllocated(), Data = r.ReadStringAllocated() });
            GenericWriter<DsResearch>.SetWrite((w, v) => { w.WriteBoolean(v.Individual); w.WriteString(v.Ids ?? ""); });
            GenericReader<DsResearch>.SetRead(r => new DsResearch { Individual = r.ReadBoolean(), Ids = r.ReadStringAllocated() });
        }

        /// <summary>Every frame: (re)hooks when a server or client starts or the NetworkManager is replaced.</summary>
        public static void Update()
        {
            NetworkManager m = Manager;
            bool server = ServerStarted, client = ClientStarted;
            if (!ReferenceEquals(m, _manager)) { Unhook(); _manager = m; }
            if (m == null) return;
            try
            {
                if (server && !_hookedServer)
                {
                    Register();
                    // false: FishNet KICKS a not-yet-authenticated sender of an auth-only broadcast
                    m.ServerManager.RegisterBroadcast<DsHello>(ServerHello, false);
                    m.ServerManager.RegisterBroadcast<DsProof>(ServerProof, false);
                    m.ServerManager.RegisterBroadcast<DsInputs>(ServerInputs, false);
                    _hookedServer = true;
                }
                else if (!server && _hookedServer) { UnhookServer(m); }
                if (client && !_hookedClient)
                {
                    Register();
                    m.ClientManager.RegisterBroadcast<DsVerdict>(ClientVerdict);
                    m.ClientManager.RegisterBroadcast<DsChallenge>(ClientChallenge);
                    m.ClientManager.RegisterBroadcast<DsResearch>(ClientResearch);
                    m.ClientManager.RegisterBroadcast<DsServerInfo>(ClientServerInfo);
                    m.ClientManager.RegisterBroadcast<DsEvent>(ClientEvent);
                    _hookedClient = true;
                }
                else if (!client && _hookedClient) { UnhookClient(m); }
            }
            catch (Exception e) { Log.Once("net-hook", "could not hook the handshake messages: " + e.Message); }
        }

        private static void UnhookServer(NetworkManager m)
        {
            try
            {
                if (m != null && m.ServerManager != null)
                {
                    m.ServerManager.UnregisterBroadcast<DsHello>(ServerHello);
                    m.ServerManager.UnregisterBroadcast<DsProof>(ServerProof);
                    m.ServerManager.UnregisterBroadcast<DsInputs>(ServerInputs);
                }
            }
            catch { }
            _hookedServer = false;
        }

        private static void UnhookClient(NetworkManager m)
        {
            try
            {
                if (m != null && m.ClientManager != null)
                {
                    m.ClientManager.UnregisterBroadcast<DsVerdict>(ClientVerdict);
                    m.ClientManager.UnregisterBroadcast<DsChallenge>(ClientChallenge);
                    m.ClientManager.UnregisterBroadcast<DsResearch>(ClientResearch);
                    m.ClientManager.UnregisterBroadcast<DsServerInfo>(ClientServerInfo);
                    m.ClientManager.UnregisterBroadcast<DsEvent>(ClientEvent);
                }
            }
            catch { }
            _hookedClient = false;
        }

        private static void Unhook()
        {
            if (_hookedServer) UnhookServer(_manager);
            if (_hookedClient) UnhookClient(_manager);
        }

        private static void ServerHello(NetworkConnection conn, DsHello msg, Channel ch)
        {
            try { OnHello?.Invoke(conn, msg); }
            catch (Exception e) { Log.Once("hello", "hello handler: " + e); }
        }

        private static void ServerProof(NetworkConnection conn, DsProof msg, Channel ch)
        {
            try { OnProof?.Invoke(conn, msg); }
            catch (Exception e) { Log.Once("proof", "proof handler: " + e); }
        }

        private static void ServerInputs(NetworkConnection conn, DsInputs msg, Channel ch)
        {
            try { OnInputs?.Invoke(conn, msg); }
            catch (Exception e) { Log.Once("inputs", "inputs handler: " + e); }
        }

        private static void ClientVerdict(DsVerdict msg, Channel ch)
        {
            try { OnVerdict?.Invoke(msg); }
            catch (Exception e) { Log.Once("verdict", "verdict handler: " + e); }
        }

        private static void ClientChallenge(DsChallenge msg, Channel ch)
        {
            try { OnChallenge?.Invoke(msg); }
            catch (Exception e) { Log.Once("challenge", "challenge handler: " + e); }
        }

        private static void ClientServerInfo(DsServerInfo msg, Channel ch)
        {
            try { OnServerInfo?.Invoke(msg); }
            catch (Exception e) { Log.Once("serverinfo", "server info handler: " + e); }
        }

        private static void ClientEvent(DsEvent msg, Channel ch)
        {
            try { OnEvent?.Invoke(msg); }
            catch (Exception e) { Log.Once("event-" + msg.Kind, "server message '" + msg.Kind + "': " + e); }
        }

        private static void ClientResearch(DsResearch msg, Channel ch)
        {
            try { OnResearch?.Invoke(msg); }
            catch (Exception e) { Log.Once("research", "research handler: " + e); }
        }

        // ---- sending ----------------------------------------------------------------------------

        public static bool ToServer<T>(T msg) where T : struct, IBroadcast
        {
            NetworkManager m = Manager;
            if (m == null || m.ClientManager == null || !m.ClientManager.Started) return false;
            Register();
            m.ClientManager.Broadcast(msg);
            return true;
        }

        public static bool ToServerUnreliable<T>(T msg) where T : struct, IBroadcast
        {
            NetworkManager m = Manager;
            if (m == null || m.ClientManager == null || !m.ClientManager.Started) return false;
            Register();
            m.ClientManager.Broadcast(msg, Channel.Unreliable);
            return true;
        }

        /// <summary>requireAuthenticated false: a client being checked (or turned away) must still hear us.</summary>
        public static void ToClient<T>(NetworkConnection conn, T msg) where T : struct, IBroadcast
        {
            NetworkManager m = Manager;
            if (m == null || m.ServerManager == null || !m.ServerManager.Started || conn == null) return;
            Register();
            m.ServerManager.Broadcast(conn, msg, false);
        }

        /// <summary>To every authenticated player.</summary>
        public static void ToAll<T>(T msg) where T : struct, IBroadcast
        {
            NetworkManager m = Manager;
            if (m == null || m.ServerManager == null || !m.ServerManager.Started) return;
            Register();
            m.ServerManager.Broadcast(msg, true);
        }

        public static void Event(NetworkConnection conn, string kind, string text, string data) { ToClient(conn, new DsEvent { Kind = kind, Text = text, Data = data }); }
        public static void EventAll(string kind, string text, string data) { ToAll(new DsEvent { Kind = kind, Text = text, Data = data }); }

        public static bool SendHello(DsHello msg) { return ToServer(msg); }
        public static void SendVerdict(NetworkConnection conn, DsVerdict msg) { ToClient(conn, msg); }
    }
}
