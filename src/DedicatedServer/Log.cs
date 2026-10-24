using System;
using System.Collections.Generic;
using MelonLoader;

namespace DedicatedServer
{
    /// <summary>Logging that never throws, with repeat suppression, and a debug log by area.</summary>
    internal static class Log
    {
        public static MelonLogger.Instance Logger;
        private static readonly Dictionary<string, int> _repeats = new Dictionary<string, int>();

        public static void Msg(string s) { try { Logger?.Msg(s); } catch { } }
        public static void Warn(string s) { try { Logger?.Warning(s); } catch { } }
        public static void Error(string s) { try { Logger?.Error(s); } catch { } }

        /// <summary>The first time, then only at powers of ten.</summary>
        public static void Once(string key, string s)
        {
            int n;
            _repeats.TryGetValue(key, out n);
            n++;
            _repeats[key] = n;
            if (n == 1 || n == 10 || n == 100 || n == 1000 || n == 10000)
                Warn(n == 1 ? s : s + "  (x" + n + ")");
        }

        // ---- debug log ---------------------------------------------------------------------------
        //
        // Off by default. DebugLog (server: dedicatedserver.conf; player: MelonPreferences.cfg) is "off",
        // "all", or a list of areas: net, gate, chat, ground, respawn, signals, markers, query, install,
        // ui, saves. Lines go to the normal log as "[debug/<area>] ...".

        public static readonly string[] Areas = { "net", "gate", "chat", "ground", "respawn", "signals", "markers", "query", "install", "ui", "saves" };
        private static HashSet<string> _debug = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _all;
        private static string _debugSpec;

        public static void SetDebug(string spec)
        {
            spec = (spec ?? "").Trim();
            if (spec == _debugSpec) return;
            _debugSpec = spec;
            _all = spec.Equals("all", StringComparison.OrdinalIgnoreCase) || spec.Equals("on", StringComparison.OrdinalIgnoreCase);
            _debug = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_all && !spec.Equals("off", StringComparison.OrdinalIgnoreCase))
                foreach (string a in spec.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)) _debug.Add(a.Trim());
            if (_all || _debug.Count > 0) Msg("debug log on: " + (_all ? "all" : string.Join(", ", new List<string>(_debug).ToArray())));
        }

        public static bool DebugOn(string area) { return _all || _debug.Contains(area); }

        public static void Debug(string area, string s)
        {
            if (!_all && !_debug.Contains(area)) return;
            try { Logger?.Msg("[debug/" + area + "] " + s); } catch { }
        }
    }
}
