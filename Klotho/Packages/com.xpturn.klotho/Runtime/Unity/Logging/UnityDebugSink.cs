// Unity debug sink. Engine-dependent, so it lives in the engine-facing logging assembly.
#if UNITY_5_3_OR_NEWER
using System;
using UnityEngine;

namespace xpTURN.Klotho.Logging
{
    /// <summary>Maps levels to UnityEngine.Debug.Log/LogWarning/LogError. The Unity console adds its own timestamp, so no prefix is attached.</summary>
    public sealed class UnityDebugSink : IKLogSink
    {
        public void Write(KLogLevel level, string message, Exception exception)
        {
            string text = exception == null ? message : (message + "\n" + exception.Message + "\n" + exception.StackTrace);
            switch (level)
            {
                case KLogLevel.Trace:
                case KLogLevel.Debug:
                case KLogLevel.Information:
                    Debug.Log(text);
                    break;
                case KLogLevel.Warning:
                    Debug.LogWarning(text);
                    break;
                case KLogLevel.Error:
                case KLogLevel.Critical:
                    Debug.LogError(text);
                    break;
            }
        }

        public void Flush() { }
        public void Dispose() { }
    }
}
#endif
