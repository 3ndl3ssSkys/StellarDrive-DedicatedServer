// Other mods' settings on the server (their MelonPreferences), for the Server Manager's Mods page.
// <data>/modsettings.json lists every category with its entries (type, value, default, description)
// and the mod it most likely belongs to, written when the server is ready and after each change.
// `modset <category> <entry> <value>` changes one and saves UserData/MelonPreferences.cfg.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DedicatedServer
{
    internal static class ModSettings
    {
        // the server's own settings (dedicatedserver.conf, the Settings page) and its player-side ones
        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DedicatedServerHost", "DedicatedServer" };

        public static string Path_ { get { return Path.Combine(Server.DataDir, "modsettings.json"); } }

        private static string Squash(string s)
        {
            s = (s ?? "").ToLowerInvariant().Replace("+", "plus");
            return new string(s.Where(char.IsLetterOrDigit).ToArray());
        }

        /// <summary>The mod a settings category belongs to, by name ("" if none fits).</summary>
        public static string Owner(string category)
        {
            string c = Squash(category);
            if (c.Length == 0) return "";
            List<MelonMod> mods = MelonMod.RegisteredMelons.Where(m => m != null && m.Info != null).ToList();
            Func<MelonMod, IEnumerable<string>> names = m =>
            {
                string file = "";
                try { file = Path.GetFileNameWithoutExtension(m.MelonAssembly != null ? m.MelonAssembly.Location : ""); } catch { }
                string n = Squash(m.Info.Name);
                return new[] { n, n.StartsWith("demo") ? n.Substring(4) : n, Squash(file), Squash(m.GetType().Namespace) }.Where(x => x.Length > 0);
            };
            MelonMod hit = mods.FirstOrDefault(m => names(m).Contains(c))
                ?? mods.FirstOrDefault(m => names(m).Any(n => n.Length >= 4 && c.Length >= 4 && (n.Contains(c) || c.Contains(n))));
            return hit != null ? hit.Info.Name : "";
        }

        public static void Write()
        {
            try
            {
                JArray cats = new JArray();
                foreach (MelonPreferences_Category cat in MelonPreferences.Categories)
                {
                    if (cat == null || Skip.Contains(cat.Identifier) || cat.IsHidden) continue;
                    JArray entries = new JArray();
                    foreach (MelonPreferences_Entry e in cat.Entries)
                    {
                        if (e == null || e.IsHidden) continue;
                        Type t = e.GetReflectedType();
                        JObject o = new JObject
                        {
                            ["id"] = e.Identifier,
                            ["name"] = string.IsNullOrEmpty(e.DisplayName) ? e.Identifier : e.DisplayName,
                            ["desc"] = e.Description ?? "",
                            ["type"] = TypeName(t),
                            ["value"] = Text(e.BoxedValue),
                            ["default"] = Safe(() => e.GetDefaultValueAsString()),
                        };
                        if (t.IsEnum) o["choices"] = new JArray(Enum.GetNames(t));
                        entries.Add(o);
                    }
                    if (entries.Count == 0) continue;
                    cats.Add(new JObject { ["id"] = cat.Identifier, ["name"] = string.IsNullOrEmpty(cat.DisplayName) ? cat.Identifier : cat.DisplayName, ["mod"] = Owner(cat.Identifier), ["entries"] = entries });
                }
                File.WriteAllText(Path_, new JObject { ["writtenUtc"] = DateTime.UtcNow.ToString("o"), ["categories"] = cats }.ToString(Formatting.Indented));
            }
            catch (Exception e) { Log.Once("modsettings", "modsettings.json: " + e.Message); }
        }

        private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

        private static string TypeName(Type t)
        {
            if (t == typeof(bool)) return "bool";
            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong)) return "int";
            if (t == typeof(float) || t == typeof(double)) return "number";
            if (t == typeof(string)) return "text";
            if (t.IsEnum) return "choice";
            return "other";
        }

        private static string Text(object v)
        {
            if (v == null) return "";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is IFormattable) return ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture);
            return v.ToString();
        }

        /// <summary>Sets one entry and saves MelonPreferences.cfg. Returns the reply.</summary>
        public static string Set(string category, string entry, string value)
        {
            MelonPreferences_Category cat = MelonPreferences.Categories.FirstOrDefault(c => c != null && string.Equals(c.Identifier, category, StringComparison.OrdinalIgnoreCase));
            if (cat == null || Skip.Contains(cat.Identifier)) return "No mod settings called [" + category + "].";
            MelonPreferences_Entry e = cat.Entries.FirstOrDefault(x => x != null && string.Equals(x.Identifier, entry, StringComparison.OrdinalIgnoreCase));
            if (e == null) return "[" + cat.Identifier + "] has no " + entry + ".";
            Type t = e.GetReflectedType();
            object v;
            string v0 = (value ?? "").Trim();
            try
            {
                if (t == typeof(bool))
                {
                    string l = v0.ToLowerInvariant();
                    if (l == "on" || l == "true" || l == "yes" || l == "1") v = true;
                    else if (l == "off" || l == "false" || l == "no" || l == "0") v = false;
                    else return entry + " is on or off.";
                }
                else if (t == typeof(string)) v = v0.Length >= 2 && v0[0] == '"' && v0[v0.Length - 1] == '"' ? v0.Substring(1, v0.Length - 2) : v0;
                else if (t.IsEnum) v = Enum.Parse(t, v0, true);
                else v = Convert.ChangeType(v0, t, CultureInfo.InvariantCulture);
            }
            catch { return entry + " needs " + (TypeName(t) == "int" ? "a whole number" : TypeName(t) == "number" ? "a number" : TypeName(t) == "choice" ? "one of " + string.Join(", ", Enum.GetNames(t)) : "a " + t.Name) + "."; }
            try { e.BoxedValue = v; }
            catch (Exception ex) { return "Not set: " + ex.Message; }
            try { MelonPreferences.Save(); } catch (Exception ex) { Log.Warn("modset: could not save MelonPreferences.cfg: " + ex.Message); }
            Write();
            Log.Msg("mod setting [" + cat.Identifier + "] " + e.Identifier + " = " + Text(e.BoxedValue));
            return "[" + cat.Identifier + "] " + e.Identifier + " = " + Text(e.BoxedValue) + ". Mods that read it as they go use it now; others when the server restarts.";
        }
    }
}
