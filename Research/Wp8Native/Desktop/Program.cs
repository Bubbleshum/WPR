using System.Windows.Forms;
using WPR.Wp8Native;

namespace WPR.Wp8Native.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                Console.WriteLine("""
                    WPR WP8 native desktop host

                      WPR.Wp8Desktop <path-to-arm-exe>

                    Runs a WP8 "Modern Native" executable on the emulated CPU and shows every
                    frame it presents in a window. The mouse is the touch screen: press, drag
                    and release are delivered to the image as pointer events.
                    """);
                return 0;
            }

            string path = Path.GetFullPath(args[0]);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"No such file: {path}");
                return 1;
            }

            // A live host has a real pointer, so the scripted taps the console probe uses to
            // get through the splash screens must be off. Static configuration is read at type
            // initialisation, so this has to happen before anything touches the runtime.
            Environment.SetEnvironmentVariable("WPR_TAP", "0");

            PeImage image = PeImage.Load(path);
            var emulator = new ArmEmulator(
                image,
                imageDirectory: Path.GetDirectoryName(path)!,
                collectBlockStats: false);

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var window = new GameWindow(emulator, Path.GetFileNameWithoutExtension(path));
            Application.Run(window);

            Console.WriteLine($"stopped: {window.Outcome}");
            Console.WriteLine($"frames presented: {emulator.Direct3D.PresentCount:N0}");
            Console.WriteLine($"main loop turns: {emulator.ProcessEventsCalls:N0}");
            foreach (string line in emulator.InputDelivered.TakeLast(6))
            {
                Console.WriteLine($"input: {line}");
            }

            return 0;
        }
    }
}
