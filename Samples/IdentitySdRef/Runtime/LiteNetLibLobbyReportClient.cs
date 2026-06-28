using System;

using xpTURN.Klotho.Logging; // IKLogger

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Dedicated-server → lobby reporting transport (P1). Owns its own <see cref="LiteNetLibLobbyConnection"/>
    /// to the dev lobby (separate from the redeem client's connection), and is
    /// the SOLE owner of <c>serverRegister</c>: an immutable payload built once at construction (all fields are
    /// fixed at startup) and (re)sent by the connection's <c>onConnected</c> on first connect and every
    /// reconnect — that doubles as the reconnect-restore trigger. <see cref="SdRoomReporter"/> drives periodic
    /// <c>roomReport</c> via <see cref="SendRoomReport"/>.
    /// <para>
    /// One-way: the lobby sends no reply to either message (ReliableOrdered guarantees delivery), so this
    /// client never receives. <c>onConnected</c> also fires the injected callback so the reporter can mark its
    /// snapshot dirty and push a fresh report next tick (the poll thread never reads the reporter's snapshot).
    /// </para>
    /// </summary>
    public sealed class LiteNetLibLobbyReportClient : IDisposable
    {
        private readonly LiteNetLibLobbyConnection _conn;
        private readonly string _serverId;
        private readonly byte[] _serverRegister; // immutable — built once (serverId/host/port/maxRooms/maxPlayers fixed)
        private readonly Action _onConnected;    // reporter hook: mark dirty → fresh report next tick

        /// <param name="lobbyHost">/<paramref name="lobbyPort"/> — the dev lobby to connect to.</param>
        /// <param name="serverId">This dedicated server's id (carried in both messages).</param>
        /// <param name="advertiseHost">/<paramref name="advertisePort"/> — the client-reachable address the
        /// lobby injects into issue responses (NOT the 0.0.0.0 bind).</param>
        /// <param name="maxRooms">/<paramref name="maxPlayersPerRoom"/> — this server's actual capacity (D4
        /// authority): the dedi's own config, not the lobby seed constants.</param>
        /// <param name="onConnected">Invoked after serverRegister is (re)sent on connect — the reporter marks
        /// dirty so a fresh roomReport follows.</param>
        public LiteNetLibLobbyReportClient(IKLogger logger, string lobbyHost, int lobbyPort,
                                           string serverId, string advertiseHost, int advertisePort,
                                           int maxRooms, int maxPlayersPerRoom,
                                           Action onConnected, string connectionKey = "xpTURN.DevLobby")
        {
            _serverId = serverId;
            _onConnected = onConnected;
            _serverRegister = LobbyWire.EncodeServerRegister(0, serverId, advertiseHost, advertisePort,
                                                             maxRooms, maxPlayersPerRoom);
            _conn = new LiteNetLibLobbyConnection(logger, lobbyHost, lobbyPort, connectionKey,
                                                  OnData, OnConnected, threadName: "klotho-lobby-report-poll");
            _conn.Start();
        }

        public bool IsConnected => _conn.IsConnected;

        /// <summary>Sends a roomReport snapshot (drops silently if not yet connected — onConnected re-pushes
        /// serverRegister + dirty on reconnect, so the next report follows).</summary>
        public void SendRoomReport(RoomStateReport[] rooms, int count)
            => _conn.TrySend(LobbyWire.EncodeRoomReport(0, _serverId, rooms, count));

        private void OnConnected()
        {
            _conn.TrySend(_serverRegister); // (re)advertise capacity/endpoint — also the reconnect-restore trigger
            _onConnected?.Invoke();         // reporter marks dirty → fresh roomReport next tick
        }

        // One-way channel: the lobby never replies to register/report, so nothing to decode here.
        private void OnData(int peerId, byte[] data, int length) { }

        public void Dispose() => _conn.Dispose();
    }
}
