// Engine-facing builder extension that registers the Unity debug sink.
#if UNITY_5_3_OR_NEWER
namespace xpTURN.Klotho.Logging
{
    public static class KLogBuilderUnityExtensions
    {
        public static KLogBuilder AddUnityDebug(this KLogBuilder builder) => builder.AddSink(new UnityDebugSink());
    }
}
#endif
