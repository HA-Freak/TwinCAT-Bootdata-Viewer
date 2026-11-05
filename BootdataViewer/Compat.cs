using System.Text;
using System.Text.RegularExpressions;

namespace BootdataViewer
{
    // Helpers for APIs that .NET Framework 4.8.1 lacks (newer .NET has them built in).
    static class Compat
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        // Shortest text that parses back to the same value. .NET Framework's "R" can give 17 digits
        // or, for some doubles, text that does not round-trip at all.
        public static string RoundTrip(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return d.ToString(Inv);
            for (int digits = 15; digits < 17; digits++)
            {
                string s = d.ToString("G" + digits, Inv);
                if (System.BitConverter.DoubleToInt64Bits(double.Parse(s, Inv)) == System.BitConverter.DoubleToInt64Bits(d)) return s;
            }
            return d.ToString("G17", Inv);
        }

        public static string RoundTrip(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return f.ToString(Inv);
            for (int digits = 6; digits < 9; digits++)
            {
                string s = f.ToString("G" + digits, Inv);
                if (float.Parse(s, Inv).Equals(f)) return s;
            }
            return f.ToString("G9", Inv);
        }

        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        public static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;
        public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        public static string ToHexString(byte[] d, int offset, int length) =>
            BitConverter.ToString(d, offset, length).Replace("-", "");

        public static byte[] FromHexString(string s)
        {
            if (s.Length % 2 != 0) throw new FormatException("Odd number of hex digits.");
            var bytes = new byte[s.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(s.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
            return bytes;
        }
    }
}

// The rest fills gaps in .NET Framework 4.8.1 (types and overloads the code uses from newer .NET).
namespace BootdataViewer
{
    // Shadows System.BitConverter inside this namespace, adding the span overloads of .NET Core.
    static class BitConverter
    {
        public static byte[] GetBytes(uint v) => System.BitConverter.GetBytes(v);
        public static string ToString(byte[] d, int offset, int length) => System.BitConverter.ToString(d, offset, length);

        public static short ToInt16(byte[] d, int p) => System.BitConverter.ToInt16(d, p);
        public static ushort ToUInt16(byte[] d, int p) => System.BitConverter.ToUInt16(d, p);
        public static int ToInt32(byte[] d, int p) => System.BitConverter.ToInt32(d, p);
        public static uint ToUInt32(byte[] d, int p) => System.BitConverter.ToUInt32(d, p);
        public static long ToInt64(byte[] d, int p) => System.BitConverter.ToInt64(d, p);
        public static ulong ToUInt64(byte[] d, int p) => System.BitConverter.ToUInt64(d, p);
        public static float ToSingle(byte[] d, int p) => System.BitConverter.ToSingle(d, p);
        public static double ToDouble(byte[] d, int p) => System.BitConverter.ToDouble(d, p);

        public static ushort ToUInt16(ReadOnlySpan<byte> d) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d);
        public static uint ToUInt32(ReadOnlySpan<byte> d) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d);
        public static ulong ToUInt64(ReadOnlySpan<byte> d) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(d);
        public static float ToSingle(ReadOnlySpan<byte> d) => System.BitConverter.ToSingle(d.Slice(0, 4).ToArray(), 0);
        public static double ToDouble(ReadOnlySpan<byte> d) => System.BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(d));

        public static bool TryWriteBytes(Span<byte> s, short v) => Write(s, System.BitConverter.GetBytes(v));
        public static bool TryWriteBytes(Span<byte> s, ushort v) => Write(s, System.BitConverter.GetBytes(v));
        public static bool TryWriteBytes(Span<byte> s, int v) => Write(s, System.BitConverter.GetBytes(v));
        public static bool TryWriteBytes(Span<byte> s, uint v) => Write(s, System.BitConverter.GetBytes(v));
        public static bool TryWriteBytes(Span<byte> s, long v) => Write(s, System.BitConverter.GetBytes(v));
        public static bool TryWriteBytes(Span<byte> s, ulong v) => Write(s, System.BitConverter.GetBytes(v));
        public static bool TryWriteBytes(Span<byte> s, float v) => Write(s, System.BitConverter.GetBytes(v));
        public static bool TryWriteBytes(Span<byte> s, double v) => Write(s, System.BitConverter.GetBytes(v));

        static bool Write(Span<byte> s, byte[] bytes)
        {
            if (s.Length < bytes.Length) return false;
            bytes.CopyTo(s);
            return true;
        }
    }

    static class CompatExtensions
    {
        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, TValue value) where TKey : notnull
        {
            if (d.ContainsKey(key)) return false;
            d.Add(key, value);
            return true;
        }

        public static void ReadExactly(this Stream s, byte[] buffer)
        {
            for (int read = 0; read < buffer.Length;)
            {
                int n = s.Read(buffer, read, buffer.Length - read);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
        }

        public static int IndexOfAnyExcept(this Span<byte> s, byte value)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] != value) return i;
            return -1;
        }

        public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
        public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;
        public static bool Contains(this string s, string value, StringComparison comparison) => s.IndexOf(value, comparison) >= 0;

        public static T FirstOrDefault<T>(this IEnumerable<T> source, Func<T, bool> predicate, T defaultValue)
        {
            foreach (var item in source)
                if (predicate(item)) return item;
            return defaultValue;
        }

        public static IEnumerable<TResult> Select<TResult>(this MatchCollection matches, Func<Match, TResult> selector) =>
            matches.Cast<Match>().Select(selector);
    }
}

namespace System.Runtime.CompilerServices
{
    // Needed by records and init-only properties.
    static class IsExternalInit { }
}

namespace System
{
    // Index/Range let the compiler translate ^1 and a..b.
    readonly struct Index
    {
        readonly int _value;
        public Index(int value, bool fromEnd = false) => _value = fromEnd ? ~value : value;
        public static Index Start => new(0);
        public static Index End => new(0, true);
        public static Index FromStart(int value) => new(value);
        public static Index FromEnd(int value) => new(value, true);
        public bool IsFromEnd => _value < 0;
        public int Value => _value < 0 ? ~_value : _value;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int value) => FromStart(value);
    }

    readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length), end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end) throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
    }
}
