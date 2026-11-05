using System.Text;
using TwinCAT.Ads;

namespace BootdataViewer;

// Reads a bootdata file from the boot folder of any target (Windows, TwinCAT/BSD, UmRT) through
// the ADS file services of the TwinCAT system service (port 10000). Only reads; the target's
// files are never changed. Downloads are kept in %LOCALAPPDATA%\BootdataViewer\Targets\<NetId>.
public static class AdsBootFiles
{
    const uint FOpen = 120, FClose = 121, FRead = 122;
    const uint ModeRead = 0x1, ModeBinary = 0x10;
    const uint PathBootPath = 4;    // relative to the target's TwinCAT boot folder

    public static string CacheFolder(string netId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootdataViewer", "Targets", netId);

    // Downloads Plc/Port_<port>.bootdata (or the -old copy) and returns the local path of the copy.
    public static string Download(string netId, int port, bool old = false)
    {
        string name = $"Port_{port}.bootdata" + (old ? "-old" : "");
        byte[] data = Read(netId, "Plc/" + name);
        string folder = CacheFolder(netId);
        Directory.CreateDirectory(folder);
        string local = Path.Combine(folder, name);
        File.WriteAllBytes(local, data);
        return local;
    }

    public static byte[] Read(string netId, string bootRelativePath)
    {
        using var client = new AdsClient { Timeout = 5000 };
        client.Connect(netId.Equals("local", StringComparison.OrdinalIgnoreCase) ? AmsNetId.Local : AmsNetId.Parse(netId), 10000);

        var handleBytes = new byte[4];
        var err = client.TryReadWrite(FOpen, ModeRead | ModeBinary | (PathBootPath << 16), handleBytes,
                                      Encoding.ASCII.GetBytes(bootRelativePath + "\0"), out _);
        if (err == AdsErrorCode.DeviceNotFound)
            throw new FileNotFoundException($"{bootRelativePath} does not exist in the boot folder of {netId}.");
        if (err != AdsErrorCode.NoError)
            throw new AdsErrorException($"Opening {bootRelativePath} on {netId} failed", err);

        uint handle = BitConverter.ToUInt32(handleBytes, 0);
        try
        {
            var result = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                err = client.TryReadWrite(FRead, handle, buffer, Array.Empty<byte>(), out int read);
                if (err != AdsErrorCode.NoError) throw new AdsErrorException($"Reading {bootRelativePath} on {netId} failed", err);
                if (read == 0) break;
                result.Write(buffer, 0, read);
            }
            return result.ToArray();
        }
        finally
        {
            client.TryReadWrite(FClose, handle, Array.Empty<byte>(), Array.Empty<byte>(), out _);
        }
    }

    // Which local file came from which target, so the write dialog can preselect it.
    public static string? SourceTarget(string localPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(localPath));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootdataViewer", "Targets");
        return dir != null && Path.GetDirectoryName(dir)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true
            ? Path.GetFileName(dir) : null;
    }
}
