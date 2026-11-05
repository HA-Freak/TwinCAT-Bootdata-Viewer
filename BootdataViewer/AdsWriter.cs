using System.Text;
using TwinCAT.Ads;

namespace BootdataViewer;

public enum WriteStatus { Equal, Differs, NotFound, SizeMismatch, TypeMismatch, Damaged, Written, Failed }

public sealed class WriteItem
{
    public SymbolEntry Symbol = null!;
    public string Name => Symbol.FullName;
    public string FileType = "";
    public string PlcType = "";
    public uint PlcSize;
    public byte[]? PlcData;
    public WriteStatus Status;
    public string Message = "";

    // Only these can be written; a mismatch means the PLC program changed since the file was saved.
    public bool CanWrite => Status is WriteStatus.Equal or WriteStatus.Differs;
}

// Writes values from a bootdata file into a running PLC by symbol name, using
// the standard ADS symbol services:
//   0xF009 SYM_INFOBYNAMEEX   symbol size/type for a name
//   0xF003 SYM_HNDBYNAME      handle for a name
//   0xF005 SYM_VALBYHND       read/write value by handle
//   0xF006 SYM_RELEASEHND     release handle
public sealed class AdsWriter : IDisposable
{
    const uint SymInfoByNameEx = 0xF009;
    const uint SymHandleByName = 0xF003;
    const uint SymValueByHandle = 0xF005;
    const uint SymReleaseHandle = 0xF006;

    readonly AdsClient _client = new();
    readonly BootdataFile _file;
    readonly ValueDecoder _decoder;

    public AdsWriter(BootdataFile file, string netId, int port)
    {
        _file = file;
        _decoder = new ValueDecoder(file);
        _client.Timeout = 5000;
        if (string.IsNullOrWhiteSpace(netId) || netId.Equals("local", StringComparison.OrdinalIgnoreCase))
            _client.Connect(AmsNetId.Local, port);
        else
            _client.Connect(AmsNetId.Parse(netId.Trim()), port);
        var err = _client.TryReadState(out var state);
        if (err == AdsErrorCode.TargetPortNotFound)
            throw new InvalidOperationException(
                $"No PLC runtime on port {port} at {_client.Address?.NetId}" + SystemStateHint(_client.Address?.NetId) + ".");
        if (err != AdsErrorCode.NoError)
            throw new AdsErrorException($"Reading the PLC state on port {port} failed", err);
        // Comparing also works in Stop; writing requires Run (see EnsureRunning).
        if (state.AdsState != AdsState.Run && state.AdsState != AdsState.Stop)
            throw new InvalidOperationException($"PLC on port {port} is in state {state.AdsState}.");
        PlcState = state.AdsState;
    }

    // The state seen by the last ReadState call.
    public AdsState PlcState { get; private set; }
    public bool IsRunning => PlcState == AdsState.Run;

    public AdsState ReadState()
    {
        var err = _client.TryReadState(out var state);
        if (err != AdsErrorCode.NoError) throw new AdsErrorException("Reading the PLC state failed", err);
        PlcState = state.AdsState;
        return PlcState;
    }

    // Values are only written into a running PLC; the state is read live, not taken from the connect.
    public void EnsureRunning()
    {
        var state = ReadState();
        if (state != AdsState.Run)
            throw new InvalidOperationException($"The PLC is in {state}, not in Run. Start the PLC before writing.");
    }

    // Port 10000 is the TwinCAT system service; its state explains a missing PLC port (e.g. Config mode).
    static string SystemStateHint(AmsNetId? netId)
    {
        if (netId == null) return "";
        using var sys = new AdsClient { Timeout = 3000 };
        try
        {
            sys.Connect(netId, 10000);
            return sys.TryReadState(out var s) == AdsErrorCode.NoError ? $" (TwinCAT system is in {s.AdsState} mode)" : "";
        }
        catch { return ""; }
    }

    // Whether the PLC program saves its persistent data itself (FB_S_UPS / FB_WritePersistentData).
    public PersistentSaveCheck CheckPersistentSave() => PersistentSaveCheck.Run(_client);

    // Values are written per bootdata entry that carries data; containers (FB instances) only group them.
    public static IEnumerable<SymbolEntry> Leaves(BootdataFile file, string? prefix = null) =>
        file.AllSymbols.Where(s => !s.IsContainer &&
            (string.IsNullOrEmpty(prefix) || s.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    public List<WriteItem> Compare(IEnumerable<SymbolEntry> symbols)
    {
        var items = new List<WriteItem>();
        foreach (var sym in symbols)
        {
            var item = new WriteItem { Symbol = sym, FileType = _file.FindType(sym.TypeGuid)?.Name ?? "" };
            try
            {
                if (!TryGetSymbolInfo(sym.FullName, out item.PlcSize, out item.PlcType))
                {
                    item.Status = WriteStatus.NotFound;
                    item.Message = "symbol not found in PLC";
                }
                else if (item.PlcSize != sym.Data.Length)
                {
                    item.Status = WriteStatus.SizeMismatch;
                    item.Message = $"size in file {sym.Data.Length}, in PLC {item.PlcSize}";
                }
                else if (!SameType(item.FileType, item.PlcType))
                {
                    item.Status = WriteStatus.TypeMismatch;
                    item.Message = $"type in file {item.FileType}, in PLC {item.PlcType}";
                }
                else
                {
                    item.PlcData = ReadValue(sym.FullName, sym.Data.Length);
                    item.Status = item.PlcData.AsSpan().SequenceEqual(sym.Data) ? WriteStatus.Equal : WriteStatus.Differs;
                }
            }
            catch (Exception ex)
            {
                item.Status = WriteStatus.Failed;
                item.Message = ex.Message;
            }
            // Values from a damaged entry, or with impossible bytes (e.g. BOOL = 5), are never written;
            // the PLC value is still read above so it can be compared. Editing the value fixes the latter.
            string? damage = sym.Damage ?? BootdataCheck.ValueProblems(_decoder, sym).Select(p => "implausible value: " + p.Message).FirstOrDefault();
            if (damage != null && item.Status is not WriteStatus.NotFound)
            {
                item.Status = WriteStatus.Damaged;
                item.Message = damage + (sym.Damage == null ? " Fix it in the viewer first." : "");
            }
            items.Add(item);
        }
        return items;
    }

    // Writes the file's value of each item; items that failed the compare are never written.
    // Throws before writing anything if the PLC is not in Run, and stops if it leaves Run midway.
    public void Write(IEnumerable<WriteItem> items)
    {
        EnsureRunning();
        bool stopped = false;
        foreach (var item in items)
        {
            if (!item.CanWrite) continue;
            if (stopped)
            {
                item.Status = WriteStatus.Failed;
                item.Message = $"not written, PLC left Run ({PlcState})";
                continue;
            }
            try
            {
                uint handle = CreateHandle(item.Name);
                try { _client.Write(SymValueByHandle, handle, item.Symbol.Data); }
                finally { ReleaseHandle(handle); }
                item.PlcData = ReadValue(item.Name, item.Symbol.Data.Length);
                bool ok = item.PlcData.AsSpan().SequenceEqual(item.Symbol.Data);
                item.Status = ok ? WriteStatus.Written : WriteStatus.Failed;
                item.Message = ok ? "" : "read-back differs from written value";
            }
            catch (Exception ex)
            {
                item.Status = WriteStatus.Failed;
                item.Message = ex.Message;
            }
            // A failure may mean the PLC was stopped meanwhile; then the remaining items are skipped.
            if (item.Status == WriteStatus.Failed)
            {
                try { stopped = ReadState() != AdsState.Run; }
                catch { stopped = true; }
            }
        }
    }

    bool TryGetSymbolInfo(string name, out uint size, out string type)
    {
        size = 0;
        type = "";
        var read = new byte[0xFFFF];
        var err = _client.TryReadWrite(SymInfoByNameEx, 0, read, NameBytes(name), out int len);
        if (err == AdsErrorCode.DeviceSymbolNotFound) return false;
        if (err != AdsErrorCode.NoError) throw new AdsErrorException($"Reading symbol info for '{name}' failed", err);
        if (len < 30) return false;

        // AdsSymbolEntry: entryLength, iGroup, iOffs, size, dataType, flags, u16 nameLen, typeLen, commentLen
        size = BitConverter.ToUInt32(read, 12);
        int nameLen = BitConverter.ToUInt16(read, 24);
        int typeLen = BitConverter.ToUInt16(read, 26);
        type = Compat.Latin1.GetString(read, 30 + nameLen + 1, typeLen);
        return true;
    }

    byte[] ReadValue(string name, int size)
    {
        uint handle = CreateHandle(name);
        try
        {
            var result = _client.ReadAsResult(SymValueByHandle, handle, size);
            if (result.ErrorCode != AdsErrorCode.NoError)
                throw new AdsErrorException($"Reading '{name}' failed", result.ErrorCode);
            return result.Data.ToArray();
        }
        finally { ReleaseHandle(handle); }
    }

    uint CreateHandle(string name)
    {
        var buf = new byte[4];
        _client.ReadWrite(SymHandleByName, 0, buf, NameBytes(name));
        return BitConverter.ToUInt32(buf);
    }

    void ReleaseHandle(uint handle) =>
        _client.TryWrite(SymReleaseHandle, 0, BitConverter.GetBytes(handle));

    static byte[] NameBytes(string name) => Compat.Latin1.GetBytes(name + "\0");

    // The PLC may report a type with or without its library namespace (Tc2_System.ST_X vs ST_X).
    static bool SameType(string fileType, string plcType)
    {
        if (fileType.Length == 0) return true;
        static string Strip(string t) => t.Replace(" ", "").ToUpperInvariant();
        string a = Strip(fileType), b = Strip(plcType);
        return a == b || a.EndsWith("." + b) || b.EndsWith("." + a);
    }

    public void Dispose() => _client.Dispose();
}
