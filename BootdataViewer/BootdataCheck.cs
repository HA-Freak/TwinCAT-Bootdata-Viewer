using System.Text;

namespace BootdataViewer;

public enum IssueSeverity { Warning, Error }

public record IntegrityIssue(IssueSeverity Severity, long Offset, string Message)
{
    public override string ToString() =>
        $"{(Severity == IssueSeverity.Error ? "ERROR  " : "warning")} 0x{Offset:X6}  {Message}";
}

public sealed class IntegrityReport
{
    public List<IntegrityIssue> Issues { get; } = new();
    public bool IsCorrupt => Issues.Any(i => i.Severity == IssueSeverity.Error);
    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    public string Summary =>
        IsCorrupt ? $"File is corrupt: {Errors} error(s), {Warnings} warning(s)."
        : Warnings > 0 ? $"File structure is intact; {Warnings} value warning(s)."
        : "File is intact.";

    public string? Recovery;    // what could still be loaded from a corrupt file

    public override string ToString() =>
        Summary + (Recovery != null ? " " + Recovery : "") + (Issues.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, Issues) : "");

    internal void Error(long offset, string message) => Issues.Add(new(IssueSeverity.Error, offset, message));
    internal void Warn(long offset, string message) => Issues.Add(new(IssueSeverity.Warning, offset, message));
}

// Checks a bootdata file for corruption, typically a write cut off by a power failure
// (truncated or zero-filled file) or damaged bytes. The structure is walked directly on the
// raw bytes, so it also describes files that BootdataFile.Parse cannot load. Every length,
// count and terminator must agree exactly; the format leaves no slack between entries.
public static class BootdataCheck
{
    const int MaxIssues = 200;

    public static IntegrityReport Check(byte[] b)
    {
        var r = new IntegrityReport();
        if (b.Length == 0) { r.Error(0, "File is empty."); return r; }
        if (b.AsSpan().IndexOfAnyExcept((byte)0) < 0) { r.Error(0, $"File contains only zero bytes ({b.Length} bytes)."); return r; }
        if (b.Length < 12) { r.Error(0, $"File is too short for a header ({b.Length} bytes)."); return r; }
        if (Encoding.ASCII.GetString(b, 0, 4) != "TC3=") { r.Error(0, "Missing 'TC3=' header; not a TwinCAT 3 bootdata file."); return r; }

        uint typeCount = U32(b, 4);
        long typeEnd = 12L + U32(b, 8);
        if (typeEnd > b.Length)
        {
            r.Error(8, $"Datatype section ({U32(b, 8)} bytes) runs past the end of the file ({b.Length} bytes); the file is truncated.");
            return r;
        }

        var guids = new HashSet<Guid>();
        var sizes = new Dictionary<Guid, (uint Size, string Name)>();
        int pos = 12, types = 0;
        while (pos < typeEnd && r.Issues.Count < MaxIssues)
        {
            if (!CheckType(b, pos, (int)typeEnd, r, 0, out int len, out var guid, out uint size, out string name)) break;
            if (guid is Guid g) { guids.Add(g); sizes.TryAdd(g, (size, name)); }
            types++;
            pos += len;
        }
        if (types != typeCount)
            r.Error(4, $"Header announces {typeCount} datatype(s), the section holds {types}.");
        if (pos != typeEnd && !r.IsCorrupt)
            r.Error(pos, $"Datatype entries end at 0x{pos:X}, the section at 0x{typeEnd:X}.");

        int symStart = (int)typeEnd;
        if (symStart + 8 > b.Length)
        {
            r.Error(symStart, "Symbol section is missing; the file ends after the datatypes (truncated).");
            return r;
        }
        uint symCount = U32(b, symStart);
        long symEnd = symStart + 8L + U32(b, symStart + 4);
        if (symEnd > b.Length)
        {
            r.Error(symStart + 4, $"Symbol section ({U32(b, symStart + 4)} bytes) runs past the end of the file; the file is truncated by {symEnd - b.Length} byte(s).");
            symEnd = b.Length;
        }

        pos = symStart + 8;
        int symbols = 0, prevDepth = -1;
        bool prevContainer = false;
        while (symbols < symCount && pos < symEnd && r.Issues.Count < MaxIssues)
        {
            int start = pos;
            if (pos + 12 > symEnd) { r.Error(pos, $"Symbol #{symbols} header is cut off."); break; }
            uint entryLen = U32(b, pos);
            if (entryLen < 16 || start + entryLen > symEnd)
            {
                r.Error(pos, $"Symbol #{symbols} has an invalid length {entryLen} (section ends at 0x{symEnd:X}).");
                break;
            }
            int end = start + (int)entryLen;
            uint size = U32(b, pos + 4);
            ushort flags = U16(b, pos + 8);
            int nameLen = U16(b, pos + 10);
            if (12 + nameLen + 1 > entryLen || b[pos + 12 + nameLen] != 0)
            {
                r.Error(pos, $"Symbol #{symbols}: name length {nameLen} does not fit the entry or lacks its terminator.");
                break;
            }
            string name = Compat.Latin1.GetString(b, pos + 12, nameLen);
            if (!Printable(name)) r.Error(pos + 12, $"Symbol #{symbols}: name contains invalid characters ('{Escape(name)}').");

            bool container = (flags & 0x8000) != 0;
            int depth = flags & 0xFF;
            if ((flags & 0x3F00) != 0) r.Error(pos + 8, $"'{name}': unknown flag bits 0x{flags:X4}.");
            if (depth > 0 && (prevDepth < 0 || depth > prevDepth + (prevContainer ? 1 : 0)))
                r.Error(pos + 8, $"'{name}': nesting depth {depth} without a parent FB instance.");
            if (!container)
            {
                int dataPos = start + ((12 + nameLen + 1 + 3) & ~3);
                if (dataPos + size + 16 > end)
                    r.Error(pos + 4, $"'{name}': {size} data bytes plus type GUID do not fit the entry ({entryLen} bytes).");
                else
                {
                    var guid = new Guid(b.AsSpan(end - 16, 16).ToArray());
                    if (!guids.Contains(guid))
                        r.Error(end - 16, $"'{name}': type GUID {guid} matches no datatype in the file.");
                    else if (sizes[guid].Size != size)
                        r.Error(pos + 4, $"'{name}': {size} data bytes, but its type {sizes[guid].Name} has {sizes[guid].Size}.");
                }
            }
            prevDepth = depth;
            prevContainer = container;
            symbols++;
            pos = end;
        }
        if (symbols != symCount && r.Issues.Count < MaxIssues)
            r.Error(symStart, $"Header announces {symCount} symbol(s), the section holds {symbols}.");
        if (pos < symEnd && !r.IsCorrupt)
            r.Error(pos, $"{symEnd - pos} unused byte(s) at the end of the symbol section.");
        if (symEnd < b.Length)
        {
            bool zeros = b.AsSpan((int)symEnd).IndexOfAnyExcept((byte)0) < 0;
            r.Warn(symEnd, $"{b.Length - symEnd} byte(s) after the symbol section{(zeros ? " (all zero)" : "")}.");
        }
        return r;
    }

    // One datatype entry (and, recursively, its members) must fit exactly inside [start, limit).
    static bool CheckType(byte[] b, int start, int limit, IntegrityReport r, int depth,
                          out int len, out Guid? guid, out uint size, out string name)
    {
        len = 0; guid = null; size = 0; name = "";
        if (start + 42 > limit) { r.Error(start, "Datatype entry header is cut off."); return false; }
        len = (int)Math.Min(U32(b, start), int.MaxValue);
        if (len < 42 || start + (long)len > limit)
        {
            r.Error(start, $"Datatype entry has an invalid length {U32(b, start)} (space left {limit - start}).");
            return false;
        }
        int end = start + len;
        size = U32(b, start + 16);
        var flags = (DatatypeFlags)U32(b, start + 28);
        int nameLen = U16(b, start + 32), typeLen = U16(b, start + 34), commentLen = U16(b, start + 36);
        int arrayDim = U16(b, start + 38), subItems = U16(b, start + 40);
        int p = start + 42;

        string? Str(int l, string what, out bool ok)
        {
            ok = p + l + 1 <= end && b[p + l] == 0;
            if (!ok) { r.Error(p, $"Datatype at 0x{start:X}: {what} (length {l}) does not fit or lacks its terminator."); return null; }
            string s = Compat.Latin1.GetString(b, p, l);
            p += l + 1;
            return s;
        }

        name = Str(nameLen, "name", out bool ok1) ?? "";
        if (!ok1) return true;      // length is valid, so the walk can continue with the next entry
        if (nameLen == 0 || !Printable(name)) r.Error(start + 42, $"Datatype at 0x{start:X} has an invalid name '{Escape(name)}'.");
        string typeName = Str(typeLen, "type name", out bool ok2) ?? "";
        if (!ok2) return true;
        if (!Printable(typeName)) r.Error(start, $"Datatype '{name}' has an invalid type name '{Escape(typeName)}'.");
        Str(commentLen, "comment", out bool ok3);
        if (!ok3) return true;

        if (p + arrayDim * 8 > end) { r.Error(p, $"Datatype '{name}': {arrayDim} array dimension(s) do not fit."); return true; }
        p += arrayDim * 8;
        for (int i = 0; i < subItems; i++)
        {
            if (!CheckType(b, p, end, r, depth + 1, out int subLen, out _, out _, out _))
            {
                r.Error(p, $"Datatype '{name}': member {i + 1} of {subItems} is damaged.");
                return true;
            }
            p += subLen;
        }
        if (flags.HasFlag(DatatypeFlags.TypeGuid))
        {
            if (p + 16 > end) { r.Error(p, $"Datatype '{name}': type GUID does not fit."); return true; }
            guid = new Guid(b.AsSpan(p, 16).ToArray());
        }
        return true;
    }

    // Values whose bytes cannot come from a PLC: booleans other than 0/1, enum numbers that are
    // no member, strings without terminator, NaN/infinite reals. They point at damaged data.
    public static void CheckValues(BootdataFile file, IntegrityReport r)
    {
        var dec = new ValueDecoder(file);
        foreach (var sym in file.AllSymbols.Where(s => !s.IsContainer && !s.IsDamaged))
            foreach (var (offset, message) in ValueProblems(dec, sym))
            {
                if (r.Issues.Count >= MaxIssues) return;
                r.Warn(offset, message);
            }
    }

    // Implausible values inside one variable, with file offset and "path: problem".
    public static List<(long Offset, string Message)> ValueProblems(ValueDecoder dec, SymbolEntry sym)
    {
        var found = new List<(long, string)>();
        var raw = sym.Data;
        ValueNode root;
        try { root = dec.Decode(sym); }
        catch (Exception ex) { found.Add((sym.FileOffset, $"{sym.FullName}: cannot be decoded ({ex.Message}).")); return found; }
        Walk(root, sym.FullName);
        return found;

        void Walk(ValueNode n, string path)
        {
            foreach (var c in n.Children)
                Walk(c, c.Name.StartsWith('[') ? path + c.Name : path + "." + c.Name);
            long at = n.FileOffset - sym.FileOffset;
            if (n.Kind is not ScalarKind kind || n.Size <= 0 || at < 0 || at + n.Size > raw.Length) return;
            var d = raw.AsSpan((int)at, n.Size);
            long signed = n.Size is 1 or 2 or 4 or 8 ? BootdataFile.ReadSigned(raw, (int)at, n.Size) : 0;
            long unsigned = n.Size is 1 or 2 or 4 or 8 ? (long)ReadUnsigned(d) : 0;
            string? problem = kind switch
            {
                ScalarKind.Bool when d[0] > 1 => $"BOOL holds {d[0]} (only 0 or 1 are valid)",
                ScalarKind.Enum when n.EnumValues is { Count: > 0 } ev && !ev.Any(e => e.Value == signed || e.Value == unsigned)
                    => $"enum holds {signed}, which is no member",
                ScalarKind.String when d.IndexOf((byte)0) < 0 => "STRING has no terminating NUL",
                ScalarKind.WString when !HasWideNul(d) => "WSTRING has no terminating NUL",
                ScalarKind.Real when n.Size == 4 && !Compat.IsFinite(BitConverter.ToSingle(d)) => "REAL is NaN or infinite",
                ScalarKind.LReal when n.Size == 8 && !Compat.IsFinite(BitConverter.ToDouble(d)) => "LREAL is NaN or infinite",
                _ => null,
            };
            if (problem != null) found.Add((n.FileOffset, $"{path}: {problem}."));
        }
    }

    static ulong ReadUnsigned(ReadOnlySpan<byte> d) => d.Length switch
    {
        1 => d[0], 2 => BitConverter.ToUInt16(d), 4 => BitConverter.ToUInt32(d), _ => BitConverter.ToUInt64(d),
    };

    static bool HasWideNul(ReadOnlySpan<byte> d)
    {
        for (int i = 0; i + 1 < d.Length; i += 2)
            if (d[i] == 0 && d[i + 1] == 0) return true;
        return false;
    }

    public static IntegrityReport CheckFile(string path)
    {
        byte[] raw;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            raw = new byte[fs.Length];
            fs.ReadExactly(raw);
        }
        var r = Check(raw);
        try
        {
            var file = BootdataFile.Parse(raw);
            CheckValues(file, r);
            if (r.IsCorrupt) r.Recovery = Recovery(file);
        }
        catch (Exception ex) { r.Error(0, "Parsing failed: " + ex.Message); }
        return r;
    }

    // "812 of 974 entries recovered intact, 3 damaged" for a corrupt file that still loads.
    public static string Recovery(BootdataFile file)
    {
        var leaves = file.AllSymbols.Where(s => !s.IsContainer).ToList();
        int damaged = leaves.Count(s => s.IsDamaged);
        return $"{leaves.Count - damaged} variable(s) recovered intact, {damaged} damaged"
               + (file.Warnings.Any(w => w.StartsWith("Header announces") && w.Contains("symbols")) ? ", some lost." : ".");
    }

    static bool Printable(string s) => s.All(c => c >= 0x20 && c != 0x7F);

    static string Escape(string s) =>
        new(s.Take(60).Select(c => c >= 0x20 && c < 0x7F ? c : '?').ToArray());

    static uint U32(byte[] b, int p) => BitConverter.ToUInt32(b, p);
    static ushort U16(byte[] b, int p) => BitConverter.ToUInt16(b, p);
}
