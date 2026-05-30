using System;
using System.Collections.Generic;
using System.Globalization;

namespace xpTURN.Klotho.Logging
{
    /// <summary>
    /// Formatting buffer for the interpolated-string log handlers. A struct backed by a
    /// reusable [ThreadStatic] char[] so no per-call allocation occurs. Only acquired when the
    /// target level is enabled; a single string is produced once via ToStringAndReset.
    /// Re-entrant logging on the same thread (logging while formatting a log) overwrites the
    /// buffer, so log arguments must be side-effect free and must not log.
    /// </summary>
    internal struct CharBufferWriter
    {
        [ThreadStatic] private static char[] t_buf;
        private int _pos;

        public static CharBufferWriter Acquire(int hint)
        {
            if (t_buf == null || t_buf.Length < hint)
                t_buf = new char[Math.Max(256, hint)];
            return new CharBufferWriter { _pos = 0 };
        }

        private static void Grow(int curPos, int needed)
        {
            int cap = Math.Max(t_buf.Length * 2, curPos + needed);
            var bigger = new char[cap];
            Array.Copy(t_buf, bigger, curPos);
            t_buf = bigger;
        }

        public void Append(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (_pos + s.Length > t_buf.Length) Grow(_pos, s.Length);
            s.AsSpan().CopyTo(t_buf.AsSpan(_pos));
            _pos += s.Length;
        }

        public void Append(ReadOnlySpan<char> s)
        {
            if (s.Length == 0) return;
            if (_pos + s.Length > t_buf.Length) Grow(_pos, s.Length);
            s.CopyTo(t_buf.AsSpan(_pos));
            _pos += s.Length;
        }

        // Primitive overloads format directly via TryFormat (no boxing). Grow and retry on overflow.
        public void AppendF(int v)    { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(uint v)   { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(long v)   { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(ulong v)  { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(float v)  { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(double v) { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, default, CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }

        public void AppendF(int v, string format)    { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(long v, string format)   { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(ulong v, string format)  { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(float v, string format)  { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }
        public void AppendF(double v, string format) { int w; while (!v.TryFormat(t_buf.AsSpan(_pos), out w, format.AsSpan(), CultureInfo.InvariantCulture)) Grow(_pos, 32); _pos += w; }

        // Generic fallback. Enums resolve to a cached name (no allocation); other value types box once via ToString.
        public void AppendValue<T>(T value)
        {
            if (EnumNames<T>.IsEnum) { Append(EnumNames<T>.GetName(value)); return; }
            Append(value?.ToString());
        }

        public void AppendValuePadded<T>(T value, int alignment)
        {
            string s = EnumNames<T>.IsEnum ? EnumNames<T>.GetName(value) : value?.ToString();
            AppendPadded(s, alignment);
        }

        public void AppendPadded(string value, int alignment)
        {
            value ??= "";
            int pad = Math.Abs(alignment) - value.Length;
            if (alignment > 0 && pad > 0) AppendSpaces(pad);
            Append(value);
            if (alignment < 0 && pad > 0) AppendSpaces(pad);
        }

        private void AppendSpaces(int n)
        {
            if (_pos + n > t_buf.Length) Grow(_pos, n);
            for (int i = 0; i < n; i++) t_buf[_pos + i] = ' ';
            _pos += n;
        }

        public string ToStringAndReset()
        {
            string r = new string(t_buf, 0, _pos);
            _pos = 0;
            return r;
        }
    }

    /// <summary>
    /// Per-T cache of enum names. Defined enum values return a cached name string (no per-call
    /// allocation). Matching relies on EqualityComparer&lt;T&gt;.Default, which avoids boxing for enums.
    /// </summary>
    internal static class EnumNames<T>
    {
        public static readonly bool IsEnum = typeof(T).IsEnum;
        private static readonly T[] _values = IsEnum ? (T[])Enum.GetValues(typeof(T)) : Array.Empty<T>();
        private static readonly string[] _names = IsEnum ? Enum.GetNames(typeof(T)) : Array.Empty<string>();

        public static string GetName(T value)
        {
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < _values.Length; i++)
                if (cmp.Equals(value, _values[i])) return _names[i];
            return value.ToString(); // flags combinations / undefined values
        }
    }
}
