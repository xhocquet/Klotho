// Portable sinks (Console / RollingFile). No engine dependency, so they can be reused outside Unity.
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace xpTURN.Klotho.Logging
{
    /// <summary>Console sink. Emits a "{ts}|{short}|" prefix.</summary>
    public sealed class ConsoleSink : IKLogSink
    {
        private readonly object _gate = new object();

        // Per-thread line buffer so the whole line is written in a single atomic call (no allocation,
        // no inter-line interleaving even with concurrent writers).
        [ThreadStatic] private static char[] t_line;

        public void Write(KLogLevel level, string message, Exception exception)
        {
            char[] buf = t_line ??= new char[256];
            int pos = 0;

            int tn;
            while (!DateTime.Now.TryFormat(buf.AsSpan(pos), out tn, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                buf = Grow(buf, pos + 32);
            pos += tn;

            pos = AppendChar(ref buf, pos, '|');
            pos = AppendStr(ref buf, pos, KLogLevelShort.Of(level));
            pos = AppendChar(ref buf, pos, '|');
            pos = AppendStr(ref buf, pos, message);
            if (exception != null)
            {
                pos = AppendChar(ref buf, pos, '\n');
                pos = AppendStr(ref buf, pos, exception.Message);
                pos = AppendChar(ref buf, pos, '\n');
                pos = AppendStr(ref buf, pos, exception.StackTrace);
            }
            pos = AppendStr(ref buf, pos, Environment.NewLine);
            t_line = buf;

            lock (_gate) Console.Out.Write(buf, 0, pos);
        }

        private static char[] Grow(char[] buf, int needed)
        {
            var bigger = new char[Math.Max(buf.Length * 2, needed)];
            Array.Copy(buf, bigger, buf.Length);
            return bigger;
        }

        private static int AppendChar(ref char[] buf, int pos, char c)
        {
            if (pos + 1 > buf.Length) buf = Grow(buf, pos + 1);
            buf[pos] = c;
            return pos + 1;
        }

        private static int AppendStr(ref char[] buf, int pos, string s)
        {
            if (string.IsNullOrEmpty(s)) return pos;
            if (pos + s.Length > buf.Length) buf = Grow(buf, pos + s.Length);
            s.AsSpan().CopyTo(buf.AsSpan(pos));
            return pos + s.Length;
        }

        public void Flush() { lock (_gate) Console.Out.Flush(); }
        public void Dispose() { Flush(); }
    }

    /// <summary>Rolling file sink (by day and by size). Emits a "{ts}|{short}|" prefix.</summary>
    public sealed class RollingFileSink : IKLogSink
    {
        private readonly object _gate = new object();
        private readonly string _dir;
        private readonly string _prefix;
        private readonly long _rollingSizeBytes;

        // Buffered writes are flushed on Error/Critical (so crash-context logs are durable) and
        // whenever this many unflushed chars accumulate. Bounds data loss on a process crash without
        // a per-line flush. Flush pushes to the OS file cache, which survives process death.
        private const long FlushThresholdChars = 8 * 1024;

        private StreamWriter _writer;
        private DateTime _curDate;
        private int _index;
        private long _written;
        private long _unflushed;

        public RollingFileSink(string filePrefix = "Client", int rollingSizeKB = 1024 * 1024, string dir = "Logs")
        {
            _prefix = filePrefix;
            _rollingSizeBytes = (long)rollingSizeKB * 1024;
            _dir = dir;
        }

        public void Write(KLogLevel level, string message, Exception exception)
        {
            Span<char> ts = stackalloc char[24];
            int n = 0;
            lock (_gate)
            {
                var now = DateTime.Now;
                EnsureWriter(now);
                now.TryFormat(ts, out n, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

                string shortLevel = KLogLevelShort.Of(level);
                _writer.Write(ts.Slice(0, n)); _writer.Write('|'); _writer.Write(shortLevel); _writer.Write('|'); _writer.Write(message);
                long len = n + 2 + shortLevel.Length + (message?.Length ?? 0);
                if (exception != null)
                {
                    _writer.Write('\n'); _writer.Write(exception.Message); _writer.Write('\n'); _writer.Write(exception.StackTrace);
                    len += 2 + (exception.Message?.Length ?? 0) + (exception.StackTrace?.Length ?? 0);
                }
                _writer.Write(Environment.NewLine);
                long lineLen = len + Environment.NewLine.Length;
                _written += lineLen;
                _unflushed += lineLen;

                if (_written >= _rollingSizeBytes)
                {
                    RollNext(now); // CloseWriter flushes the old file; OpenWriter resets counters
                }
                else if (level >= KLogLevel.Error || _unflushed >= FlushThresholdChars)
                {
                    try { _writer.Flush(); } catch { }
                    _unflushed = 0;
                }
            }
        }

        private void EnsureWriter(DateTime now)
        {
            if (_writer != null && now.Date == _curDate) return;
            CloseWriter();
            _curDate = now.Date;
            _index = 0;
            OpenWriter(now);
        }

        private void RollNext(DateTime now)
        {
            CloseWriter();
            _index++;
            OpenWriter(now);
        }

        private void OpenWriter(DateTime now)
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, $"{_prefix}_{now:yyyy-MM-dd-HH-mm-ss-fff}_{_index:000}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = false };
            _written = 0;
            _unflushed = 0;
        }

        private void CloseWriter()
        {
            if (_writer == null) return;
            try { _writer.Flush(); _writer.Dispose(); } catch { }
            _writer = null;
        }

        public void Flush() { lock (_gate) { try { _writer?.Flush(); } catch { } } }
        public void Dispose() { lock (_gate) CloseWriter(); }
    }
}
