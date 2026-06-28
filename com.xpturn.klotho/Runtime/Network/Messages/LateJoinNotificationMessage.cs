using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Broadcast from host (P2P) or server (SD) to existing peers when a new player completes
    /// late-join handshake. Recipients add the player to their _players list so PlayerCount /
    /// Players / OnPlayerJoined surfaces stay consistent across all peers.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.LateJoinNotification)]
    public partial class LateJoinNotificationMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int JoinTick;
        // Authoritative identity, declared after the prior fields so wire offsets stay stable. Unset = "".
        [KlothoOrder] public string Account;
        [KlothoOrder] public string DisplayName;
    }
}
