using System.Text;

namespace BootdataViewer;

// Layout of a TwinCAT 3 (4026) Port_xxx.bootdata file, as reverse-engineered:
//
//   "TC3="                      magic
//   u32 typeCount
//   u32 typeSectionSize         bytes following, up to the symbol section
//   AdsDatatypeEntry[typeCount] standard ADS datatype entries (self-sized)
//   u32 symbolCount
//   u32 symbolSectionSize
//   SymbolEntry[symbolCount]:
//       u32 entryLength, u32 size, u16 flags, u16 nameLength, name\0,
//       padding to a 4-byte boundary, data[size], padding,
//       GUID of the datatype (last 16 bytes of the entry; values only)
//
// The symbol table is a flattened tree. The low byte of flags is the nesting
// depth; 0x8000 marks a container without data (a function block instance),
// 0x4000 an entry with a value. Nested names start with '.', e.g.
//   MAIN.fbConveyor (0x8000) -> .fbDrive (0x8001) -> .bEnable (0x4002)
//
// All integers are little-endian.

[Flags]
public enum DatatypeFlags : uint
{
    DataType = 0x1,
    DataItem = 0x2,
    ReferenceTo = 0x4,
    MethodDeref = 0x8,
    Oversample = 0x10,
    BitValues = 0x20,
    PropItem = 0x40,
    TypeGuid = 0x80,
    Persistent = 0x100,
    CopyMask = 0x200,
    TcComInterfacePtr = 0x400,
    MethodInfos = 0x800,
    Attributes = 0x1000,
    EnumInfos = 0x2000,
    Aligned = 0x10000,
    Static = 0x20000,
    SpLevels = 0x40000,
    IgnorePersist = 0x80000,
    AnySizeArray = 0x100000,
    PersistantDatatype = 0x200000,
    InitOnReset = 0x400000,
}

// ADS base type ids (ADST_xxx).
public enum AdsType : uint
{
    Void = 0, Int16 = 2, Int32 = 3, Real32 = 4, Real64 = 5,
    Int8 = 16, UInt8 = 17, UInt16 = 18, UInt32 = 19, Int64 = 20, UInt64 = 21,
    String = 30, WString = 31, Real80 = 32, Bit = 33, BigType = 65, MaxTypes = 67,
}

public record ArrayDim(int LowerBound, int Elements);

public sealed class DataTypeEntry
{
    public uint Version;
    public uint Size;
    public uint Offset;
    public AdsType AdsType;
    public DatatypeFlags Flags;
    public string Name = "";
    public string TypeName = "";
    public string Comment = "";
    public List<ArrayDim> ArrayDims = new();
    public List<DataTypeEntry> SubItems = new();
    public Guid? TypeGuid;
    public Dictionary<string, string> Attributes = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, long Value)> EnumValues = new();

    public override string ToString() => string.IsNullOrEmpty(TypeName) ? Name : $"{Name} : {TypeName}";
}

public sealed class SymbolEntry
{
    public string Name = "";        // as stored, nested names start with '.'
    public string FullName = "";    // e.g. MAIN.fbConveyor.fbDrive.bEnable
    public uint Size;
    public ushort Flags;
    public byte[] Data = Array.Empty<byte>();
    public Guid? TypeGuid;
    public long FileOffset;     // file offset of Data[0]
    public List<SymbolEntry> Children = new();
    // Set when the entry cannot be trusted (truncated data, unknown type, uncertain parent);
    // such entries are shown but never written to a PLC.
    public string? Damage;

    public int Depth => Flags & 0xFF;
    public bool IsContainer => (Flags & 0x8000) != 0;
    public bool IsDamaged => Damage != null;
}

public sealed class BootdataFile
{
    public string Path = "";
    public List<DataTypeEntry> DataTypes = new();
    public List<SymbolEntry> Symbols = new();      // top-level entries; nested ones are in Children
    public List<SymbolEntry> AllSymbols = new();   // every entry in file order
    public List<string> Warnings = new();

    readonly Dictionary<Guid, DataTypeEntry> _byGuid = new();
    readonly Dictionary<string, DataTypeEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public static BootdataFile Load(string path)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        var file = Parse(bytes);
        file.Path = path;
        return file;
    }

    // Values are edited in place (SymbolEntry.Data); everything else is written back unchanged.
    public byte[] ToBytes()
    {
        var bytes = (byte[])_raw.Clone();
        foreach (var sym in AllSymbols.Where(s => !s.IsContainer))
            sym.Data.CopyTo(bytes, sym.FileOffset);
        return bytes;
    }

    public void Save(string path)
    {
        var bytes = ToBytes();
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        _raw = bytes;
        Path = path;
    }

    public bool IsModified => !ToBytes().AsSpan().SequenceEqual(_raw);

    byte[] _raw = Array.Empty<byte>();

    // Damaged files are loaded as far as possible: datatypes up to the first broken entry, and
    // every symbol entry that is intact. After a broken symbol entry the parser searches forward
    // for the next entry that is plausible, so data behind a damaged spot is not lost.
    public static BootdataFile Parse(byte[] b)
    {
        if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "TC3=")
            throw new InvalidDataException("Not a TwinCAT 3 bootdata file (missing 'TC3=' header).");

        var file = new BootdataFile { _raw = (byte[])b.Clone() };
        uint typeCount = U32(b, 4);
        long typeEnd = 12L + U32(b, 8);
        if (typeEnd > b.Length)
        {
            file.Warnings.Add($"Datatype section runs past the end of the file (truncated); no variables can be recovered.");
            typeEnd = b.Length;
        }

        int pos = 12;
        while (pos + 42 <= typeEnd)
        {
            uint len = U32(b, pos);
            if (len < 42 || pos + len > typeEnd)
            {
                file.Warnings.Add($"Datatype entry at 0x{pos:X} has an invalid length {len}; the following datatypes are lost.");
                break;
            }
            file.DataTypes.Add(ParseDataType(b, pos, out _, file.Warnings));
            pos += (int)len;
        }
        if (file.DataTypes.Count != typeCount)
            file.Warnings.Add($"Header announces {typeCount} datatypes, found {file.DataTypes.Count}.");
        foreach (var t in file.DataTypes)
        {
            if (t.TypeGuid is Guid g) file._byGuid.TryAdd(g, t);
            file._byName.TryAdd(t.Name, t);
        }

        pos = (int)typeEnd;
        if (pos + 8 <= b.Length)
        {
            uint symCount = U32(b, pos);
            int symEnd = (int)Math.Min(b.Length, pos + 8L + U32(b, pos + 4));
            pos += 8;
            bool afterGap = false;
            while (file.AllSymbols.Count < symCount && pos + 16 <= symEnd)
            {
                // The last entry of a truncated file is kept as far as it goes.
                var sym = file.ReadSymbol(b, pos, symEnd, out int len, cutOff: symEnd == b.Length);
                if (sym == null)
                {
                    int next = file.Resync(b, pos + 1, symEnd);
                    if (next < 0)
                    {
                        file.Warnings.Add($"Damaged symbol entry at 0x{pos:X}; nothing usable follows.");
                        break;
                    }
                    file.Warnings.Add($"Damaged symbol data at 0x{pos:X}..0x{next - 1:X} ({next - pos} bytes) skipped.");
                    afterGap = true;
                    pos = next;
                    continue;
                }
                // Behind a gap, nested entries may belong to a parent that was lost; their names are not certain.
                if (sym.Depth == 0) afterGap = false;
                else if (afterGap) sym.Damage = "follows a damaged region, its parent FB instance is uncertain";
                file.AllSymbols.Add(sym);
                pos += len;
            }
            if (file.AllSymbols.Count != symCount)
                file.Warnings.Add($"Header announces {symCount} symbols, found {file.AllSymbols.Count}.");
            file.BuildTree();
        }
        else file.Warnings.Add("Symbol section is missing (file truncated).");
        return file;
    }

    // Reads one symbol entry; null when its header does not fit. A value entry that fits but
    // cannot be trusted (cut-off data, unknown type, size mismatch) is returned with Damage set.
    // With cutOff, limit is the end of a truncated file and an entry running past it is read
    // as far as it goes (entryLen then covers only the bytes that exist).
    SymbolEntry? ReadSymbol(byte[] b, int start, int limit, out int entryLen, bool cutOff = false)
    {
        entryLen = (int)Math.Min(U32(b, start), int.MaxValue);
        int nameLen = U16(b, start + 10);
        ushort flags = U16(b, start + 8);
        int fullLen = entryLen;
        bool truncated = cutOff && start + (long)entryLen > limit && entryLen <= 0x1000_0000;
        if (truncated) entryLen = limit - start;
        if (fullLen < 16 || start + (long)entryLen > limit || 12 + nameLen + 1 > entryLen
            || b[start + 12 + nameLen] != 0 || (flags & 0x3F00) != 0)
            return null;
        var sym = new SymbolEntry
        {
            Size = U32(b, start + 4),
            Flags = flags,
            Name = Compat.Latin1.GetString(b, start + 12, nameLen),
        };
        if (nameLen == 0 || sym.Name.Any(c => c < 0x20 || c == 0x7F)) return null;
        if (sym.IsContainer) return sym;

        int end = start + entryLen;
        int dataPos = start + ((12 + nameLen + 1 + 3) & ~3);
        sym.FileOffset = dataPos;
        sym.Data = b.AsSpan(dataPos, (int)Compat.Clamp(end - dataPos, 0, sym.Size)).ToArray();
        if (truncated)
        {
            if (sym.Data.Length < sym.Size)
            {
                sym.Damage = $"the file ends inside its data ({sym.Data.Length} of {sym.Size} bytes present)";
                return sym;
            }
            // Data complete, type GUID cut off: the GUID bytes that remain plus the size usually
            // still single out the datatype.
            int guidPos = start + fullLen - 16;
            var partial = guidPos < limit ? b.AsSpan(guidPos, limit - guidPos).ToArray() : Array.Empty<byte>();
            var matches = partial.Length < 2 ? new List<DataTypeEntry>() : DataTypes.Where(t => t.TypeGuid is Guid g && t.Size == sym.Size
                              && g.ToByteArray().AsSpan(0, partial.Length).SequenceEqual(partial)).ToList();
            if (matches.Count == 1)
            {
                sym.TypeGuid = matches[0].TypeGuid;
                Warnings.Add($"'{sym.Name}': the file ends inside its type GUID; type {matches[0].Name} identified from the " +
                             $"{partial.Length} remaining GUID byte(s) and the size. The data is complete.");
            }
            else sym.Damage = "the file ends inside its type information; the datatype cannot be identified";
            return sym;
        }
        if (dataPos + (long)sym.Size + 16 > end)
        {
            sym.Damage = "data or type information is cut off";
            return sym;
        }
        sym.TypeGuid = new Guid(b.AsSpan(end - 16, 16).ToArray());
        var type = FindType(sym.TypeGuid);
        if (type == null) sym.Damage = "its datatype is unknown (damaged type GUID or lost datatype)";
        else if (type.Size != sym.Size) sym.Damage = $"size {sym.Size} does not match its type {type.Name} ({type.Size})";
        return sym;
    }

    // Next offset at which an intact entry starts that is followed by another intact entry
    // (or the section end), so random bytes are not mistaken for an entry.
    int Resync(byte[] b, int from, int limit)
    {
        for (int p = from; p + 16 <= limit; p++)
        {
            if (ReadSymbol(b, p, limit, out int len) is not { IsDamaged: false }) continue;
            int next = p + len;
            if (next == limit || next + 16 <= limit && ReadSymbol(b, next, limit, out _) is { IsDamaged: false }) return p;
        }
        return -1;
    }

    void BuildTree()
    {
        var stack = new List<SymbolEntry>();    // stack[d] = innermost container at depth d
        foreach (var sym in AllSymbols)
        {
            int depth = Math.Min(sym.Depth, stack.Count);
            if (depth != sym.Depth)
            {
                Warnings.Add($"'{sym.Name}' has depth {sym.Depth} without a parent at depth {sym.Depth - 1}.");
                sym.Damage ??= "its parent FB instance is missing";
            }
            stack.RemoveRange(depth, stack.Count - depth);
            if (depth > 0 && stack[depth - 1].Damage != null) sym.Damage ??= "its parent FB instance is damaged";
            if (depth == 0)
            {
                sym.FullName = sym.Name;
                Symbols.Add(sym);
            }
            else
            {
                var parent = stack[depth - 1];
                // Members are stored as ".x", array-of-FB elements as "[1]" (-> MAIN.aAxes[1].nCycles).
                sym.FullName = parent.FullName + (sym.Name.StartsWith('.') || sym.Name.StartsWith('[') ? sym.Name : "." + sym.Name);
                parent.Children.Add(sym);
            }
            if (sym.IsContainer) stack.Add(sym);
        }
    }

    public DataTypeEntry? FindType(Guid? guid) =>
        guid is Guid g && _byGuid.TryGetValue(g, out var t) ? t : null;

    public DataTypeEntry? FindType(string name) =>
        _byName.TryGetValue(name.Trim(), out var t) ? t : null;

    internal static DataTypeEntry ParseDataType(byte[] b, int start, out int entryLen, List<string> warnings)
    {
        entryLen = (int)U32(b, start);
        var e = new DataTypeEntry
        {
            Version = U32(b, start + 4),
            Size = U32(b, start + 16),
            Offset = U32(b, start + 20),
            AdsType = (AdsType)U32(b, start + 24),
            Flags = (DatatypeFlags)U32(b, start + 28),
        };
        int nameLen = U16(b, start + 32);
        int typeLen = U16(b, start + 34);
        int commentLen = U16(b, start + 36);
        int arrayDim = U16(b, start + 38);
        int subItems = U16(b, start + 40);
        int end = start + entryLen;
        int p = start + 42;

        try
        {
            e.Name = Str(b, ref p, nameLen);
            e.TypeName = Str(b, ref p, typeLen);
            e.Comment = Str(b, ref p, commentLen);
            for (int i = 0; i < arrayDim; i++, p += 8)
                e.ArrayDims.Add(new ArrayDim((int)U32(b, p), (int)U32(b, p + 4)));
            for (int i = 0; i < subItems; i++)
            {
                var sub = ParseDataType(b, p, out int subLen, warnings);
                if (subLen <= 0) break;
                e.SubItems.Add(sub);
                p += subLen;
            }
            if (e.Flags.HasFlag(DatatypeFlags.TypeGuid) && p + 16 <= end)
            {
                e.TypeGuid = new Guid(b.AsSpan(p, 16).ToArray());
                p += 16;
            }
            if (e.Flags.HasFlag(DatatypeFlags.CopyMask))
                p += (int)e.Size;
            if (e.Flags.HasFlag(DatatypeFlags.MethodInfos) && p + 2 <= end)
            {
                int count = U16(b, p);
                p += 2;
                for (int i = 0; i < count && p + 4 <= end; i++)
                    p += (int)U32(b, p);    // method entries are self-sized; skip them
            }
            if (e.Flags.HasFlag(DatatypeFlags.Attributes) && p + 2 <= end)
            {
                int count = U16(b, p);
                p += 2;
                for (int i = 0; i < count && p + 2 <= end; i++)
                {
                    int nl = b[p], vl = b[p + 1];
                    p += 2;
                    string n = Str(b, ref p, nl);
                    string v = Str(b, ref p, vl);
                    e.Attributes[n] = v;
                }
            }
            if (e.Flags.HasFlag(DatatypeFlags.EnumInfos) && p + 2 <= end)
            {
                int count = U16(b, p);
                p += 2;
                int valueSize = (int)Compat.Clamp(e.Size, 1, 8);
                for (int i = 0; i < count && p < end; i++)
                {
                    int nl = b[p++];
                    string n = Str(b, ref p, nl);
                    e.EnumValues.Add((n, ReadSigned(b, p, valueSize)));
                    p += valueSize;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            warnings.Add($"Datatype '{e.Name}' at 0x{start:X} is truncated or malformed.");
        }
        return e;
    }

    static string Str(byte[] b, ref int p, int len)
    {
        string s = Compat.Latin1.GetString(b, p, len);
        p += len + 1;   // skip terminating NUL
        return s;
    }

    internal static long ReadSigned(byte[] b, int p, int size) => size switch
    {
        1 => (sbyte)b[p],
        2 => BitConverter.ToInt16(b, p),
        4 => BitConverter.ToInt32(b, p),
        _ => BitConverter.ToInt64(b, p),
    };

    static uint U32(byte[] b, int p) => BitConverter.ToUInt32(b, p);
    static ushort U16(byte[] b, int p) => BitConverter.ToUInt16(b, p);
}
