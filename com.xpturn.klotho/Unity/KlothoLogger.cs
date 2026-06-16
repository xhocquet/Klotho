using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Unity
{
    public static class KlothoLogger
    {
        // Default IKLogger with UnityDebug + RollingFile sinks.
        // Callers needing custom sinks should build their own factory.
        public static IKLogger CreateDefault(
            KLogLevel level = KLogLevel.Information,
            string filePrefix = "Client",
            string categoryName = "Client",
            int rollingSizeKB = 1024 * 1024)
        {
            var loggerFactory = KLoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(level);
                builder.AddUnityDebug();
                builder.AddRollingFile(options =>
                {
                    options.FilePrefix = filePrefix;
                    options.RollingSizeKB = rollingSizeKB;
                });
            });

            return loggerFactory.CreateLogger(categoryName);
        }
    }
}
