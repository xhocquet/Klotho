using UnityEngine;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Unity
{
    /// <summary>
    /// Unity-layer convenience for <see cref="KlothoFlowSetupBuilder"/>. Kept out of the Core
    /// builder so the Core assembly stays free of UnityEngine references.
    /// </summary>
    public static class KlothoFlowSetupBuilderUnityExtensions
    {
        /// <summary>
        /// Fills the repeated Unity handshake defaults:
        /// AppVersion = Application.version, DeviceIdProvider = new UnityDeviceIdProvider().
        /// </summary>
        public static KlothoFlowSetupBuilder WithUnityDefaults(this KlothoFlowSetupBuilder builder)
            => builder.WithHandshake(Application.version, new UnityDeviceIdProvider());
    }
}
