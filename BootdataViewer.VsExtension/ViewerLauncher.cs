using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BootdataViewer.VsExtension
{
    // Starts the bundled viewer (Viewer\BootdataViewer.exe next to this assembly) as its own process.
    internal static class ViewerLauncher
    {
        public static string ExePath =>
            Path.Combine(Path.GetDirectoryName(typeof(ViewerLauncher).Assembly.Location) ?? "", "Viewer", "BootdataViewer.exe");

        public static void Start(params string[] args)
        {
            if (!File.Exists(ExePath)) throw new FileNotFoundException("The bootdata viewer is missing from the extension.", ExePath);
            Process.Start(new ProcessStartInfo(ExePath, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(ExePath),
            });
        }

        // Windows command-line quoting for paths with spaces or quotes.
        static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var s = new System.Text.StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                s.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                s.Append(c);
            }
            s.Append('\\', backslashes * 2).Append('"');
            return s.ToString();
        }
    }
}
