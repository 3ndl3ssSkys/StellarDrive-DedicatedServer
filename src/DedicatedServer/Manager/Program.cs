// StellarDrive Server Manager: set up, run and watch StellarDrive Demo dedicated servers.
//   StellarDriveServerManager.exe                  the app
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SDServerManager
{
    public static class Program
    {
        public const string Version = "1.4.2";
        public static Icon AppIcon;
        public static bool Quiet;

        [STAThread]
        public static int Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppIcon = MakeIcon();
#if SDSMTEST
            if (args.Length >= 2 && args[0] == "--selftest") { Quiet = true; return SelfTest.Run(args[1]); }    // test build only
#endif
            ManagerSettings settings = ManagerSettings.Load();
            Application.Run(new MainForm(settings));
            return 0;
        }

        /// <summary>A ring and a dot in the game's blue: drawn, so the app needs no image files.</summary>
        private static Icon MakeIcon()
        {
            using (Bitmap b = new Bitmap(64, 64))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Theme.Back)) g.FillEllipse(bg, 2, 2, 60, 60);
                    using (Pen p = new Pen(Theme.Line, 6)) g.DrawEllipse(p, 10, 10, 44, 44);
                    using (SolidBrush dot = new SolidBrush(Theme.Line)) g.FillEllipse(dot, 25, 25, 14, 14);
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }
    }
}
