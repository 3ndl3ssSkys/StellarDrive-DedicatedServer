// Movement for far-away players.
//
// The game sends each tick's movement input to the server as a RELIABLE, ORDERED message
// (PlayerNetworkInputs.SetPlayerInput_ServerRPC). On a connection that loses packets, one lost
// packet holds back every input after it until it's resent a round trip later: the server runs
// out of inputs, repeats the last one, then gets a burst - and the player is pulled back
// (measured through a 120 ms / 1% loss relay: up to 1.5 m corrections, a queue of up to 32 inputs).
//
// So on a dedicated server the client ALSO sends, every tick, its last few inputs as one
// UNRELIABLE message (DsInputs). A lost packet is covered by the next one, 20 ms later. The server
// hands each input to the game's own handler (RpcLogic___SetPlayerInput_ServerRPC), which keeps
// only inputs newer than the last one it used and files them by tick - so duplicates, and the
// reliable copy arriving late, change nothing.
using System;
using System.Collections.Generic;
using System.Reflection;
using FishNet.Connection;
using FishNet.Transporting;
using HarmonyLib;
using Players;
using Players.LocalClient.Controls.Model;
using Players.Network;

namespace DedicatedServer
{
    internal static class InputRelay
    {
        public const int Redundancy = 6;          // inputs per message: covers 5 lost packets in a row (100 ms)

        private static readonly PlayerControlInput[] _ring = new PlayerControlInput[Redundancy];
        private static int _count, _next;
        public static int Sent, Received, Filled;
        private static readonly Dictionary<int, PlayerNetworkInputs> _byConn = new Dictionary<int, PlayerNetworkInputs>();

#if DSTEST
        public static bool Enabled = Environment.GetEnvironmentVariable("SDD_DS_NO_INPUT_RELAY") != "1";   // test switch: the game's own way only
#else
        public const bool Enabled = true;
#endif

        public static void InitClient(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(PlayerNetworkInputs), "SetPlayerInput_ServerRPC"),
                    postfix: new HarmonyMethod(typeof(InputRelay).GetMethod("SentPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { Log.Warn("input relay unavailable: " + e.Message); }
        }

        public static void InitServer() { Net.OnInputs = OnInputs; }

        // ---- client -----------------------------------------------------------------------------

        public static void ClientReset() { _count = 0; _next = 0; }

        private static void SentPostfix(PlayerControlInput controlInput)
        {
            if (!Enabled || Net.ServerStarted || !Net.ClientStarted) return;
            if (!Client.LastVerdict.HasValue || !Client.LastVerdict.Value.Accepted) return;    // only on a dedicated server with this mod
            _ring[_next] = controlInput;
            _next = (_next + 1) % Redundancy;
            if (_count < Redundancy) _count++;
            PlayerControlInput[] batch = new PlayerControlInput[_count];
            for (int i = 0; i < _count; i++) batch[i] = _ring[(_next - _count + i + Redundancy) % Redundancy];    // oldest first
            if (Net.ToServerUnreliable(new DsInputs { Inputs = batch })) Sent++;
        }

        // ---- server -----------------------------------------------------------------------------

        private static void OnInputs(NetworkConnection conn, DsInputs msg)
        {
            if (msg.Inputs == null || msg.Inputs.Length == 0) return;
            PlayerNetworkInputs inputs;
            if (!_byConn.TryGetValue(conn.ClientId, out inputs) || inputs == null)
            {
                TrackedPlayerServer p = Server.PlayerByConnection(conn.ClientId);
                if (p == null) return;
                PlayerNetworkState ns = Traverse.Create(p).Property("NetworkState").GetValue<PlayerNetworkState>();
                inputs = ns != null ? ns.GetComponent<PlayerNetworkInputs>() : null;
                if (inputs == null) return;
                _byConn[conn.ClientId] = inputs;
            }
            Received++;
            int before = inputs.CurrentBufferedInputCount;
            foreach (PlayerControlInput i in msg.Inputs) inputs.RpcLogic___SetPlayerInput_ServerRPC___2798132000(i);
            if (inputs.CurrentBufferedInputCount > before) Filled += inputs.CurrentBufferedInputCount - before;
        }

        public static void Forget(int connectionId) { _byConn.Remove(connectionId); }
    }
}
