// The research tree as this game has it: every research node, and for every mod part (from each
// loaded mod's own copy of Common's PartKit) which node unlocks it. The server's `parts` command
// and the client's probe both use it, so the two sides' trees can be compared. The tree is data
// each game builds for itself in memory (mods add nodes and move parts into them), so if the server
// builds it differently from its players, a player can research a node the server doesn't know,
// and nothing happens.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Research;
using Research.Interface.Values;
using Ships.Interface.Settings;
using UnityEngine;

namespace DedicatedServer
{
    internal static class ResearchTree
    {
        public class PartRow
        {
            public string Mod, Label;
            public ushort Id;
            public ushort NodeId;          // 0 = in no node
            public string Node = "";
            public bool Unlocked;
        }

        public static List<ResearchSettings> Nodes()
        {
            Dictionary<ushort, ResearchSettings> byId = new Dictionary<ushort, ResearchSettings>();
            foreach (ResearchSettingsList l in Resources.FindObjectsOfTypeAll<ResearchSettingsList>())
                if (l != null && l.list != null)
                    foreach (ResearchSettings r in l.list)
                        if (r != null && !byId.ContainsKey(r.id)) byId[r.id] = r;
            return byId.Values.OrderBy(r => r.id).ToList();
        }

        /// <summary>"id:name" of every node: two games with the same text have the same tree.</summary>
        public static string Signature()
        {
            return string.Join(";", Nodes().Select(r => r.id + ":" + r.techName).ToArray());
        }

        public static List<PartRow> ModParts()
        {
            List<PartRow> rows = new List<PartRow>();
            List<ResearchSettings> nodes = Nodes();
            ResearchClientProxy proxy = Core.Services.ServiceLocator.GetService<ResearchClientProxy>();
            foreach (MelonMod m in MelonMod.RegisteredMelons)
            {
                if (m == null || m.Info == null) continue;
                Type kit = null;
                try { kit = m.GetType().Assembly.GetTypes().FirstOrDefault(t => t.Name == "PartKit" && t.Namespace != null && t.Namespace.EndsWith(".Common")); } catch { }
                if (kit == null) continue;
                IEnumerable defs = kit.GetField("Defs", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IEnumerable;
                if (defs == null) continue;
                foreach (object d in defs)
                {
                    Type dt = d.GetType();
                    ushort id = (ushort)dt.GetField("Id").GetValue(d);
                    PartSettings s = dt.GetField("Settings").GetValue(d) as PartSettings;
                    PartRow row = new PartRow { Mod = m.Info.Name, Label = dt.GetField("Label").GetValue(d) as string ?? id.ToString(), Id = id };
                    ResearchSettings node = nodes.FirstOrDefault(r => r.unlockedParts != null && r.unlockedParts.Any(p => p != null && p.id == id));
                    if (node != null) { row.NodeId = node.id; row.Node = node.techName; }
                    try { row.Unlocked = s != null && proxy != null && proxy.IsPartUnlocked(s); } catch { row.Unlocked = false; }
                    rows.Add(row);
                }
            }
            return rows;
        }
    }
}
