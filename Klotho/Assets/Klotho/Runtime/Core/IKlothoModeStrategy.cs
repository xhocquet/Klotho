using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Per-mode behavior dispatcher. Game code consults the strategy resolved from
    /// <c>ISimulationConfig.Mode</c> instead of branching on the enum directly.
    /// </summary>
    public interface IKlothoModeStrategy
    {
        NetworkMode Mode { get; }

        /// <summary>True when the local Inspector "Host" affordance is meaningful for this mode.</summary>
        bool SupportsLocalHost { get; }

        /// <summary>True when the local Inspector "Guest" affordance is meaningful for this mode.</summary>
        bool SupportsLocalGuest { get; }

        /// <summary>True when cold-start AutoReconnect should be attempted (P2P host is excluded).</summary>
        bool IsReconnectEligible(bool isLocalHost);

        /// <summary>Builds the pre-join handshake (returns null for P2P).</summary>
        NetworkMessageBase BuildPreJoinHandshake(int roomId);

        /// <summary>Normalizes the room id (P2P returns -1).</summary>
        int NormalizeRoomId(int requestedRoomId);
    }
}
