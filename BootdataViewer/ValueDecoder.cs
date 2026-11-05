using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BootdataViewer;

// How a scalar value is stored; drives both formatting and parsing edited values.
public enum ScalarKind
{
    Raw, Bool, Bit, SInt, USInt, Int, UInt, DInt, UDInt, LInt, ULInt, Real, LReal,
    String, WString, Pointer, Time, LTime, Tod, LTod, Date, Dt, LDate, Ldt, Enum,
}

public sealed class ValueNode
{
    public ScalarKind? Kind;    // set on scalar leaves
    public IReadOnlyList<(string Name, long Value)>? EnumValues;
    public string Name = "";
    public string Type = "";
    public string Value = "";
    public int Offset;          // offset inside the symbol's data
    public int Size;
    public int? BitOffset;      // set for packed BIT members
    public string? SymbolPath;  // full PLC name, set on nodes that are bootdata entries
    public string? Damage;      // set on bootdata entries that cannot be trusted (see SymbolEntry.Damage)
    public long FileOffset;
    public List<ValueNode> Children = new();

    public JsonNode ToJson()
    {
        if (Children.Count == 0) return JsonValue.Create(Value)!;
        var obj = new JsonObject();
        foreach (var c in Children) obj[c.Name] = c.ToJson();
        return obj;
    }
}

// Turns raw symbol bytes into a tree of named, formatted values using the
// datatype table embedded in the bootdata file.
public sealed class ValueDecoder
{
    readonly BootdataFile _file;

    public ValueDecoder(BootdataFile file) => _file = file;

    public ValueNode Decode(SymbolEntry sym)
    {
        string name = sym.Name.TrimStart('.');
        if (sym.IsContainer)
        {
            var group = new ValueNode { Name = name, Type = "FB instance", SymbolPath = sym.FullName, Damage = sym.Damage };
            foreach (var c in sym.Children) group.Children.Add(Decode(c));
            return group;
        }

        var type = _file.FindType(sym.TypeGuid);
        if (type == null)
        {
            return new ValueNode
            {
                Name = name, Type = "<unknown type>", Size = sym.Data.Length, FileOffset = sym.FileOffset,
                Value = Hex(sym.Data, 0, sym.Data.Length), SymbolPath = sym.FullName, Damage = sym.Damage,
            };
        }
        var node = Decode(name, type.Name, type, sym.Data, 0, (int)sym.Size, sym.FileOffset, 0);
        node.SymbolPath = sym.FullName;
        node.Damage = sym.Damage;
        return node;
    }

    ValueNode Decode(string name, string typeName, DataTypeEntry type, byte[] data, int offset, int size,
                     long fileBase, int depth)
    {
        var node = new ValueNode
        {
            Name = name, Type = typeName, Offset = offset, Size = size, FileOffset = fileBase + offset,
        };
        if (depth > 32 || offset < 0 || offset + size > data.Length)
        {
            node.Value = "<out of range>";
            return node;
        }

        var chain = ResolveChain(type);
        var arrayDef = chain.FirstOrDefault(t => t.ArrayDims.Count > 0);
        var structDef = chain.FirstOrDefault(t => t.SubItems.Count > 0);

        if (arrayDef != null && (structDef == null || chain.IndexOf(arrayDef) <= chain.IndexOf(structDef)))
        {
            DecodeArray(node, arrayDef, data, offset, size, fileBase, depth);
            return node;
        }
        if (structDef != null)
        {
            foreach (var sub in structDef.SubItems)
            {
                if (sub.Flags.HasFlag(DatatypeFlags.Static)) continue;
                if (IsBit(sub))
                {
                    node.Children.Add(DecodeBit(sub, data, offset, fileBase));
                    continue;
                }
                node.Children.Add(Decode(sub.Name, sub.TypeName, sub, data, offset + (int)sub.Offset,
                                         (int)sub.Size, fileBase, depth + 1));
            }
            return node;
        }
        node.Kind = Classify(chain, size, out var enumDef);
        node.EnumValues = enumDef?.EnumValues;
        node.Value = Format(node.Kind.Value, node.EnumValues, data, offset, size);
        return node;
    }

    // BIT members are packed: their offset and size are given in bits, not bytes.
    static bool IsBit(DataTypeEntry sub) =>
        sub.Flags.HasFlag(DatatypeFlags.BitValues) || sub.TypeName.Equals("BIT", StringComparison.OrdinalIgnoreCase);

    static ValueNode DecodeBit(DataTypeEntry sub, byte[] data, int structOffset, long fileBase)
    {
        int byteOffset = structOffset + (int)(sub.Offset / 8);
        int bit = (int)(sub.Offset % 8);
        var node = new ValueNode
        {
            Name = sub.Name, Type = sub.TypeName, Offset = byteOffset, Size = 1, FileOffset = fileBase + byteOffset,
            BitOffset = bit,
        };
        if (byteOffset < data.Length)
        {
            node.Kind = ScalarKind.Bit;
            node.Value = Format(ScalarKind.Bit, null, data, byteOffset, 1, bit);
        }
        else node.Value = "<out of range>";
        return node;
    }

    void DecodeArray(ValueNode node, DataTypeEntry arr, byte[] data, int offset, int size, long fileBase, int depth)
    {
        int total = arr.ArrayDims.Aggregate(1, (a, d) => a * Math.Max(d.Elements, 0));
        if (total == 0) { node.Value = "[]"; return; }
        int elemSize = size / total;

        // A datatype entry for "ARRAY [..] OF X" carries X as its type name;
        // a struct member declared inline carries the full "ARRAY [..] OF X" string.
        string elemName = arr.TypeName;
        int of = elemName.LastIndexOf(" OF ", StringComparison.OrdinalIgnoreCase);
        if (elemName.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase) && of >= 0)
            elemName = elemName[(of + 4)..].Trim();
        var elemType = _file.FindType(elemName) ?? Synthetic(elemName, arr.AdsType, elemSize);

        var idx = arr.ArrayDims.Select(d => d.LowerBound).ToArray();
        for (int i = 0; i < total; i++)
        {
            node.Children.Add(Decode($"[{string.Join(",", idx)}]", elemName, elemType, data,
                                     offset + i * elemSize, elemSize, fileBase, depth + 1));
            for (int d = idx.Length - 1; d >= 0; d--)
            {
                if (++idx[d] < arr.ArrayDims[d].LowerBound + arr.ArrayDims[d].Elements) break;
                idx[d] = arr.ArrayDims[d].LowerBound;
            }
        }
    }

    // Follows TypeName links (member -> alias -> base type) so we can find the
    // first entry that actually describes the layout, enum values etc.
    List<DataTypeEntry> ResolveChain(DataTypeEntry start)
    {
        var chain = new List<DataTypeEntry> { start };
        var cur = start;
        while (chain.Count < 16 && cur.SubItems.Count == 0 && cur.ArrayDims.Count == 0 && cur.EnumValues.Count == 0)
        {
            var next = (!string.IsNullOrEmpty(cur.TypeName) ? _file.FindType(cur.TypeName) : null)
                       ?? (cur.Flags.HasFlag(DatatypeFlags.DataItem) ? _file.FindType(cur.TypeGuid) : null);
            if (next == null || chain.Contains(next)) break;
            chain.Add(next);
            cur = next;
        }
        return chain;
    }

    static DataTypeEntry Synthetic(string name, AdsType ads, int size) =>
        new() { Name = name, TypeName = "", AdsType = ads, Size = (uint)size };

    // Decides how a scalar is stored. IEC names carry more meaning than the ADS base type
    // (BOOL is stored as BYTE, TIME as UDINT, ...). For a datatype entry Name is the type
    // itself (BOOL) and TypeName its base (BYTE); for a member entry Name is the member
    // and TypeName its type.
    static ScalarKind Classify(List<DataTypeEntry> chain, int size, out DataTypeEntry? enumDef)
    {
        enumDef = chain.FirstOrDefault(t => t.EnumValues.Count > 0);
        if (enumDef != null) return size is 1 or 2 or 4 or 8 ? ScalarKind.Enum : ScalarKind.Raw;

        foreach (var t in chain)
        {
            var names = t.Flags.HasFlag(DatatypeFlags.DataType) ? new[] { t.Name, t.TypeName } : new[] { t.TypeName };
            foreach (var n in names)
                if (IecKind(n) is ScalarKind k) return Fits(k, size) ? k : ScalarKind.Raw;
        }

        var ads = chain.Select(t => t.AdsType).FirstOrDefault(a => a != AdsType.Void && a != AdsType.BigType, AdsType.Void);
        var kind = ads switch
        {
            AdsType.Bit => ScalarKind.Bool,
            AdsType.Int8 => ScalarKind.SInt,
            AdsType.UInt8 => ScalarKind.USInt,
            AdsType.Int16 => ScalarKind.Int,
            AdsType.UInt16 => ScalarKind.UInt,
            AdsType.Int32 => ScalarKind.DInt,
            AdsType.UInt32 => ScalarKind.UDInt,
            AdsType.Int64 => ScalarKind.LInt,
            AdsType.UInt64 => ScalarKind.ULInt,
            AdsType.Real32 => ScalarKind.Real,
            AdsType.Real64 => ScalarKind.LReal,
            AdsType.String => ScalarKind.String,
            AdsType.WString => ScalarKind.WString,
            _ => ScalarKind.Raw,
        };
        return Fits(kind, size) ? kind : ScalarKind.Raw;
    }

    static ScalarKind? IecKind(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string n = name.ToUpperInvariant();
        if (n.StartsWith("STRING")) return ScalarKind.String;
        if (n.StartsWith("WSTRING")) return ScalarKind.WString;
        if (n.StartsWith("POINTER TO") || n.StartsWith("REFERENCE TO")) return ScalarKind.Pointer;
        return n switch
        {
            "BOOL" or "BIT" => ScalarKind.Bool,
            "SINT" => ScalarKind.SInt,
            "USINT" or "BYTE" => ScalarKind.USInt,
            "INT" => ScalarKind.Int,
            "UINT" or "WORD" => ScalarKind.UInt,
            "DINT" => ScalarKind.DInt,
            "UDINT" or "DWORD" => ScalarKind.UDInt,
            "LINT" => ScalarKind.LInt,
            "ULINT" or "LWORD" => ScalarKind.ULInt,
            "REAL" => ScalarKind.Real,
            "LREAL" => ScalarKind.LReal,
            "TIME" => ScalarKind.Time,
            "LTIME" => ScalarKind.LTime,
            "TIME_OF_DAY" or "TOD" => ScalarKind.Tod,
            "LTIME_OF_DAY" or "LTOD" => ScalarKind.LTod,
            "DATE" => ScalarKind.Date,
            "DATE_AND_TIME" or "DT" => ScalarKind.Dt,
            "LDATE" => ScalarKind.LDate,
            "LDATE_AND_TIME" or "LDT" => ScalarKind.Ldt,
            _ => null,
        };
    }

    // Size in bytes a kind needs; 0 for variable-length kinds.
    public static int SizeOf(ScalarKind k) => k switch
    {
        ScalarKind.Bool or ScalarKind.Bit or ScalarKind.SInt or ScalarKind.USInt => 1,
        ScalarKind.Int or ScalarKind.UInt => 2,
        ScalarKind.DInt or ScalarKind.UDInt or ScalarKind.Real or ScalarKind.Time or ScalarKind.Tod
            or ScalarKind.Date or ScalarKind.Dt => 4,
        ScalarKind.LInt or ScalarKind.ULInt or ScalarKind.LReal or ScalarKind.LTime or ScalarKind.LTod
            or ScalarKind.LDate or ScalarKind.Ldt => 8,
        _ => 0,
    };

    static bool Fits(ScalarKind k, int size) => size >= Math.Max(SizeOf(k), 1);

    public static string Format(ScalarKind kind, IReadOnlyList<(string Name, long Value)>? enumValues,
                                byte[] d, int o, int size, int? bit = null)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case ScalarKind.Bit: return ((d[o] >> (bit ?? 0)) & 1) != 0 ? "TRUE" : "FALSE";
            case ScalarKind.Bool: return d[o] != 0 ? "TRUE" : "FALSE";
            case ScalarKind.SInt: return ((sbyte)d[o]).ToString(inv);
            case ScalarKind.USInt: return d[o].ToString(inv);
            case ScalarKind.Int: return BitConverter.ToInt16(d, o).ToString(inv);
            case ScalarKind.UInt: return BitConverter.ToUInt16(d, o).ToString(inv);
            case ScalarKind.DInt: return BitConverter.ToInt32(d, o).ToString(inv);
            case ScalarKind.UDInt: return BitConverter.ToUInt32(d, o).ToString(inv);
            case ScalarKind.LInt: return BitConverter.ToInt64(d, o).ToString(inv);
            case ScalarKind.ULInt: return BitConverter.ToUInt64(d, o).ToString(inv);
            case ScalarKind.Real: return Compat.RoundTrip(BitConverter.ToSingle(d, o));
            case ScalarKind.LReal: return Compat.RoundTrip(BitConverter.ToDouble(d, o));
            case ScalarKind.String: return Quote(CString(d, o, size));
            case ScalarKind.WString: return Quote(WString(d, o, size));
            case ScalarKind.Pointer:
                return "0x" + (size >= 8 ? BitConverter.ToUInt64(d, o).ToString("X16") : BitConverter.ToUInt32(d, o).ToString("X8"));
            case ScalarKind.Time: return "T#" + Duration(TimeSpan.FromMilliseconds(BitConverter.ToUInt32(d, o)));
            case ScalarKind.LTime: return "LTIME#" + Duration(TimeSpan.FromTicks((long)(BitConverter.ToUInt64(d, o) / 100)));
            case ScalarKind.Tod: return "TOD#" + TimeSpan.FromMilliseconds(BitConverter.ToUInt32(d, o)).ToString(@"hh\:mm\:ss\.fff");
            case ScalarKind.LTod: return "LTOD#" + TimeSpan.FromTicks((long)(BitConverter.ToUInt64(d, o) / 100)).ToString(@"hh\:mm\:ss\.fffffff");
            case ScalarKind.Date: return "D#" + Compat.UnixEpoch.AddSeconds(BitConverter.ToUInt32(d, o)).ToString("yyyy-MM-dd");
            case ScalarKind.Dt: return "DT#" + Compat.UnixEpoch.AddSeconds(BitConverter.ToUInt32(d, o)).ToString("yyyy-MM-dd-HH:mm:ss");
            case ScalarKind.LDate: return "LDATE#" + Compat.UnixEpoch.AddTicks((long)(BitConverter.ToUInt64(d, o) / 100)).ToString("yyyy-MM-dd");
            case ScalarKind.Ldt: return "LDT#" + Compat.UnixEpoch.AddTicks((long)(BitConverter.ToUInt64(d, o) / 100)).ToString("yyyy-MM-dd-HH:mm:ss.fffffff");
            case ScalarKind.Enum:
                long v = BootdataFile.ReadSigned(d, o, size);
                var m = enumValues?.FirstOrDefault(e => e.Value == v) ?? default;
                return m.Name != null ? $"{m.Name} ({v})" : v.ToString(inv);
            default: return Hex(d, o, size);
        }
    }

    static string Duration(TimeSpan t)
    {
        if (t == TimeSpan.Zero) return "0ms";
        var sb = new StringBuilder();
        if (t.Days > 0) sb.Append(t.Days).Append('d');
        if (t.Hours > 0) sb.Append(t.Hours).Append('h');
        if (t.Minutes > 0) sb.Append(t.Minutes).Append('m');
        if (t.Seconds > 0) sb.Append(t.Seconds).Append('s');
        if (t.Milliseconds > 0) sb.Append(t.Milliseconds).Append("ms");
        long subMs = t.Ticks % TimeSpan.TicksPerMillisecond;
        if (subMs > 0) sb.Append(subMs * 100).Append("ns");
        return sb.ToString();
    }

    static string CString(byte[] d, int o, int size)
    {
        int n = Array.IndexOf(d, (byte)0, o, size);
        return Compat.Latin1.GetString(d, o, (n < 0 ? o + size : n) - o);
    }

    static string WString(byte[] d, int o, int size)
    {
        int len = 0;
        while (len + 1 < size && (d[o + len] != 0 || d[o + len + 1] != 0)) len += 2;
        return Encoding.Unicode.GetString(d, o, len);
    }

    static string Quote(string s) => "'" + s.Replace("'", "$'") + "'";

    public static string Hex(byte[] d, int o, int size) =>
        size == 0 ? "" : Compat.ToHexString(d, o, Math.Min(size, d.Length - o)).ToLowerInvariant();
}
