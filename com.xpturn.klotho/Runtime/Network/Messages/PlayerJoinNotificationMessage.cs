using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Broadcast from host (P2P) / server (SD) to existing peers when a new player completes the
    /// normal-join (pre-game / lobby) handshake. Recipients add the player to their _players list so
    /// PlayerCount / Players / OnPlayerJoined surfaces stay consistent across all peers before StartGame.
    /// Distinct from LateJoinNotificationMessage (post-game, IsReady implicitly true) — this carries the
    /// lobby IsReady (default false) and connection state explicitly.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerJoinNotification)]
    public partial class PlayerJoinNotificationMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public byte ConnectionState;
        [KlothoOrder] public bool IsReady;
    }
}
