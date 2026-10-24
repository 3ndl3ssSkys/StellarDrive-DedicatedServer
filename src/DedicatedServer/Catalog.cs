// <data>/catalog.json: the names behind the ids a world file uses (planets, items, ship parts), written
// when the server is ready. The Server Manager uses it to show a save's players and ships by name.
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Catalog
    {
        public static void Write()
        {
            try
            {
                JObject planets = new JObject(), items = new JObject(), parts = new JObject();
                foreach (Planets.Planet p in Resources.FindObjectsOfTypeAll<Planets.Planet>())
                    if (p != null && p.gameObject.scene.IsValid()) planets[p.Id.ToString()] = p.Name;
                foreach (Items.Model.ItemSettings i in Resources.FindObjectsOfTypeAll<Items.Model.ItemSettings>())
                    if (i != null) items[i.id.ToString()] = string.IsNullOrEmpty(i.itemName) ? i.name : i.itemName;
                foreach (Ships.Interface.Settings.PartSettings s in new Ships.Interface.Services.PartSettingsProvider().GetAllPartSettings())
                    if (s != null) parts[s.id.ToString()] = string.IsNullOrEmpty(s.fullLabel) ? s.name : s.fullLabel;
                JObject o = new JObject { ["writtenUtc"] = DateTime.UtcNow.ToString("o"), ["planets"] = planets, ["items"] = items, ["parts"] = parts };
                File.WriteAllText(Path.Combine(Server.DataDir, "catalog.json"), o.ToString());
                Log.Debug("saves", "catalog: " + planets.Count + " planet(s), " + items.Count + " item(s), " + parts.Count + " part(s)");
            }
            catch (Exception e) { Log.Once("catalog", "catalog.json: " + e.Message); }
        }
    }
}
