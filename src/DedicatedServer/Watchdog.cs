// A server whose main loop stops (a hang: the process is up, players get stuck loading) is restarted.
//
// The loop marks the time every frame. A background thread checks it; once the loop has been still for
// WatchdogSeconds it logs that, writes "restarting" to status.json and ends the process with exit code
// 3, which the Server Manager and StartDedicatedServer.bat take as "start it again". Nothing since the
// last save can be kept: a stuck main thread can't save. Armed only after the world is up.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DedicatedServer
{
    internal static class Watchdog
    {
        private static volatile int _beat;           // Environment.TickCount at the last frame
        private static Thread _thread;
        private static volatile bool _armed;

        [DllImport("kernel32.dll")] private static extern void ExitProcess(uint code);

        public static void Beat() { _beat = Environment.TickCount; }

        public static void Arm()
        {
            Beat();
            _armed = true;
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "DedicatedServer watchdog" };
            _thread.Start();
        }

        public static void Disarm() { _armed = false; }

        private static void Run()
        {
            bool told = false;
            double memAtStop = -1;
            while (true)
            {
                Thread.Sleep(1000);
                int limit;
                try { limit = Server.Cfg.WatchdogSeconds.Value; } catch { continue; }
                if (!_armed || Server.Stopping) continue;
                int still = unchecked(Environment.TickCount - _beat) / 1000;
                if (still < 10) { told = false; memAtStop = -1; continue; }
                double mem = Usage.PrivateMB(), total = Usage.MachineMB();
                if (memAtStop < 0) memAtStop = mem;
                if (!told)
                {
                    // said once, early: where it is stuck, before anything else happens to it
                    told = true;
                    try { Log.Warn("watchdog: the server loop has been still for " + still + " s: " + ServerPerf.Where() + "; memory " + mem.ToString("0") + " MB"); } catch { }
                }
                // stuck and eating memory: end it now, before the machine runs out (the main loop can't check this itself)
                int pct = 0;
                try { pct = Server.Cfg.MemoryLimitPercent.Value; } catch { }
                bool runaway = mem - memAtStop > 2048 || (pct > 0 && total > 0 && mem > total * pct / 100.0);
                if (runaway || (limit > 0 && still >= limit))
                {
                    try
                    {
                        Log.Error("watchdog: the server loop has not run for " + still + " s" + (runaway ? " and its memory went from " + memAtStop.ToString("0") + " MB to " + mem.ToString("0") + " MB" : " (limit " + limit + " s)")
                            + "; stuck at " + ServerPerf.Where() + "; restarting the server (exit code 3)");
                        Server.KeepThisLog();
                        Server.WriteStatusFromWatchdog("restarting");
                    }
                    catch { }
                    Thread.Sleep(500);           // let the log reach the disk
                    ExitProcess(3);
                }
            }
        }
    }
}
