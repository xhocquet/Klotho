using System;

using Microsoft.Extensions.Logging;

using ZLogger;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Logging.Mel;

namespace xpTURN.Klotho.Samples.LoggingMelConsole
{
    /// <summary>
    /// Demonstrates routing Klotho's IKLogger logging surface through a standard
    /// Microsoft.Extensions.Logging pipeline. The host owns the MEL ILoggerFactory;
    /// MelKLogger adapts a MEL ILogger into an IKLogger, after which the call site
    /// uses the regular KInformation/KWarning/KError/KDebug extension methods.
    /// </summary>
    internal static class Program
    {
        private static void Main()
        {
            // Host-owned MEL logging pipeline. Trace lets every level reach the bridge;
            // the actual gate is the MEL filter below.
            using var melFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Trace)
                    .AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;
                        options.TimestampFormat = "HH:mm:ss.fff ";
                    })
                    // Rolling file output under Logs/, one file per day, rolled at 1 MB.
                    .AddZLoggerRollingFile(
                        (timestamp, sequence) => $"Logs/sample-{timestamp.ToLocalTime():yyyy-MM-dd}_{sequence:000}.log",
                        rollSizeKB: 1024);
            });

            // Bridge: wrap a MEL ILogger so Klotho code logs through it.
            Microsoft.Extensions.Logging.ILogger melLogger = melFactory.CreateLogger("Klotho.Sample");
            IKLogger logger = new MelKLogger(melLogger);

            logger.KInformation($"MEL logging adapter ready. enabled(Debug)={logger.IsEnabled(KLogLevel.Debug)}");

            // Interpolated arguments are formatted by the Klotho handler before reaching MEL.
            int frame = 42;
            float dt = 0.016f;
            logger.KDebug($"frame {frame} stepped (dt={dt:0.000}s)");
            logger.KInformation($"player {frame} joined the session");
            logger.KWarning($"input buffer is {87}% full");

            try
            {
                throw new InvalidOperationException("simulated desync");
            }
            catch (Exception ex)
            {
                logger.KError(ex, $"rollback failed at frame {frame}");
            }

            logger.KInformation($"done");
        }
    }
}
