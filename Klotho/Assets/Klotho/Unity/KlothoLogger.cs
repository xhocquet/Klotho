using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Unity;
using ZLogger.Providers;
using Utf8StringInterpolation;

namespace xpTURN.Klotho.Unity
{
    public static class KlothoLogger
    {
        // Default ILogger factory with UnityDebug + RollingFile sinks.
        // Callers needing custom providers should build their own LoggerFactory.
        public static ILogger CreateDefault(
            LogLevel level = LogLevel.Information,
            string filePrefix = "Client",
            string categoryName = "Client",
            int rollingSizeKB = 1024 * 1024)
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(level);
                builder.AddZLoggerUnityDebug();
                builder.AddZLoggerRollingFile(options =>
                {
                    options.FilePathSelector = (dt, index) =>
                        $"Logs/{filePrefix}_{dt:yyyy-MM-dd-HH-mm-ss-fff}_{index:000}.log";
                    options.RollingInterval = RollingInterval.Day;
                    options.RollingSizeKB = rollingSizeKB;
                    options.UsePlainTextFormatter(formatter =>
                    {
                        formatter.SetPrefixFormatter(
                            $"{0}|{1:short}|",
                            (in MessageTemplate template, in LogInfo info)
                                => template.Format(info.Timestamp, info.LogLevel));
                        formatter.SetExceptionFormatter(
                            (writer, ex) => Utf8String.Format(writer, $"{ex.Message}\n{ex.StackTrace}"));
                    });
                });
            });

            return loggerFactory.CreateLogger(categoryName);
        }
    }
}
