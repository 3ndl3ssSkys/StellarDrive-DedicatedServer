// Typing commands into the server's console window.
//
// MelonLoader opens a console window for the game process. The game is a windowed program, so
// its standard input is not that console; the console's own input buffer (CONIN$) is read
// directly instead, on a background thread, and each line is handed to the main thread.
// If the process's input was redirected (a pipe, e.g. a service wrapper or a script), that
// is read instead.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DedicatedServer
{
    internal static class ServerConsole
    {
        private static readonly Queue<string> _lines = new Queue<string>();
        private static Thread _thread;
        public static string Source = "none";

        public static void Start(bool enabled)
        {
            if (!enabled) { Source = "off"; return; }
            IntPtr stdin = GetStdHandle(STD_INPUT_HANDLE);
            uint type = stdin != IntPtr.Zero && stdin != new IntPtr(-1) ? GetFileType(stdin) : 0;
            if (type == FILE_TYPE_PIPE || type == FILE_TYPE_DISK)
            {
                Source = "standard input";
                _thread = new Thread(ReadStdin) { IsBackground = true, Name = "DedicatedServer stdin" };
            }
            else if (GetConsoleWindow() != IntPtr.Zero)
            {
                Source = "console window";
                _conin = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (_conin == IntPtr.Zero || _conin == new IntPtr(-1)) { Log.Warn("could not open the console for input (error " + Marshal.GetLastWin32Error() + ")"); return; }
                QuickEditOff = NoQuickEdit(_conin);
                _thread = new Thread(ReadConsole) { IsBackground = true, Name = "DedicatedServer console" };
            }
            else
            {
                Source = "none";
                Log.Msg("no console window (MelonLoader's console is hidden): send commands through " + Server.InboxPath + ", or in game as an operator");
                return;
            }
            _thread.Start();
            Log.Msg("reading commands from the " + Source + ". Type 'help'.");
        }

        /// <summary>Main thread: run whatever was typed.</summary>
        public static void Drain()
        {
            while (true)
            {
                string line;
                lock (_lines) { if (_lines.Count == 0) return; line = _lines.Dequeue(); }
                Commands.Run(line, Caller.Console("console"));
            }
        }

        private static void Push(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0) return;
            lock (_lines) _lines.Enqueue(line);
        }

        private static void ReadStdin()
        {
            try
            {
                using (StreamReader r = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                {
                    string l;
                    while ((l = r.ReadLine()) != null) Push(l);
                }
            }
            catch (Exception e) { Log.Warn("standard input closed: " + e.Message); }
        }

        private static IntPtr _conin;
        public static bool QuickEditOff;

        /// <summary>
        /// A console window in QuickEdit mode starts selecting text on any click in it, and while a
        /// selection is up EVERY write to that console blocks. MelonLoader logs on the game's main
        /// thread, so one stray click would freeze the whole server - players rubber-band until a key
        /// is pressed in the window. QuickEdit goes; right-click Mark still selects text.
        /// </summary>
        private static bool NoQuickEdit(IntPtr h)
        {
            uint mode;
            if (!GetConsoleMode(h, out mode)) return false;
            uint want = (mode | ENABLE_EXTENDED_FLAGS | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT) & ~ENABLE_QUICK_EDIT_MODE;
            if (!SetConsoleMode(h, want)) return false;
            uint now;
            bool off = GetConsoleMode(h, out now) && (now & ENABLE_QUICK_EDIT_MODE) == 0;
            Log.Msg(off ? "console QuickEdit is off (a click in the window can no longer freeze the server)" : "could not turn the console's QuickEdit off");
            return off;
        }

        private static void ReadConsole()
        {
            IntPtr h = _conin;
            StringBuilder pending = new StringBuilder();
            char[] buf = new char[512];
            while (true)
            {
                uint read;
                if (!ReadConsoleW(h, buf, (uint)buf.Length, out read, IntPtr.Zero)) { Thread.Sleep(200); continue; }
                for (int i = 0; i < read; i++)
                {
                    char ch = buf[i];
                    if (ch == '\n') { Push(pending.ToString()); pending.Length = 0; }
                    else if (ch != '\r') pending.Append(ch);
                }
            }
        }

        private const int STD_INPUT_HANDLE = -10;
        private const uint FILE_TYPE_DISK = 1, FILE_TYPE_PIPE = 3;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private const uint ENABLE_PROCESSED_INPUT = 1, ENABLE_LINE_INPUT = 2, ENABLE_ECHO_INPUT = 4, ENABLE_QUICK_EDIT_MODE = 0x40, ENABLE_EXTENDED_FLAGS = 0x80;

        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ReadConsoleW(IntPtr h, [Out] char[] buf, uint toRead, out uint read, IntPtr control);
    }
}
