using System.Text;
using TwinCAT.Ads;

namespace BootdataViewer;

public sealed record AdsTarget(string Name, string NetId, string Address, bool IsLocal)
{
    public override string ToString() =>
        IsLocal ? $"Local ({NetId})" : $"{Name} ({NetId}){(Address.Length > 0 ? " - " + Address : "")}";
}

public sealed record PlcRuntime(int Port, AdsState State)
{
    public override string ToString() => $"{Port} ({State})";
}

// Reads the ADS routes of the local TwinCAT system service and finds PLC runtimes on a target.
public static class AdsRoutes
{
    const int SystemServicePort = 10000;
    const uint EnumRemote = 0x323;      // SYSTEMSERVICE_ENUMREMOTE, index offset = route number

    public static List<AdsTarget> ReadTargets()
    {
        using var client = new AdsClient { Timeout = 3000 };
        client.Connect(AmsNetId.Local, SystemServicePort);
        var targets = new List<AdsTarget> { new("Local", AmsNetId.Local.ToString(), "", true) };
        for (uint i = 0; i < 1024; i++)
        {
            var result = client.ReadAsResult(EnumRemote, i, 0x800);
            if (result.ErrorCode == AdsErrorCode.DeviceNoMoreHandles) break;   // past the last route
            if (result.ErrorCode != AdsErrorCode.NoError)
                throw new AdsErrorException("Reading the ADS routes from the system service failed", result.ErrorCode);
            targets.Add(ParseRoute(result.Data.Span));
        }
        return targets;
    }

    // Route entry as returned by the TwinCAT 3 system service (pack 1, 44-byte header):
    //   0 AmsNetId[6], 6 u16 transport, 8 u32 flags, 12 u32 timeout, 16 u32 maxFragment,
    //   20 u32 reserved[3], 32 u32 cbAddress, 36 u32 cbName, 40 u32 cbAddNetIds,
    //   44 address\0, name\0, ...
    // e.g. 192.0.2.10.1.1, TCP/IP, flags 0x28, 60000 ms, "192.0.2.10", "CX-1A2B3C"
    const int RouteHeaderSize = 44;

    static AdsTarget ParseRoute(ReadOnlySpan<byte> b)
    {
        if (b.Length < RouteHeaderSize)
            throw new InvalidDataException($"Route entry too short ({b.Length} bytes).");
        var netId = new AmsNetId(b[..6].ToArray());
        int cbAddress = (int)BitConverter.ToUInt32(b[32..]);
        int cbName = (int)BitConverter.ToUInt32(b[36..]);
        if (RouteHeaderSize + cbAddress + cbName > b.Length)
            throw new InvalidDataException($"Route entry for {netId} has inconsistent string lengths.");
        string address = CString(b, RouteHeaderSize, cbAddress);
        string name = CString(b, RouteHeaderSize + cbAddress, cbName);
        return new AdsTarget(name.Length > 0 ? name : netId.ToString(), netId.ToString(), address, false);
    }

    static string CString(ReadOnlySpan<byte> b, int start, int len)
    {
        if (start >= b.Length || len <= 0) return "";
        var s = b.Slice(start, Math.Min(len, b.Length - start));
        int nul = s.IndexOf((byte)0);
        return Compat.Latin1.GetString((nul >= 0 ? s[..nul] : s).ToArray());
    }

    // TwinCAT 3 PLC runtimes use ports 851, 852, ...; probe the usual range.
    public static List<PlcRuntime> FindPlcRuntimes(string netId)
    {
        var found = new List<PlcRuntime>();
        var id = netId.Equals("local", StringComparison.OrdinalIgnoreCase) ? AmsNetId.Local : AmsNetId.Parse(netId);
        for (int port = 851; port <= 870; port++)
        {
            using var client = new AdsClient { Timeout = 1500 };
            client.Connect(id, port);
            var err = client.TryReadState(out var state);
            if (err == AdsErrorCode.NoError) found.Add(new PlcRuntime(port, state.AdsState));
            else if (err is AdsErrorCode.ClientSyncTimeOut or AdsErrorCode.TargetMachineNotFound or AdsErrorCode.ClientPortNotOpen)
                break;   // target unreachable; no point probing further ports
        }
        return found;
    }
}
