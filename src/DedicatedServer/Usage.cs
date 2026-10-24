// What the server uses of the machine, and what the machine has:
//   * at start, one line of specs: OS, CPU, cores, RAM, Unity and game versions (no names, paths or addresses);
//   * every ResourceLogMinutes, a line: the server's memory (process, managed heap, Unity), CPU, threads,
//     handles, counts of the Unity objects that pile up when something leaks, the machine's RAM and CPU,
//     and the free disk where the world is;
//   * every minute a row in <data>/resources.csv (seven days kept) for the Server Manager's charts;
//   * status.json "resources" with the latest numbers;
//   * MemoryLimitPercent: a server using more than that share of the machine's RAM for a minute warns
//     everyone and restarts (saving first), before the machine runs out.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DedicatedServer
{
    internal static class Usage
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        // the game's runtime (Mono) reports 0 for its own process's memory, threads and handles: Windows is asked directly
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        private struct THREADENTRY32 { public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID; public int tpBasePri, tpDeltaPri; public uint dwFlags; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll")] private static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 e);
        [DllImport("kernel32.dll")] private static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 e);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        /// <summary>The server's private memory now (safe from any thread).</summary>
        public static double PrivateMB()
        {
            PROCESS_MEMORY_COUNTERS_EX pm;
            return GetProcessMemoryInfo(GetCurrentProcess(), out pm, (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX))) ? pm.PrivateUsage.ToUInt64() / 1048576.0 : 0;
        }

        public static double MachineMB()
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(m) ? m.ullTotalPhys / 1048576.0 : 0;
        }

        private static int ThreadCount()
        {
            IntPtr snap = CreateToolhelp32Snapshot(4, 0);       // TH32CS_SNAPTHREAD
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
            try
            {
                uint me = GetCurrentProcessId();
                int n = 0;
                THREADENTRY32 e = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32)) };
                if (!Thread32First(snap, ref e)) return 0;
                do { if (e.th32OwnerProcessID == me) n++; e.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32)); } while (Thread32Next(snap, ref e));
                return n;
            }
            finally { CloseHandle(snap); }
        }

        public class Sample
        {
            public DateTime At;
            public double ProcessMB, PrivateMB, ManagedMB, UnityMB, SystemUsedMB, SystemTotalMB, CommitMB, CommitLimitMB, CpuProcess, CpuSystem, DiskFreeGB;
            public int Threads, Handles, GameObjects, Meshes, Textures, Colliders;
        }

        public static Sample Last;
        public static string Specs = "";
        private static float _nextMinute, _nextLog, _overSince = -1f;
        private static long _lastCpu, _lastSysIdle, _lastSysTotal;
        private static DateTime _lastCpuAt;
        private static double _baseline = -1;
        private static bool _limitTriggered;
        public static int CountsTaken;

        public static string CsvPath { get { return Path.Combine(Server.DataDir, "resources.csv"); } }

        /// <summary>The machine, once at start: nothing that names it, its owner or where it is.</summary>
        public static void LogSpecs()
        {
            try
            {
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                GlobalMemoryStatusEx(m);
                Specs = SystemInfo.operatingSystem + "; " + SystemInfo.processorType.Trim() + ", " + SystemInfo.processorCount + " threads"
                    + (SystemInfo.processorFrequency > 0 ? " at " + (SystemInfo.processorFrequency / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " GHz" : "")
                    + "; " + Gb(m.ullTotalPhys) + " RAM; Unity " + Application.unityVersion + ", game " + Application.version
                    + "; graphics " + (SystemInfo.graphicsDeviceType.ToString() == "Null" ? "none (headless)" : SystemInfo.graphicsDeviceName);
                Log.Msg("system: " + Specs);
            }
            catch (Exception e) { Log.Warn("system specs: " + e.Message); }
        }

        private static string Gb(ulong bytes) { return (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"; }
        private static string Mb(double mb) { return mb >= 1024 ? (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : mb.ToString("0", CultureInfo.InvariantCulture) + " MB"; }

        /// <summary>Every frame on the server.</summary>
        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextMinute) return;
            _nextMinute = now + 60f;
            int logEvery = Server.Cfg.ResourceLogMinutes.Value;
            bool counts = logEvery > 0 && now >= _nextLog;
            Sample s = Take(counts);
            if (s == null) return;
            Last = s;
            Append(s);
            if (_baseline < 0 && now > 300f) _baseline = s.PrivateMB;      // five minutes in: the world is loaded and settled
            if (counts)
            {
                _nextLog = now + logEvery * 60f - 1f;
                Log.Msg("resources: " + Line(s));
                Growth(s);
            }
            Limit(s, now);
        }

        public static string Line(Sample s)
        {
            return "server " + Mb(s.PrivateMB) + " private, " + Mb(s.ProcessMB) + " in RAM (managed heap " + Mb(s.ManagedMB) + ", Unity " + Mb(s.UnityMB) + "), CPU "
                + s.CpuProcess.ToString("0", CultureInfo.InvariantCulture) + "%, " + s.Threads + " threads, " + s.Handles + " handles"
                + (s.GameObjects > 0 ? "; objects: " + s.GameObjects + " game objects, " + s.Meshes + " meshes, " + s.Textures + " textures, " + s.Colliders + " colliders" : "")
                + " | machine: RAM " + Mb(s.SystemUsedMB) + " of " + Mb(s.SystemTotalMB) + " used (" + (100 * s.SystemUsedMB / Math.Max(1, s.SystemTotalMB)).ToString("0", CultureInfo.InvariantCulture)
                + "%), committed " + Mb(s.CommitMB) + " of " + Mb(s.CommitLimitMB) + ", CPU " + s.CpuSystem.ToString("0", CultureInfo.InvariantCulture) + "%"
                + (s.DiskFreeGB >= 0 ? ", " + s.DiskFreeGB.ToString("0.0", CultureInfo.InvariantCulture) + " GB free on the world's drive" : "")
                + " | players " + Server.ActivePlayers().Count + ", up " + (Server.Ready ? ServerData.Span((DateTime.UtcNow - Server.Data.RunStart).TotalSeconds) : "-");
        }

        public static Sample Take(bool counts)
        {
            try
            {
                Sample s = new Sample { At = DateTime.UtcNow };
                IntPtr me = GetCurrentProcess();
                PROCESS_MEMORY_COUNTERS_EX pm;
                if (GetProcessMemoryInfo(me, out pm, (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX))))
                {
                    s.ProcessMB = pm.WorkingSetSize.ToUInt64() / 1048576.0;
                    s.PrivateMB = pm.PrivateUsage.ToUInt64() / 1048576.0;
                }
                uint handles;
                if (GetProcessHandleCount(me, out handles)) s.Handles = (int)handles;
                s.Threads = ThreadCount();
                long created, exited, kernelT, userT;
                if (GetProcessTimes(me, out created, out exited, out kernelT, out userT))
                {
                    long cpu = kernelT + userT;
                    DateTime t = DateTime.UtcNow;
                    if (_lastCpuAt != DateTime.MinValue)
                        s.CpuProcess = 100.0 * (cpu - _lastCpu) / Math.Max(1, (t - _lastCpuAt).Ticks) / Math.Max(1, Environment.ProcessorCount);
                    _lastCpu = cpu; _lastCpuAt = t;
                }
                s.ManagedMB = GC.GetTotalMemory(false) / 1048576.0;
                try { s.UnityMB = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0; } catch { }
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(m))
                {
                    s.SystemTotalMB = m.ullTotalPhys / 1048576.0;
                    s.SystemUsedMB = (m.ullTotalPhys - m.ullAvailPhys) / 1048576.0;
                    s.CommitLimitMB = m.ullTotalPageFile / 1048576.0;
                    s.CommitMB = (m.ullTotalPageFile - m.ullAvailPageFile) / 1048576.0;
                }
                long idle, kernel, user;
                if (GetSystemTimes(out idle, out kernel, out user))
                {
                    long total = kernel + user;
                    if (_lastSysTotal > 0 && total > _lastSysTotal) s.CpuSystem = 100.0 * (1.0 - (idle - _lastSysIdle) / (double)(total - _lastSysTotal));
                    _lastSysIdle = idle; _lastSysTotal = total;
                }
                s.DiskFreeGB = -1;
                try { s.DiskFreeGB = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Server.WorldPath))).AvailableFreeSpace / 1073741824.0; } catch { }
                if (counts)
                {
                    // what piles up when something leaks (these look through every object: only every few minutes)
                    s.GameObjects = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>().Length;
                    s.Meshes = UnityEngine.Resources.FindObjectsOfTypeAll<Mesh>().Length;
                    s.Textures = UnityEngine.Resources.FindObjectsOfTypeAll<Texture>().Length;
                    s.Colliders = UnityEngine.Resources.FindObjectsOfTypeAll<Collider>().Length;
                    CountsTaken++;
                }
                return s;
            }
            catch (Exception e) { Log.Once("resources", "resources: " + e.Message); return null; }
        }

        // ---- the record the Manager draws -----------------------------------------------------------

        private static int _appended;

        private static void Append(Sample s)
        {
            try
            {
                string path = CsvPath;
                if (!File.Exists(path)) File.WriteAllText(path, "utc,private_mb,ram_mb,managed_mb,unity_mb,cpu_process,machine_used_mb,machine_total_mb,cpu_machine,players,game_objects,meshes\n");
                File.AppendAllText(path, string.Join(",", new[]
                {
                    s.At.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), F(s.PrivateMB), F(s.ProcessMB), F(s.ManagedMB), F(s.UnityMB), F(s.CpuProcess),
                    F(s.SystemUsedMB), F(s.SystemTotalMB), F(s.CpuSystem), Server.ActivePlayers().Count.ToString(CultureInfo.InvariantCulture),
                    s.GameObjects.ToString(CultureInfo.InvariantCulture), s.Meshes.ToString(CultureInfo.InvariantCulture),
                }) + "\n");
                if (++_appended % 60 == 0) Trim(path);
            }
            catch (Exception e) { Log.Once("resources-csv", "resources.csv: " + e.Message); }
        }

        private static string F(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }

        private static void Trim(string path)
        {
            string[] lines = File.ReadAllLines(path);
            string cut = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            List<string> keep = lines.Take(1).Concat(lines.Skip(1).Where(l => string.CompareOrdinal(l, cut) >= 0)).ToList();
            if (keep.Count < lines.Length) File.WriteAllLines(path, keep.ToArray());
        }

        public static JObject Status()
        {
            Sample s = Last;
            if (s == null) return null;
            return new JObject
            {
                ["privateMB"] = Math.Round(s.PrivateMB), ["ramMB"] = Math.Round(s.ProcessMB), ["managedMB"] = Math.Round(s.ManagedMB), ["unityMB"] = Math.Round(s.UnityMB),
                ["cpu"] = Math.Round(s.CpuProcess, 1), ["threads"] = s.Threads, ["handles"] = s.Handles,
                ["machineUsedMB"] = Math.Round(s.SystemUsedMB), ["machineTotalMB"] = Math.Round(s.SystemTotalMB), ["machineCpu"] = Math.Round(s.CpuSystem, 1),
                ["diskFreeGB"] = Math.Round(s.DiskFreeGB, 1), ["specs"] = Specs,
                ["limitPercent"] = Server.Cfg.MemoryLimitPercent.Value,
            };
        }

        // ---- a leak shows as steady growth ----------------------------------------------------------

        private static void Growth(Sample s)
        {
            if (_baseline < 0) return;
            double grown = s.PrivateMB - _baseline;
            double hours = Math.Max(0.1, (Time.realtimeSinceStartup - 300f) / 3600.0);
            if (grown > 1024 && grown / hours > 300)
                Log.Warn("resources: the server has grown by " + Mb(grown) + " since it settled (" + Mb(grown / hours) + " an hour). If it keeps growing, send the debug report (Logs page) to whoever looks after the mods.");
        }

        private static void Limit(Sample s, float now)
        {
            int pct = Server.Cfg.MemoryLimitPercent.Value;
            if (pct <= 0 || s.SystemTotalMB <= 0 || _limitTriggered) return;
            double limit = s.SystemTotalMB * pct / 100.0;
            bool over = s.PrivateMB > limit;
            if (!over) { _overSince = -1f; return; }
            if (_overSince < 0) { _overSince = now; Log.Warn("resources: the server uses " + Mb(s.PrivateMB) + ", over MemoryLimitPercent (" + pct + "% = " + Mb(limit) + ")"); return; }
            if (now - _overSince < 55f) return;
            _limitTriggered = true;
            Log.Error("resources: the server has used more than " + pct + "% of this machine's memory for a minute (" + Mb(s.PrivateMB) + " of " + Mb(s.SystemTotalMB) + "); restarting it, after saving, before the machine runs out");
            if (!Chat.Pending) Chat.Schedule(true, 120f, "the server is using too much memory");
        }
    }
}
