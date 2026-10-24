// Safety for a server started by the verify harness (dormant otherwise): it stops itself
// when the test game that started it is gone, or after a time limit, so a test can never
// leave a server running on Ethan's machine.
//   SDD_DS_PARENT_PID=<pid>   stop when this process has exited
//   SDD_DS_QUIT_AFTER=<sec>   stop after this long whatever happens
//   SDD_DS_HEARTBEAT=<file>   stop when this file hasn't been touched for 90 s (a test server on another PC, started by the
//                             Server Manager there: the test game here touches the file in the shared folder every few seconds)
using System;
using System.Diagnostics;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Test
    {
        private static Process _parent;
        private static DateTime _parentStart;
        private static float _quitAt = -1f, _nextLook;
        private static bool _armed;
        private static string _heartbeat;
        public static bool Armed { get { return _armed; } }

        public static void InitServer()
        {
            int pid;
            if (int.TryParse(Environment.GetEnvironmentVariable("SDD_DS_PARENT_PID") ?? "", out pid))
            {
                try { _parent = Process.GetProcessById(pid); _parentStart = _parent.StartTime; _armed = true; }
                catch { Log.Warn("test parent " + pid + " is already gone - stopping"); _armed = true; _quitAt = 0f; }
            }
            float s;
            if (float.TryParse(Environment.GetEnvironmentVariable("SDD_DS_QUIT_AFTER") ?? "", out s) && s > 0f) { _quitAt = s; _armed = true; }
            _heartbeat = Environment.GetEnvironmentVariable("SDD_DS_HEARTBEAT");
            if (!string.IsNullOrEmpty(_heartbeat)) { _armed = true; Log.Msg("test server: stops when " + _heartbeat + " goes 90 s untouched"); }
            if (_armed) Log.Msg("test server: stops when its test game is gone" + (_quitAt > 0 ? " or after " + _quitAt + " s" : ""));
        }

        public static void Update()
        {
            if (!_armed || Server.Stopping) return;
            float now = Time.realtimeSinceStartup;
            if (_quitAt >= 0f && now >= _quitAt) { Server.Stop("test time limit"); return; }
            if (now < _nextLook) return;
            _nextLook = now + 2f;
            if (_heartbeat != null && now > 120f)
            {
                DateTime last = DateTime.MinValue;
                try { if (System.IO.File.Exists(_heartbeat)) last = System.IO.File.GetLastWriteTimeUtc(_heartbeat); } catch { last = DateTime.UtcNow; }
                if ((DateTime.UtcNow - last).TotalSeconds > 90) { Server.Stop("the test game on the other PC has gone quiet (no heartbeat for 90 s)"); return; }
            }
            if (_parent == null) return;
            bool gone;
            try { _parent.Refresh(); gone = _parent.HasExited || _parent.StartTime != _parentStart; }
            catch { gone = true; }
            if (gone) Server.Stop("the test game that started this server has gone");
        }
    }
}
