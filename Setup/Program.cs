using System;
using System.Windows.Forms;

namespace KSHPLAYER.Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool update = System.Array.Exists(args, a => a.Equals("/update", System.StringComparison.OrdinalIgnoreCase));
            Application.Run(new SetupForm(update));
        }
    }
}
