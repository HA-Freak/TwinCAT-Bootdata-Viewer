using System.Text;
using System.Text.RegularExpressions;
using TwinCAT.Ads;

namespace BootdataViewer;

public record FbInstance(string Path, string Type);

// Persistent data is written to the bootdata file when TwinCAT stops normally. A power failure
// skips that, so a PLC program normally saves it itself: FB_S_UPS (Tc2_SUPS) does so when the
// UPS takes over, FB_WritePersistentData (Tc2_Utilities) on demand. This looks for instances of
// either in the PLC's uploaded symbol and datatype tables, including ones nested inside other FBs.
public sealed class PersistentSaveCheck
{
    const uint SymUploadInfo2 = 0xF00F;
    const uint SymUpload = 0xF00B;
    const uint DataTypeUpload = 0xF00E;
    const int MaxInstances = 20;

    public List<FbInstance> Instances { get; } = new();
    public bool HasPersistentSave => Instances.Count > 0;

    public string Warning =>
        "No FB_S_UPS or FB_WritePersistentData instance was found in the PLC program. " +
        "Persistent data is then only saved when TwinCAT stops normally; after a power failure " +
        "the bootdata file may be missing or outdated.";

    public static bool IsSaveFb(string typeName)
    {
        if (ElementType(typeName) != null) return false;
        string t = BaseName(typeName);
        return t.StartsWith("FB_S_UPS", StringComparison.OrdinalIgnoreCase)
            || t.Equals("FB_WritePersistentData", StringComparison.OrdinalIgnoreCase);
    }

    readonly Dictionary<string, DataTypeEntry> _types = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> _contains = new(StringComparer.OrdinalIgnoreCase);

    public static PersistentSaveCheck Run(AdsClient client)
    {
        var check = new PersistentSaveCheck();
        var info = Read(client, SymUploadInfo2, 24);
        int symSize = BitConverter.ToInt32(info, 4);
        int typeSize = BitConverter.ToInt32(info, 12);

        var types = typeSize > 0 ? Read(client, DataTypeUpload, typeSize) : Array.Empty<byte>();
        var warnings = new List<string>();
        for (int p = 0; p + 4 <= types.Length;)
        {
            var entry = BootdataFile.ParseDataType(types, p, out int len, warnings);
            if (len <= 0) break;
            check._types.TryAdd(entry.Name, entry);
            check._types.TryAdd(BaseName(entry.Name), entry);
            p += len;
        }

        var syms = symSize > 0 ? Read(client, SymUpload, symSize) : Array.Empty<byte>();
        // AdsSymbolEntry: entryLen, iGroup, iOffs, size, dataType, flags, u16 nameLen, typeLen, commentLen, name\0 type\0
        for (int p = 0; p + 30 <= syms.Length;)
        {
            int len = BitConverter.ToInt32(syms, p);
            if (len < 30 || p + len > syms.Length) break;
            int nameLen = BitConverter.ToUInt16(syms, p + 24);
            int typeLen = BitConverter.ToUInt16(syms, p + 26);
            string name = Compat.Latin1.GetString(syms, p + 30, nameLen);
            string type = Compat.Latin1.GetString(syms, p + 30 + nameLen + 1, typeLen);
            check.Collect(name, type, 0);
            p += len;
        }
        return check;
    }

    static byte[] Read(AdsClient client, uint indexGroup, int length)
    {
        var result = client.ReadAsResult(indexGroup, 0, length);
        if (result.ErrorCode != AdsErrorCode.NoError)
            throw new AdsErrorException($"Uploading the symbol table (0x{indexGroup:X}) failed", result.ErrorCode);
        return result.Data.ToArray();
    }

    // Walks only into types that contain a save FB somewhere, so large programs stay cheap.
    void Collect(string path, string typeName, int depth)
    {
        if (Instances.Count >= MaxInstances || depth > 30) return;
        if (IsSaveFb(typeName))
        {
            Instances.Add(new FbInstance(path, BaseName(typeName)));
            return;
        }
        if (!Contains(typeName)) return;
        if (ElementType(typeName) is string element)
        {
            Collect(path + "[..]", element, depth + 1);
            return;
        }
        var t = Find(typeName)!;
        if (t.ArrayDims.Count > 0) { Collect(path + "[..]", t.TypeName, depth + 1); return; }
        if (t.SubItems.Count == 0) { Collect(path, t.TypeName, depth + 1); return; }   // alias
        foreach (var sub in t.SubItems)
            Collect(path + "." + sub.Name, sub.TypeName, depth + 1);
    }

    bool Contains(string typeName)
    {
        if (IsSaveFb(typeName)) return true;
        if (typeName.Length == 0 || IsReference(typeName)) return false;
        if (_contains.TryGetValue(typeName, out bool known)) return known;
        _contains[typeName] = false;   // guards against recursive types
        bool result;
        if (ElementType(typeName) is string element) result = Contains(element);
        else if (Find(typeName) is not DataTypeEntry t) result = false;
        else if (t.SubItems.Count == 0) result = t.TypeName != typeName && Contains(t.TypeName);
        else result = t.SubItems.Any(s => Contains(s.TypeName));
        return _contains[typeName] = result;
    }

    DataTypeEntry? Find(string typeName) =>
        _types.TryGetValue(typeName, out var t) || _types.TryGetValue(BaseName(typeName), out t) ? t : null;

    // "ARRAY [1..5] OF FB_X" -> "FB_X"
    static string? ElementType(string typeName)
    {
        var m = Regex.Match(typeName, @"^ARRAY\s*\[.*?\]\s*OF\s+(.+)$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    static bool IsReference(string typeName) =>
        typeName.StartsWith("POINTER TO", StringComparison.OrdinalIgnoreCase)
        || typeName.StartsWith("REFERENCE TO", StringComparison.OrdinalIgnoreCase);

    // Library types may come with their namespace (Tc2_SUPS.FB_S_UPS_CX51x0).
    static string BaseName(string typeName) =>
        typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
}
