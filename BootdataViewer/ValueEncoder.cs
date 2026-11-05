using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BootdataViewer;

// Parses an edited value (in the same IEC 61131-3 notation the viewer displays)
// and writes it into the raw bytes of its variable.
public static class ValueEncoder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Writes text into data[offset..offset+size); throws FormatException with a readable message.
    public static void Write(ScalarKind kind, IReadOnlyList<(string Name, long Value)>? enumValues,
                             string text, byte[] data, int offset, int size, int? bit = null)
    {
        string s = text.Trim();
        var span = data.AsSpan(offset, size);
        switch (kind)
        {
            case ScalarKind.Bit:
                if (ParseBool(s)) data[offset] |= (byte)(1 << (bit ?? 0));
                else data[offset] &= (byte)~(1 << (bit ?? 0));
                return;
            case ScalarKind.Bool: span[0] = ParseBool(s) ? (byte)1 : (byte)0; return;
            case ScalarKind.SInt: span[0] = (byte)(sbyte)Integer(s, sbyte.MinValue, sbyte.MaxValue); return;
            case ScalarKind.USInt: span[0] = (byte)Integer(s, 0, byte.MaxValue); return;
            case ScalarKind.Int: BitConverter.TryWriteBytes(span, (short)Integer(s, short.MinValue, short.MaxValue)); return;
            case ScalarKind.UInt: BitConverter.TryWriteBytes(span, (ushort)Integer(s, 0, ushort.MaxValue)); return;
            case ScalarKind.DInt: BitConverter.TryWriteBytes(span, (int)Integer(s, int.MinValue, int.MaxValue)); return;
            case ScalarKind.UDInt: BitConverter.TryWriteBytes(span, (uint)Integer(s, 0, uint.MaxValue)); return;
            case ScalarKind.LInt: BitConverter.TryWriteBytes(span, (long)Integer(s, long.MinValue, long.MaxValue)); return;
            case ScalarKind.ULInt: BitConverter.TryWriteBytes(span, (ulong)Integer(s, 0, ulong.MaxValue)); return;
            case ScalarKind.Real:
                float f = (float)Real(s);
                if (float.IsInfinity(f)) throw new FormatException("Value is out of range for REAL.");
                BitConverter.TryWriteBytes(span, f);
                return;
            case ScalarKind.LReal: BitConverter.TryWriteBytes(span, Real(s)); return;
            case ScalarKind.String: WriteString(Unquote(s), Compat.Latin1, 1, span); return;
            case ScalarKind.WString: WriteString(Unquote(s), Encoding.Unicode, 2, span); return;
            case ScalarKind.Time: BitConverter.TryWriteBytes(span, (uint)Checked(Duration(s, "T", "TIME").Ticks / TimeSpan.TicksPerMillisecond, uint.MaxValue)); return;
            case ScalarKind.LTime: BitConverter.TryWriteBytes(span, (ulong)Duration(s, "LTIME", "LT").Ticks * 100); return;
            case ScalarKind.Tod: BitConverter.TryWriteBytes(span, (uint)TimeOfDay(s, "TOD", "TIME_OF_DAY").TotalMilliseconds); return;
            case ScalarKind.LTod: BitConverter.TryWriteBytes(span, (ulong)TimeOfDay(s, "LTOD", "LTIME_OF_DAY").Ticks * 100); return;
            case ScalarKind.Date: BitConverter.TryWriteBytes(span, (uint)Checked(UnixSeconds(Date(s, "D", "DATE")), uint.MaxValue)); return;
            case ScalarKind.Dt: BitConverter.TryWriteBytes(span, (uint)Checked(UnixSeconds(DateTimeValue(s, "DT", "DATE_AND_TIME")), uint.MaxValue)); return;
            case ScalarKind.LDate: BitConverter.TryWriteBytes(span, (ulong)(Date(s, "LDATE", "LD") - Compat.UnixEpoch).Ticks * 100); return;
            case ScalarKind.Ldt: BitConverter.TryWriteBytes(span, (ulong)(DateTimeValue(s, "LDT", "LDATE_AND_TIME") - Compat.UnixEpoch).Ticks * 100); return;
            case ScalarKind.Enum: WriteEnum(s, enumValues, span); return;
            case ScalarKind.Pointer:    // shown as a hex number, 0x0000...
                if (size >= 8) BitConverter.TryWriteBytes(span, (ulong)Integer(HexNumber(s), 0, ulong.MaxValue));
                else BitConverter.TryWriteBytes(span, (uint)Integer(HexNumber(s), 0, uint.MaxValue));
                return;
            default: WriteHex(s, span); return;
        }
    }

    static bool ParseBool(string s) => s.ToUpperInvariant() switch
    {
        "TRUE" or "1" => true,
        "FALSE" or "0" => false,
        _ => throw new FormatException("Enter TRUE or FALSE."),
    };

    // Accepts decimal and IEC radix literals (16#FF, 8#17, 2#1010), plus 0x prefixes and '_' separators.
    static decimal Integer(string s, decimal min, decimal max)
    {
        string t = s.Replace("_", "");
        decimal value;
        var m = Regex.Match(t, @"^([+-]?)(?:(2|8|16)#|0x)([0-9A-Fa-f]+)$");
        if (m.Success)
        {
            int radix = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : 16;
            value = Convert.ToUInt64(m.Groups[3].Value, radix);
            if (m.Groups[1].Value == "-") value = -value;
        }
        else if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign, Inv, out value))
            throw new FormatException($"'{s}' is not an integer.");
        if (value < min || value > max) throw new FormatException($"Value must be between {min} and {max}.");
        return value;
    }

    static string HexNumber(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Contains('#') ? s : "0x" + s;

    static double Real(string s)
    {
        if (!double.TryParse(s.Replace("_", ""), NumberStyles.Float, Inv, out double d) || double.IsNaN(d))
            throw new FormatException($"'{s}' is not a number (use '.' as decimal separator).");
        return d;
    }

    static long Checked(long value, long max) =>
        value < 0 || value > max ? throw new FormatException("Value is out of range for this type.") : value;

    static string StripPrefix(string s, params string[] prefixes)
    {
        foreach (var p in prefixes.OrderByDescending(p => p.Length))
            if (s.StartsWith(p + "#", StringComparison.OrdinalIgnoreCase)) return s[(p.Length + 1)..];
        return s;
    }

    // T#1d2h3m4s5ms6us7ns, or a plain number of milliseconds.
    static TimeSpan Duration(string s, params string[] prefixes)
    {
        string t = StripPrefix(s, prefixes).Replace("_", "");
        if (double.TryParse(t, NumberStyles.Float, Inv, out double ms)) return TimeSpan.FromMilliseconds(ms);
        var parts = Regex.Matches(t, @"(\d+(?:\.\d+)?)(ms|us|ns|d|h|m|s)", RegexOptions.IgnoreCase);
        if (parts.Count == 0 || string.Concat(parts.Select(p => p.Value)).Length != t.Length)
            throw new FormatException($"'{s}' is not a duration, e.g. T#1h30m or T#250ms.");
        double ticks = 0;
        foreach (Match p in parts)
        {
            double n = double.Parse(p.Groups[1].Value, Inv);
            ticks += p.Groups[2].Value.ToLowerInvariant() switch
            {
                "d" => n * TimeSpan.TicksPerDay,
                "h" => n * TimeSpan.TicksPerHour,
                "m" => n * TimeSpan.TicksPerMinute,
                "s" => n * TimeSpan.TicksPerSecond,
                "ms" => n * TimeSpan.TicksPerMillisecond,
                "us" => n * 10,
                _ => n / 100,
            };
        }
        return TimeSpan.FromTicks((long)ticks);
    }

    static TimeSpan TimeOfDay(string s, params string[] prefixes)
    {
        string t = StripPrefix(s, prefixes);
        if (!TimeSpan.TryParseExact(t, new[] { @"hh\:mm\:ss\.FFFFFFF", @"hh\:mm\:ss", @"hh\:mm" }, Inv, out var ts) || ts.TotalDays >= 1)
            throw new FormatException($"'{s}' is not a time of day, e.g. TOD#13:45:00.");
        return ts;
    }

    static DateTime Date(string s, params string[] prefixes)
    {
        string t = StripPrefix(s, prefixes);
        if (!DateTime.TryParseExact(t, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d))
            throw new FormatException($"'{s}' is not a date, e.g. D#2026-10-27.");
        return d;
    }

    static DateTime DateTimeValue(string s, params string[] prefixes)
    {
        string t = StripPrefix(s, prefixes);
        string[] formats = { "yyyy-MM-dd-HH:mm:ss.FFFFFFF", "yyyy-MM-dd-HH:mm:ss", "yyyy-MM-dd-HH:mm", "yyyy-MM-dd HH:mm:ss" };
        if (!DateTime.TryParseExact(t, formats, Inv, DateTimeStyles.None, out var d))
            throw new FormatException($"'{s}' is not a date and time, e.g. DT#2026-10-27-13:45:00.");
        return d;
    }

    static long UnixSeconds(DateTime d) => (long)(d - Compat.UnixEpoch).TotalSeconds;

    // Strings are shown as 'text' with $' for a quote; both quoted and bare input is accepted.
    static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') s = s[1..^1];
        return s.Replace("$'", "'");
    }

    static void WriteString(string value, Encoding enc, int charSize, Span<byte> span)
    {
        byte[] bytes = enc.GetBytes(value);
        if (enc == Compat.Latin1 && enc.GetString(bytes) != value)
            throw new FormatException("STRING only holds Latin-1 characters; use WSTRING for others.");
        int max = span.Length / charSize - 1;
        if (bytes.Length / charSize > max) throw new FormatException($"Text is too long; at most {max} characters fit.");
        span.Clear();   // also clears whatever followed the old terminator
        bytes.CopyTo(span);
    }

    static void WriteEnum(string s, IReadOnlyList<(string Name, long Value)>? values, Span<byte> span)
    {
        // Accepts "Name", "Type.Name", "Name (2)" and plain numbers.
        string t = Regex.Replace(s, @"\s*\(-?\d+\)$", "");
        if (t.Contains('.')) t = t[(t.LastIndexOf('.') + 1)..];
        long v;
        var match = values?.FirstOrDefault(e => e.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) ?? default;
        if (match.Name != null) v = match.Value;
        else if (long.TryParse(t, NumberStyles.AllowLeadingSign, Inv, out v)) { }
        else throw new FormatException($"'{s}' is not a value of this enum.");

        long min = span.Length >= 8 ? long.MinValue : -(1L << (span.Length * 8 - 1));
        long max = span.Length >= 8 ? long.MaxValue : (1L << (span.Length * 8)) - 1;
        if (v < min || v > max) throw new FormatException("Value does not fit the enum's base type.");
        Span<byte> buf = stackalloc byte[8];
        BitConverter.TryWriteBytes(buf, v);
        buf[..span.Length].CopyTo(span);
    }

    static void WriteHex(string s, Span<byte> span)
    {
        string t = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        t = t.Replace(" ", "").Replace("-", "");
        byte[] bytes;
        try { bytes = Compat.FromHexString(t); }
        catch (FormatException) { throw new FormatException("Enter the value as hex bytes."); }
        if (bytes.Length != span.Length) throw new FormatException($"Enter exactly {span.Length} byte(s) of hex.");
        bytes.CopyTo(span);
    }
}
