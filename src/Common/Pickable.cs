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
    using System.Linq;
    using System.Reflection;
    using HarmonyLib;
    using Managers.Interface.Model;
    using Managers.Server.Services;
    using Players;
    using Players.Interface.Model;
    using Players.Services;
    using Ships;
    using Ships.Cables;
    using Ships.Interface.Model.Parts;
    using Ships.Interface.Model.Parts.Common;
    using Ships.Interface.Settings;
    using Ships.Interface.Model.Placement;
    using Ships.Services;

    /// <summary>
    /// Left-click pick-up for a mod's own parts. The game only lets you pick up
    /// parts whose state is one of its few "pickable" kinds; ours keep other
    /// states (a chess table keeps its game in one). So the host's pick-up and
    /// put-down requests are taken over for our parts only, doing what the game
    /// does for its own - the part follows the player's crosshair as usual, is
    /// put down where they click, and its cables are plugged back in - except
    /// that the part keeps its state (a game in progress travels with the board).
    /// </summary>
    public static class Pickable
    {
        private static Func<PartSettings, bool> _mine;
        private static readonly Dictionary<PartContext, uint> _held = new Dictionary<PartContext, uint>();
        public static int PickedUp, PutDown;

        public static void Patch(HarmonyLib.Harmony h, Func<PartSettings, bool> mine)
        {
            _mine = mine;
            MethodInfo pick = typeof(ShipInteractionService).GetMethods(Refl.Any).FirstOrDefault(m => m.Name.StartsWith("RpcLogic___PickUpPart_ServerRPC"));
            MethodInfo place = typeof(ShipInteractionService).GetMethods(Refl.Any).FirstOrDefault(m => m.Name.StartsWith("RpcLogic___PlaceHeldPart_ServerRPC"));
            if (pick == null || place == null) throw new Exception("no pick-up / put-down RPC in this build");
            h.Patch(pick, prefix: new HarmonyMethod(typeof(Pickable), nameof(PickPrefix)));
            h.Patch(place, prefix: new HarmonyMethod(typeof(Pickable), nameof(PlacePrefix)));
        }

        private static TrackedPlayerServer PlayerOf(FishNet.Connection.NetworkConnection conn)
        {
            return conn == null ? null : Svc.Get<PlayersServerTracker>()?.GetTrackedPlayerFromConnectionId(conn.ClientId);
        }

        private static bool PickPrefix(PartInteractNetworkRequest __0, FishNet.Connection.NetworkConnection __1)
        {
            try
            {
                TrackedShipServer ship = Svc.Get<ShipsServerTracker>()?.GetShip(__0.ShipId);
                StatefulPart part;
                if (ship == null || !ship.TryGetStatefulPart(__0.PartId, out part) || part.Settings == null || !_mine(part.Settings)) return true;
                TrackedPlayerServer p = PlayerOf(__1);
                if (p == null) return false;
                PlayerStateType st = p.StateVal;
                if (st != PlayerStateType.Walking && st != PlayerStateType.CheatFlying) return false;
                PartContext pc = new PartContext { ShipId = ship.Id, PartId = part.Id };
                uint holder;
                if (_held.TryGetValue(pc, out holder) && holder != p.Id && StillHolding(holder, pc)) return false;   // someone has it
                _held[pc] = p.Id;
                p.SetState(new PlayerState { Type = st == PlayerStateType.Walking ? PlayerStateType.WalkingHoldingShipPart : PlayerStateType.CheatFlyingHoldingShipPart, EntityId = ship.Id, PartId = part.Id });
                PickedUp++;
                return false;
            }
            catch (Exception e) { Log.Once("pick-" + typeof(Pickable).Namespace, "pick up: " + e.Message); return true; }
        }

        private static bool StillHolding(uint playerId, PartContext pc)
        {
            TrackedPlayerServer p = Svc.Get<PlayersServerTracker>()?.ActivePlayers.FirstOrDefault(x => x.Id == playerId);
            if (p == null) return false;
            PlayerState s = p.State;
            return (s.Type == PlayerStateType.WalkingHoldingShipPart || s.Type == PlayerStateType.CheatFlyingHoldingShipPart) && s.EntityId == pc.ShipId && s.PartId == pc.PartId;
        }

        private static bool PlacePrefix(PlaceHeldPartNetworkRequest __0, FishNet.Connection.NetworkConnection __1)
        {
            try
            {
                TrackedPlayerServer p = PlayerOf(__1);
                if (p == null) return true;
                PlayerStateType st = p.StateVal;
                if (st != PlayerStateType.WalkingHoldingShipPart && st != PlayerStateType.CheatFlyingHoldingShipPart) return true;
                ShipsServerTracker ships = Svc.Get<ShipsServerTracker>();
                TrackedShipServer from = ships.GetShip(p.State.EntityId);
                StatefulPart part;
                if (from == null || !from.TryGetStatefulPart(p.State.PartId, out part) || part.Settings == null || !_mine(part.Settings)) return true;
                TrackedShipServer to = ships.GetShip(__0.ShipId);
                if (to == null) return false;
                Move(p, from, part, to, __0.Placement);
                return false;
            }
            catch (Exception e) { Log.Once("place-" + typeof(Pickable).Namespace, "put down: " + e.Message); return true; }
        }

        /// <summary>The game's own put-down, step for step, keeping the part's state.</summary>
        public static ushort Move(TrackedPlayerServer p, TrackedShipServer from, StatefulPart part, TrackedShipServer to, IPartPlacement placement)
        {
            CablesServerTracker cables = Svc.Get<CablesServerTracker>();
            PartContext pc = new PartContext { ShipId = from.Id, PartId = part.Id };
            List<CableData> attached = cables.GetCablesConnectedToPart(pc).ToList();
            Ships.Interface.Model.Parts.StateTypes.IStatefulPartState keep = part.State;
            PartSettings settings = part.Settings;
            from.RemoveStatefulPart(part.Id);
            ushort id = to.AddStatefulPart(settings, placement);
            to.UpdatePartState(id, keep);
            foreach (CableData c in attached)
            {
                CablePlugSocket a = c.State.PlugA, b = c.State.PlugB;
                if (a.EntityId == from.Id && a.PartSocket.PartId == part.Id) a = new CablePlugSocket { EntityId = to.Id, PartSocket = new PartSocket(id, a.PartSocket.SocketId) };
                else b = new CablePlugSocket { EntityId = to.Id, PartSocket = new PartSocket(id, b.PartSocket.SocketId) };
                cables.AddCable(new CableState { Type = c.State.Type, PlugA = a, PlugB = b });
            }
            _held.Remove(pc);
            if (p != null)
                p.SetState(new PlayerState { Type = p.StateVal == PlayerStateType.WalkingHoldingShipPart ? PlayerStateType.Walking : PlayerStateType.CheatFlying });
            PutDown++;
            return id;
        }
    }
}
