// Probes for players' profiles: back to the start, onto a ship, delete (on now and not), and the
// player file that carries a name and identity to a new game version.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DedicatedServer
{
    internal static partial class Probe
    {
        private static IEnumerator ProfilesProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string me = Me().Name;
            string id = MyState().SteamUserId.ToString();

            // on now: walk away, then back to the start
            yield return ConsoleText("tpforward " + me + " 0 -120", "moved", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("player info " + id, "):", w);
            double far = Num(w.Detail, @"([\d.]+) m from the world's starting point");
            yield return ConsoleText("player reset " + id, "moved", w);
            string said = w.Detail;
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("player info " + id, "):", w);
            double near = Num(w.Detail, @"([\d.]+) m from the world's starting point");
            check("player reset (on now): back at the world's starting point", far > 60 && near >= 0 && near < 8, far.ToString("0") + " m -> " + near.ToString("0") + " m; " + said.Trim());

            // on now: onto a ship
            yield return ConsoleText("tpforward " + me + " 0 -40", "moved", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("testship park 3", "test ship", w);
            string ship = Grab(w.Detail, @"#(\d+)");
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("player reset " + id, "moved", w);
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("player ship " + id + " " + ship, "ship #", w);
            said = w.Detail;
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("player info " + id, "):", w);
            double toShip = Num(w.Detail, @"nearest ship #" + ship + @" at ([\d.]+) m");
            check("player ship (on now): put on top of ship #" + ship, said.Contains("on ship #" + ship) && (w.Detail.Contains("aboard ship #" + ship + "") || (toShip >= 0 && toShip < 15)), said.Trim() + " / " + w.Detail.Trim());
            Shot("profile-on-ship");
            yield return new WaitForSecondsRealtime(0.5f);

            // not on: back to the start, and onto the ship, waiting there when they join
            yield return Leave();
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("player reset " + id, "moved", w);
            said = w.Detail;
            yield return ConsoleText("player info " + id, "):", w);
            near = Num(w.Detail, @"([\d.]+) m from the world's starting point");
            check("player reset (not on): their saved character is moved to the start", said.Contains("not on now") && w.Detail.Contains("not on;") && near >= 0 && near < 8, said.Trim() + " / " + w.Detail.Trim());
            yield return ConsoleText("player ship " + id + " " + ship, "ship #", w);
            said = w.Detail;
            yield return Rejoin(w);
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("player info " + id, "):", w);
            toShip = Num(w.Detail, @"nearest ship #" + ship + @" at ([\d.]+) m");
            check("player ship (not on): they join on top of the ship", said.Contains("not on now") && w.Detail.Contains("on now") && (w.Detail.Contains("aboard ship #" + ship + "") || (toShip >= 0 && toShip < 15)), said.Trim() + " / " + w.Detail.Trim());
            Shot("profile-joined-on-ship");
            yield return new WaitForSecondsRealtime(0.5f);
            yield return ConsoleText("testship stop", "stopped", w);

            // delete, not on: asks first; then character and record go; joining again starts fresh
            yield return Leave();
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("player delete " + id, "confirm", w);
            check("player delete without 'confirm' only says what it would do", w.Detail.Contains("To do it") && DataFile()["Players"].Any(p => (string)p["Id"] == id), w.Detail.Trim());
            yield return ConsoleText("player delete " + id + " confirm", "Deleted", w);
            said = w.Detail;
            yield return new WaitForSecondsRealtime(2f);
            yield return ConsoleText("player info " + id, "):", w);
            check("player delete (not on): their character and record are gone, a backup save first", said.Contains("their character") && w.Detail.Contains("no character")
                && !DataFile()["Players"].Any(p => (string)p["Id"] == id), said.Trim() + " / " + w.Detail.Trim());
            yield return Rejoin(w);
            yield return ConsoleText("player info " + id, "):", w);
            near = Num(w.Detail, @"([\d.]+) m from the world's starting point");
            JToken rec = DataFile()["Players"].FirstOrDefault(p => (string)p["Id"] == id);
            check("...joining again starts fresh: at the start, a new record (1 visit)", Me() != null && near >= 0 && near < 30 && rec != null && (int)rec["Visits"] == 1, w.Detail.Trim() + "; visits " + (rec != null ? (string)rec["Visits"] : "-"));

            // delete, on now: disconnected with a message, then deleted
            int seen = Client.VerdictsSeen;
            yield return ConsoleText("player delete " + id + " confirm", "disconnecting", w);
            said = w.Detail;
            yield return WaitUntil(() => Me() == null && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainGame", 20f);
            yield return new WaitForSecondsRealtime(3f);
            yield return ConsoleText("player info " + id, "):", w);
            check("player delete (on now): they're disconnected, then deleted", said.Contains("disconnecting") && w.Detail.Contains("no character") && !DataFile()["Players"].Any(p => (string)p["Id"] == id),
                said.Trim() + " / " + w.Detail.Trim());
            yield return Rejoin(w);
            check("...and can join again", w.Ok, w.Detail);
        }

        // ---- the player file: a name and identity that carry over to a new game version ----------------

        private static IEnumerator PlayerFileProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            string file = Identity.FilePath;
            string name0 = Client.PlayerName.Value, id0 = Client.OfflineId.Value, key0 = Client.IdentityKey.Value;
            string shared0 = File.Exists(file) ? File.ReadAllText(file) : null;
            check("the player file is the test game's own (not the real %APPDATA% one)", file.StartsWith(MelonLoader.Utils.MelonEnvironment.UserDataDirectory), file);
            // what joining has made by now is in the shared file
            Identity.Shared s = File.Exists(file) ? JsonConvert.DeserializeObject<Identity.Shared>(File.ReadAllText(file)) : null;
            check("the shared file has this game's identity key", s != null && s.IdentityKey == key0 && key0.Length >= 32, s != null ? "key " + (s.IdentityKey == key0 ? "same" : "different") : "no file");

            // a new game version: an empty MelonPreferences
            yield return Leave();
            yield return new WaitForSecondsRealtime(2f);
            File.WriteAllText(file, JsonConvert.SerializeObject(new Identity.Shared { Name = "Shared Name", NoSteamId = id0, IdentityKey = key0 }));
            Client.PlayerName.Value = ""; Client.OfflineId.Value = ""; Client.IdentityKey.Value = "";
            Identity.Sync();
            check("a new install takes the name, no-Steam id and key from the shared file", Client.PlayerName.Value == "Shared Name" && Client.OfflineId.Value == id0 && Client.IdentityKey.Value == key0, Identity.LastSync);
            yield return Rejoin(w);
            check("...so the server knows it's the same player (not 'another computer's game')", w.Ok, w.Detail);
            check("...under the shared name", Me() != null && Me().Name == "Shared Name", Me() != null ? Me().Name : "-");

            // the Name box in any install changes it for all
            yield return Leave();
            yield return new WaitForSecondsRealtime(2f);
            yield return OpenJoinPage(note);
            yield return WaitUntil(() => ClientUi.PanelPresent(), 10f);
            ClientUi.TypeName("Box Name");
            yield return null;
            s = JsonConvert.DeserializeObject<Identity.Shared>(File.ReadAllText(file));
            check("a name typed in the Name box goes to the shared file", s.Name == "Box Name", s.Name);

            // an install that already has its own key keeps it
            string otherKey = new string('f', 64);
            Client.IdentityKey.Value = otherKey;
            Identity.Sync();
            check("an install with a key of its own keeps it (a character tied to it stays reachable)", Client.IdentityKey.Value == otherKey, Identity.LastSync);

            // put everything back
            Client.PlayerName.Value = name0; Client.OfflineId.Value = id0; Client.IdentityKey.Value = key0;
            if (shared0 != null) File.WriteAllText(file, shared0); else File.Delete(file);
            Identity.Sync();
            try { MelonLoader.MelonPreferences.Save(); } catch { }
            yield return Rejoin(w);
            check("...and back as before, joined", w.Ok && Me() != null, w.Detail);
        }
            // ---- the network guard: a port that stops listening is opened again; the way in is watched ------

        private static string SrvLog() { return string.Join("\n", ReadShared(Path.Combine(_srvDir, "MelonLoader", "Latest.log"))); }

        private static IEnumerator NetGuardProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            JObject st = Status();
            JToken net = st != null ? st["net"] : null;
            check("network guard: on, and the port is listening", net != null && (bool)net["on"] && (bool)net["listening"], net != null ? net.ToString(Newtonsoft.Json.Formatting.None) : "no status");

            // the reading thread ends behind the library's back (what a Windows network error does)
            int before = Regex.Matches(SrvLog(), "opened UDP port").Count;
            yield return ConsoleText("testnet kill", "testnet", w);
            note(w.Detail.Trim());
            float t0 = Time.realtimeSinceStartup;
            yield return WaitUntil(() => Regex.Matches(SrvLog(), "opened UDP port").Count > before, 25f);
            float took = Time.realtimeSinceStartup - t0;
            check("a port whose reading thread ended is opened again within seconds", Regex.Matches(SrvLog(), "opened UDP port").Count > before, took.ToString("0") + " s");
            yield return new WaitForSecondsRealtime(3f);
            Query.Info got = null; bool answered = false;
            Query.AskServer(_address, (i, ms) => { got = i; answered = true; });
            yield return WaitUntil(() => answered, 8f);
            check("...the server answers through it again", got != null, got != null ? got.name : "no answer");
            check("...and the player who was on is still on", Me() != null && ClientStarted(), Me() != null ? Me().Name : "gone");
            yield return Rejoin(w);
            check("...and a new join gets in", w.Ok, w.Detail);

            // the address players use: down, then back
            yield return ConsoleText("testnet public 7999", "testnet", w);
            yield return WaitUntil(() => { JObject s = Status(); return SrvLog().Contains("CAN'T reach the server through 127.0.0.1:7999") && s != null && (string)s["net"]["public"] == "unreachable"; }, 40f);
            st = Status();
            check("an address players can't reach the server through is logged and shown in status.json", SrvLog().Contains("CAN'T reach the server through 127.0.0.1:7999") && st != null && (string)st["net"]["public"] == "unreachable",
                st != null ? st["net"].ToString(Newtonsoft.Json.Formatting.None) : "no status");
            yield return ConsoleText("testnet public " + (string)st["port"], "testnet", w);
            yield return new WaitForSecondsRealtime(0.1f);
            string port = (string)st["port"];
            yield return WaitUntil(() => { JObject s = Status(); return Regex.IsMatch(SrvLog(), "can reach the server through 127.0.0.1:" + port) && s != null && (string)s["net"]["public"] == "reachable"; }, 40f);
            st = Status();
            check("...and when it works again, that too", st != null && (string)st["net"]["public"] == "reachable", st != null ? st["net"].ToString(Newtonsoft.Json.Formatting.None) : "no status");
            yield return ConsoleText("testnet public 0", "testnet", w);
        }

        /// <summary>Last: a port that stays deaf after opening it again makes the server save and restart.</summary>
        private static IEnumerator NetGuardRestartProbe(Action<string, bool, string> check, Action<string> note)
        {
            CheckWrap w = new CheckWrap();
            yield return ConsoleText("testnet deaf", "testnet", w);
            yield return WaitUntil(() => SrvLog().Contains("hasn't answered on its own port"), 70f);
            check("a server that stops answering on its own port: the port is opened again first", SrvLog().Contains("hasn't answered on its own port"), "");
            yield return WaitUntil(() => { JObject s = Status(); return s != null && (string)s["state"] == "restarting"; }, 120f);
            JObject st = Status();
            check("...and if that doesn't help, it saves and restarts (status 'restarting', exit code 3)", st != null && (string)st["state"] == "restarting" && SrvLog().Contains("saving and restarting the server to recover"),
                st != null ? "state " + st["state"] : "no status");
        }
    }
}
