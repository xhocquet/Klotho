// Builder-shaped entry point for assembling a logger factory from sinks.
using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Logging
{
    public sealed partial class KLoggerFactory
    {
        public static IKLoggerFactory Create(Action<KLogBuilder> configure)
        {
            var b = new KLogBuilder();
            configure?.Invoke(b);
            return b.Build();
        }
    }

    public sealed class KLogBuilder
    {
        private KLogLevel _min = KLogLevel.Information;
        private readonly List<IKLogSink> _sinks = new List<IKLogSink>();

        public KLogBuilder SetMinimumLevel(KLogLevel level) { _min = level; return this; }

        public KLogBuilder AddConsole()
        {
            _sinks.Add(new ConsoleSink());
            return this;
        }

        public KLogBuilder AddRollingFile(Action<KRollingFileOptions> configure)
        {
            var o = new KRollingFileOptions();
            configure?.Invoke(o);
            _sinks.Add(new RollingFileSink(o.FilePrefix, o.RollingSizeKB, o.Directory));
            return this;
        }

        // Engine-specific sinks (e.g. the Unity debug sink) are added through extension methods
        // defined in their own assembly, which call AddSink.
        public KLogBuilder AddSink(IKLogSink sink) { if (sink != null) _sinks.Add(sink); return this; }

        internal IKLoggerFactory Build() => new KLoggerFactory(_min, _sinks);
    }

    public sealed class KRollingFileOptions
    {
        public string FilePrefix = "Client";
        public int RollingSizeKB = 1024 * 1024;
        public string Directory = "Logs";
    }
}
