using System;
using xpTURN.Klotho.Logging;
using System.Buffers;
using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Player info implementation.
    /// </summary>
    public class PlayerInfo : IPlayerInfo
    {
        public int PlayerId { get; set; }
        public string PlayerName { get; set; }
        public bool IsReady { get; set; }
        public int Ping { get; set; }
        public PlayerConnectionState ConnectionState { get; set; }
    }

    /// <summary>
    /// Per-spectator connection state.
    /// </summary>
    internal sealed class SpectatorInfo
    {
        public int SpectatorId;
        public int PeerId;
        public int LastSentTick = -1;
    }

    /// <summary>
    /// Disconnected-player info (supports reconnect, pooled).
    /// </summary>
    internal sealed class DisconnectedPlayerInfo
    {
        public int PlayerId;
        public int PeerId;
        public long DisconnectTimeMs;
        public int LastConfirmedTick;
        public int PredictedTickCount;
        public string DeviceId;
        // RTT sample captured at disconnect time. Used by SD Reconnect path
        // (ServerNetworkService.HandleReconnectRequest) to seed RecommendedExtraDelay computation
        // when no fresh handshake samples are available — the new connection's peerId differs from
        // the disconnected one, so PeerSyncStates lookup misses. P2P does not consume this field;
        // KlothoNetworkService's Reconnect path does not currently set RecommendedExtraDelay.
        public int LastAvgRtt;
        // True when this entry was added by the quorum-miss watchdog before transport-level
        // detection. Cleared once transport disconnect confirms or real input arrival rolls back.
        public bool IsPresumedDrop;

        public bool IsActive => PlayerId != 0;

        public void Reset()
        {
            PlayerId = 0;
            PeerId = 0;
            DisconnectTimeMs = 0;
            LastConfirmedTick = 0;
            PredictedTickCount = 0;
            DeviceId = null;
            LastAvgRtt = 0;
            IsPresumedDrop = false;
        }
    }

    internal class LateJoinCatchupInfo
    {
        public int PeerId;
        public int PlayerId;
        public int LastSentTick;
        public int JoinTick;

        /// <summary>
        /// Cold-start Reconnect uses the same catchup mechanism as LateJoin, but with two differences —
        /// (a) PlayerJoinCommand is NOT inserted (existing PlayerId), (b) JoinTick = _engine.CurrentTick (immediate).
        /// </summary>
        public bool IsReconnect;
    }

    /// <summary>
    /// Per-peer handshake state.
    /// </summary>
    internal class PeerSyncState
    {
        public int PeerId;
        public int SyncPacketsSent;
        public long[] RttSamples;
        public long[] ClockOffsetSamples;
        public long LastSyncSentTime;
        public int Attempt;
        public bool Completed;
        public bool IsLateJoin;

        public void GetBestSample(out int rtt, out long offset)
        {
            int minIdx = 0;
            for (int i = 1; i < SyncPacketsSent; i++)
            {
                if (RttSamples[i] < RttSamples[minIdx])
                    minIdx = i;
            }
            rtt = (int)RttSamples[minIdx];
            offset = ClockOffsetSamples[minIdx];
        }
    }

    /// <summary>
    /// Lockstep network service implementation.
    /// </summary>
    public partial class KlothoNetworkService : IKlothoNetworkService
    {
        private const int NUM_SYNC_PACKETS = 5;
        private const int SYNC_TIMEOUT_MS = 5000;
        private const int PING_INTERVAL_MS = 1000;
        private const long RESYNC_RESPONSE_COOLDOWN_MS = 2000;

        private IKLogger _logger;
        private INetworkTransport _transport;
        private ICommandFactory _commandFactory;
        private MessageSerializer _messageSerializer;

        private readonly List<PlayerInfo> _players = new List<PlayerInfo>();
        private readonly Dictionary<int, int> _peerToPlayer = new Dictionary<int, int>();
        private readonly Dictionary<(int tick, int playerId), long> _syncHashes = new Dictionary<(int tick, int playerId), long>();
        private readonly Dictionary<int, PeerSyncState> _peerSyncStates = new Dictionary<int, PeerSyncState>();
        private readonly HashSet<int> _pendingPeers = new HashSet<int>();
        private readonly Dictionary<int, long> _peerConnectedAtMs = new Dictionary<int, long>();
        private readonly List<int> _zombieScanSnapshot = new List<int>();
        private readonly List<SpectatorInfo> _spectators = new List<SpectatorInfo>();
        private int _nextSpectatorId = -1;
        private int _nextPlayerId;

        // Phase-branched player count accounting
        private bool _gameStarted;
        private int _assignedPlayerIdCount;

        private IKlothoEngine _engine;
        private ISessionConfig _sessionConfig;
        private ISimulationConfig _simConfig;
        private IReconnectCredentialsStore _reconnectCredentialsStore;
        private string _appVersion;
        private IDeviceIdProvider _deviceIdProvider;
        private readonly Dictionary<int, string> _peerDeviceIds = new Dictionary<int, string>();

        // Pending extra-delay seed buffered between InitializeFromConnection and SubscribeEngine.
        // Guest path: _engine is wired only at SubscribeEngine, so the seed value forwarded by
        // KlothoConnection (Sync scalar / LateJoin+Reconnect Payload.AcceptMessage) must be held here
        // until the engine is available, then flushed exactly once via ApplyExtraDelay.
        private int? _pendingExtraDelayValue;
        private ExtraDelaySource _pendingExtraDelaySource;

        // Cached list (GC avoidance)
        private readonly List<(int tick, int playerId)> _hashKeysToRemoveCache = new List<(int tick, int playerId)>();

        // Cached message objects (GC avoidance)
        private readonly CommandMessage _commandMessageCache = new CommandMessage();
        private readonly SyncHashMessage _syncHashMessageCache = new SyncHashMessage();
        private readonly PingMessage _pingMessageCache = new PingMessage();
        private readonly PongMessage _pongMessageCache = new PongMessage();
        private readonly PlayerJoinMessage _playerJoinMessageCache = new PlayerJoinMessage();
        private readonly SpectatorInputMessage _spectatorInputMessageCache = new SpectatorInputMessage();
        private readonly ReconnectRequestMessage _reconnectRequestCache = new ReconnectRequestMessage();
        private readonly ReconnectAcceptMessage _reconnectAcceptCache = new ReconnectAcceptMessage();
        private readonly ReconnectRejectMessage _reconnectRejectCache = new ReconnectRejectMessage();

        private long _sessionMagic;
        private SharedTimeClock _sharedClock;
        public SharedTimeClock SharedClock => _sharedClock;
        private SessionPhase _phase;
        private long _gameStartTime; // Absolute game start time on the SharedNow timeline
        private long _lastPingTime;
        private int _pingSequence;

        public int MaxPlayers { get; private set; }
        public int MaxPlayerCapacity => MaxPlayers;
        public string RoomName { get; private set; }
        public int PlayerCount => _players.Count;
        public int SpectatorCount => _spectators.Count;
        public int PendingLateJoinCatchupCount => _lateJoinCatchups.Count;
        public bool AllPlayersReady => _players.TrueForAll(p => p.IsReady);
        public int LocalPlayerId { get; private set; }
        public bool IsHost { get; private set; }
        public int RandomSeed { get; private set; }
        public IReadOnlyList<IPlayerInfo> Players => _players;

        public SessionPhase Phase
        {
            get
            {
                return _phase;
            }

            set
            {
                var prev = _phase;
                _phase = value;
                if (value == SessionPhase.Disconnected || value == SessionPhase.Lobby)
                {
                    // Disconnected = teardown signal, Lobby = fresh session entry.
                    // Guests do not use these counters (host-only) but the reset is harmless and keeps
                    // setter semantics symmetric with the SD ServerNetworkService.
                    _gameStarted = false;
                    _assignedPlayerIdCount = 0;
                    _nextPlayerId = 1;

                    // Emit PresumedDrop summary on match end transition (Playing → end).
                    if (prev == SessionPhase.Playing)
                    {
                        EmitPresumedDropMetrics(IsHost ? "host" : "guest", LocalPlayerId);
                    }
                }
                _logger?.KInformation($"[KlothoNetworkService] Session phase: {_phase}, SharedClock: {SharedClock.SharedNow}ms");

                if (prev != value)
                    OnPhaseChanged?.Invoke(value);
            }
        }

        private int _localTick;

        // Sender's measured frame-advantage (round(CalculateLocalAdvantage)), pushed
        // each tick from the engine and stamped into CommandMessage.SenderAdvantage.
        private int _localAdvantage;

        // Set immediately before a proxy-fill SendCommand (host filling for a
        // disconnected / catching-up player) so the broadcast carries IsProxyTiming and receivers
        // skip the timing vote. Consumed (cleared) inside SendCommand.
        private bool _proxyTimingPending;

        // CommandMessage.TimingFlags bit0.
        private const byte TIMING_FLAG_PROXY = 1;

        public event Action OnGameStart;
        public event Action<long> OnCountdownStarted;
        public event Action<IPlayerInfo> OnPlayerJoined;
        public event Action<IPlayerInfo> OnPlayerLeft;
        public event Action<ICommand> OnCommandReceived;
        public event Action<int, int, long, long> OnDesyncDetected;
        public event Action<int, int, bool> OnSyncHashCompared;
        public event Action<int, int> OnResyncFailureReported;
        public event Action<int> OnMatchAbortReceived;
        public event Action<int, int, int> OnFrameAdvantageReceived;
        public event Action<int> OnLocalPlayerIdAssigned;
        public event Action<int, int> OnFullStateRequested;
        public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
        public event Action<IPlayerInfo> OnPlayerDisconnected;
        public event Action<IPlayerInfo> OnPlayerReconnected;
        public event Action OnReconnecting;
        public event Action<ReconnectRejectReason> OnReconnectFailed;
        public event Action OnReconnected;
        public event Action<int, int> OnLateJoinPlayerAdded;

        // State-change events (fired on transition only).
        public event Action<SessionPhase> OnPhaseChanged;
        public event Action<int> OnPlayerCountChanged;
        public event Action<bool> OnAllPlayersReadyChanged;

        private void RaisePlayerCountIfChanged(int prevCount)
        {
            int newCount = _players.Count;
            if (prevCount != newCount)
                OnPlayerCountChanged?.Invoke(newCount);
        }

        private void RaiseAllPlayersReadyIfChanged(bool prevValue)
        {
            bool newValue = AllPlayersReady;
            if (prevValue != newValue)
                OnAllPlayersReadyChanged?.Invoke(newValue);
        }

        public void SetLocalTick(int tick) { _localTick = tick; }

        public void SetLocalAdvantage(int advantage) { _localAdvantage = advantage; }

        public void SubscribeEngine(IKlothoEngine engine)
        {
            _engine = engine;
            _sessionConfig = engine.SessionConfig;
            _simConfig = engine.SimulationConfig;
            engine.OnVerifiedInputBatchReady += HandleVerifiedInputBatchReady;
            engine.OnDisconnectedInputNeeded += HandleDisconnectedInputNeeded;
            engine.OnCatchupComplete += HandleCatchupComplete;
            engine.OnExtraDelayChanged += HandleEngineExtraDelayChanged;

            // Flush any extra-delay seed buffered during InitializeFromConnection (guest path).
            // Single-slot — re-entry (e.g. warm reconnect re-running InitializeFromConnection) refills with the new value.
            if (_pendingExtraDelayValue.HasValue)
            {
                _engine.ApplyExtraDelay(_pendingExtraDelayValue.Value, _pendingExtraDelaySource);
                _pendingExtraDelayValue = null;
            }
        }

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _emptyCommandCache = _commandFactory.CreateEmptyCommand();
            _sessionConfig = new SessionConfig();  // Default. Replaced in SubscribeEngine().
            _simConfig = new SimulationConfig();   // Default. Replaced in SubscribeEngine().

            // Wire up network events
            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        /// <summary>
        /// Inject the cold-start Reconnect credentials store. Optional — when null, cold-start
        /// credentials are not persisted. Game boot wires this with PlayerPrefsReconnectCredentialsStore.
        /// </summary>
        public void SetReconnectCredentialsStore(IReconnectCredentialsStore store, string appVersion, IDeviceIdProvider deviceIdProvider = null)
        {
            _reconnectCredentialsStore = store;
            _appVersion = appVersion;
            _deviceIdProvider = deviceIdProvider;
        }

        private string GetDeviceId() => _deviceIdProvider?.GetDeviceId() ?? string.Empty;

        /// <summary>
        /// Persist cold-start Reconnect credentials at Phase = Playing entry.
        /// No-op for host (host is not a cold-start target) or when no store is injected.
        /// </summary>
        private void SaveReconnectCredentialsIfApplicable()
        {
            if (IsHost || _reconnectCredentialsStore == null || _transport == null)
                return;

            var creds = new PersistedReconnectCredentials
            {
                RemoteAddress = _transport.RemoteAddress,
                RemotePort = _transport.RemotePort,
                SessionMagic = _sessionMagic,
                LocalPlayerId = LocalPlayerId,
                SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                RoomName = RoomName,
                AppVersion = _appVersion,
                DeviceId = GetDeviceId(),
            };
            _reconnectCredentialsStore.Save(creds);
        }

        /// <summary>
        /// Guest-only initialization — takes over a state where the handshake has already
        /// completed via KlothoConnection, skipping JoinRoom + handshake and starting in Synchronized.
        /// </summary>
        public void InitializeFromConnection(ConnectionResult result, ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _transport = result.Transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _emptyCommandCache = _commandFactory.CreateEmptyCommand();
            _sessionConfig = new SessionConfig();  // Default. Replaced in SubscribeEngine().
            _simConfig = new SimulationConfig();   // Default. Replaced in SubscribeEngine().

            // Apply the handshake result directly (replaces the Initialize + JoinRoom + handshake path)
            IsHost = false;
            LocalPlayerId = result.LocalPlayerId;
            _sessionMagic = result.SessionMagic;
            _sharedClock = new SharedTimeClock(result.SharedEpoch, result.ClockOffset);
            Phase = SessionPhase.Synchronized;

            // Buffer the server-recommended extra delay until SubscribeEngine wires the engine.
            // Source differs per JoinKind:
            //   - LateJoin / Reconnect: payload's AcceptMessage (KlothoConnection preserves the entire msg).
            //   - Normal (Sync): scalar forwarded via ConnectionResult.RecommendedExtraDelay.
            // Defensive ?. + ?? 0 guards a Kind/Payload invariant breach (theoretical) with graceful fallback.
            int seedValue = result.Kind switch
            {
                JoinKind.LateJoin  => result.LateJoinPayload?.AcceptMessage?.RecommendedExtraDelay ?? 0,
                JoinKind.Reconnect => result.ReconnectPayload?.AcceptMessage?.RecommendedExtraDelay ?? 0,
                _                  => result.RecommendedExtraDelay,
            };
            if (seedValue > 0)
            {
                _pendingExtraDelayValue = seedValue;
                _pendingExtraDelaySource = ResolveExtraDelaySource(result.Kind);
            }

            // Wire up network events (same as Initialize)
            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        private static ExtraDelaySource ResolveExtraDelaySource(JoinKind kind) => kind switch
        {
            JoinKind.LateJoin  => ExtraDelaySource.LateJoin,
            JoinKind.Reconnect => ExtraDelaySource.Reconnect,
            _                  => ExtraDelaySource.Sync,
        };

        public void CreateRoom(string roomName, int maxPlayers)
        {
            IsHost = true;
            RoomName = roomName;
            MaxPlayers = maxPlayers;
            LocalPlayerId = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sessionMagic = SessionMagicFactory.Generate();
            _sharedClock = new SharedTimeClock(now, 0);

            // Explicit reset before Phase = Synchronized (the Phase setter does NOT reset on Synchronized,
            // so this is the only init point for a new host session).
            _gameStarted = false;
            _assignedPlayerIdCount = 0;
            _nextPlayerId = 1;

            Phase = SessionPhase.Synchronized; // Host bypasses handshake — no handshaking needed

            InitDisconnectedPlayerPool(maxPlayers);

            // Add the host as a player
            var hostPlayer = new PlayerInfo
            {
                PlayerId = LocalPlayerId,
                PlayerName = "Host",
                IsReady = false
            };
            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Add(hostPlayer);
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);
        }

        public void JoinRoom(string roomName)
        {
            IsHost = false;
            _sharedClock = new SharedTimeClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            Phase = SessionPhase.Lobby;
        }

        public void LeaveRoom(bool keepReconnectCredentials = false)
        {
            if (_transport != null)
            {
                _transport.OnDataReceived -= HandleDataReceived;
                _transport.OnPeerConnected -= HandlePeerConnected;
                _transport.OnPeerDisconnected -= HandlePeerDisconnected;
                _transport.OnConnected -= HandleConnected;
                _transport.OnDisconnected -= HandleDisconnected;
            }

            // Discard cold-start Reconnect credentials on graceful session end.
            // Process-shutdown paths pass keepReconnectCredentials=true so a relaunch can Reconnect.
            if (!keepReconnectCredentials)
                _reconnectCredentialsStore?.Clear();

            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Clear();
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);
            _spectators.Clear();
            _peerToPlayer.Clear();
            _peerSyncStates.Clear();
            _syncHashes.Clear();
            _sessionMagic = 0;
            _gameStartTime = 0;

            // Explicit reset (the Phase setter also handles Disconnected — kept as defensive redundancy).
            _gameStarted = false;
            _assignedPlayerIdCount = 0;
            _nextPlayerId = 1;

            Phase = SessionPhase.Disconnected;
            _sharedClock = default;
        }

        public void SetReady(bool ready)
        {
            // Ready is only allowed once handshake has completed
            if (Phase != SessionPhase.Synchronized)
                return;

            // Broadcast ready state
            var msg = new PlayerReadyMessage
            {
                PlayerId = LocalPlayerId,
                IsReady = ready
            };
            BroadcastMessagePooled(msg, DeliveryMethod.Reliable);

            if (IsHost)
                HandlePlayerReadyMessage(msg);
        }

        private void SendSimulationConfig(int peerId)
        {
            var simConfig = _engine?.SimulationConfig;
            if (simConfig == null) return;

            var msg = new SimulationConfigMessage();
            msg.CopyFrom(simConfig);
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendPlayerConfig(int playerId, Core.PlayerConfigBase playerConfig)
        {
            int size = playerConfig.GetSerializedSize();
            byte[] configData = new byte[size];
            var writer = new Serialization.SpanWriter(configData);
            playerConfig.Serialize(ref writer);

            var msg = new PlayerConfigMessage
            {
                PlayerId = playerId,
                ConfigData = configData,
            };

            if (IsHost)
            {
                // Host: HandlePlayerConfigMessage handles local storage (Deserialize) + relay to all peers
                HandlePlayerConfigMessage(msg);
            }
            else
            {
                // Guest: send to host — the host echo-broadcasts to every peer including the sender
                using (var serialized = _messageSerializer.SerializePooled(msg))
                    _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        private void HandlePlayerConfigMessage(PlayerConfigMessage msg)
        {
            // Deserialize ConfigData into a PlayerConfigBase
            var configMsg = _messageSerializer.Deserialize(msg.ConfigData, msg.ConfigData.Length) as Core.PlayerConfigBase;
            if (configMsg != null)
            {
                (_engine as KlothoEngine)?.HandlePlayerConfigReceived(msg.PlayerId, configMsg);
            }

            // If we are the host, relay to every peer (including the sender — the sender also needs to be
            // stored in the local engine via the MessageSerializer path, so we echo it back)
            if (IsHost)
            {
                foreach (var kv in _peerToPlayer)
                {
                    using (var serialized = _messageSerializer.SerializePooled(msg))
                        _transport.Send(kv.Key, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        private void StartGame()
        {
            // Duplicate-call guard. Re-entry would re-snapshot and disrupt
            // any LateJoin already absorbed past the first call.
            if (_gameStarted)
            {
                _logger?.KWarning($"[KlothoNetworkService] StartGame called twice — ignoring (snapshot already done)");
                return;
            }

            // GameStart snapshot — must run before Phase change so the EffectivePlayerCount
            // post-branch sees consistent state from the moment _gameStarted flips.
            _assignedPlayerIdCount = _players.Count;
            int maxId = 0;
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId > maxId) maxId = _players[i].PlayerId;
            }
            _nextPlayerId = maxId + 1;
            _gameStarted = true;

            long startTime = _sharedClock.SharedNow + _sessionConfig.CountdownDurationMs;
            _gameStartTime = startTime;

            var msg = new GameStartMessage
            {
                StartTime = startTime,
                RandomSeed = Environment.TickCount,
                MaxPlayers = _players.Count,
                MinPlayers = _sessionConfig.MinPlayers,
                MaxSpectators = _sessionConfig.MaxSpectators,
                AllowLateJoin = _sessionConfig.AllowLateJoin,
                LateJoinDelayTicks = _sessionConfig.LateJoinDelayTicks,
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                ReconnectMaxRetries = _sessionConfig.ReconnectMaxRetries,
                LateJoinDelaySafety = _sessionConfig.LateJoinDelaySafety,
                RttSanityMaxMs = _sessionConfig.RttSanityMaxMs,
                MinStallAbortTicks = _sessionConfig.MinStallAbortTicks,
                CountdownDurationMs = _sessionConfig.CountdownDurationMs,
                AbortGraceMs = _sessionConfig.AbortGraceMs,
                EndGracePolicy = (int)_sessionConfig.EndGracePolicy,
                EndGraceMs = _sessionConfig.EndGraceMs,
                ClientShutdownGraceMs = _sessionConfig.ClientShutdownGraceMs,
            };

            foreach (var player in _players)
            {
                msg.PlayerIds.Add(player.PlayerId);
            }

            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
            HandleGameStartMessage(msg); // The server itself also processes it directly

            // Game start: send GameStartMessage to waiting spectators
            if (_spectators.Count > 0)
            {
                using (var serialized = _messageSerializer.SerializePooled(msg))
                {
                    for (int i = 0; i < _spectators.Count; i++)
                    {
                        if (_spectators[i].LastSentTick == -1)
                            _transport.Send(_spectators[i].PeerId, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }

        public void SendCommand(ICommand command)
        {
            int cmdSize = command.GetSerializedSize();
            var cmdBuf = StreamPool.GetBuffer(cmdSize);
            var cmdWriter = new SpanWriter(cmdBuf.AsSpan(0, cmdBuf.Length));
            command.Serialize(ref cmdWriter);

            var msg = _commandMessageCache;
            msg.Tick = command.Tick;
            msg.PlayerId = command.PlayerId;
            msg.SenderTick = _localTick;
            // Stamp the measured advantage + proxy-timing flag. Both are set on every
            // send (the cache is reused) so a stale flag/value never leaks across calls.
            msg.SenderAdvantage = _localAdvantage;
            msg.TimingFlags = _proxyTimingPending ? TIMING_FLAG_PROXY : (byte)0;
            _proxyTimingPending = false; // one-shot — consumed by this send
            msg.CommandData = cmdBuf;
            msg.CommandDataLength = cmdWriter.Position;

            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);

            // The sender processes its own command locally right away (common to host/client)
            HandleCommandMessage(msg);

            StreamPool.ReturnBuffer(cmdBuf);
        }

        public void RequestCommandsForTick(int tick)
        {
            // Implement resend requests as needed
        }

        public void SendSyncHash(int tick, long hash)
        {
            // Store our own hash so arriving remote hashes can be compared against it —
            // the transport does not loop back and SyncHashMessage is not relayed.
            _syncHashes[(tick, LocalPlayerId)] = hash;

            // Compare against remote hashes that arrived before ours was computed
            // (faster peers, or our deferred send after speculative execution).
            foreach (var kvp in _syncHashes)
            {
                if (kvp.Key.tick != tick || kvp.Key.playerId == LocalPlayerId)
                    continue;
                CompareAndReportSyncHash(tick, kvp.Key.playerId, hash, kvp.Value);
            }

            var msg = _syncHashMessageCache;
            msg.Tick = tick;
            msg.Hash = hash;
            msg.PlayerId = LocalPlayerId;

            BroadcastMessagePooled(msg, DeliveryMethod.Unreliable);
        }

        public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash)
        {
            if (IsHost) return; // host handles its own state locally — reports are guest → host
            var msg = new ResyncFailureReportMessage
            {
                PlayerId = LocalPlayerId,
                Tick = tick,
                Reason = (byte)reason,
                LocalHash = localHash,
                RemoteHash = remoteHash,
            };
            _logger?.KWarning($"[KlothoNetworkService][ResyncFailure] report → host: tick={tick}, reason={reason}");
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered); // star topology — reaches the host only
        }

        public void BroadcastMatchAbort(byte reason)
        {
            if (!IsHost) return;
            var msg = new MatchAbortMessage { Reason = reason };
            _logger?.KWarning($"[KlothoNetworkService][MatchAbort] broadcast: reason={reason}");
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
        }

        private void HandleResyncFailureReportMessage(ResyncFailureReportMessage msg)
        {
            if (!IsHost) return;
            _logger?.KWarning($"[KlothoNetworkService][ResyncFailure] received: playerId={msg.PlayerId}, tick={msg.Tick}, reason={(ResyncFailureReason)msg.Reason}, local=0x{msg.LocalHash:X16}, remote=0x{msg.RemoteHash:X16}");
            OnResyncFailureReported?.Invoke(msg.PlayerId, msg.Tick);
        }

        private void HandleMatchAbortMessage(MatchAbortMessage msg)
        {
            if (IsHost) return;
            _logger?.KWarning($"[KlothoNetworkService][MatchAbort] received: reason={msg.Reason}");
            OnMatchAbortReceived?.Invoke(msg.Reason);
        }

        public void InvalidateLocalSyncHashes(int fromTick)
        {
            _hashKeysToRemoveCache.Clear();
            foreach (var key in _syncHashes.Keys)
            {
                if (key.playerId == LocalPlayerId && key.tick >= fromTick)
                    _hashKeysToRemoveCache.Add(key);
            }
            for (int i = 0; i < _hashKeysToRemoveCache.Count; i++)
                _syncHashes.Remove(_hashKeysToRemoveCache[i]);
        }

        public void InvalidateSyncHashes(int fromTick)
        {
            // Drop ALL peers' entries (local + remote) >= fromTick so a post-apply
            // recompute is not compared against a pre-reset remote hash across the reset boundary.
            _hashKeysToRemoveCache.Clear();
            foreach (var key in _syncHashes.Keys)
            {
                if (key.tick >= fromTick)
                    _hashKeysToRemoveCache.Add(key);
            }
            for (int i = 0; i < _hashKeysToRemoveCache.Count; i++)
                _syncHashes.Remove(_hashKeysToRemoveCache[i]);
        }

        public void Update()
        {
            _transport?.PollEvents();

            // Check countdown completion (common to host/client)
            if (Phase == SessionPhase.Countdown && _sharedClock.IsValid)
            {
                if (_sharedClock.SharedNow >= _gameStartTime)
                {
                    Phase = SessionPhase.Playing;
                    SaveReconnectCredentialsIfApplicable();
                    OnGameStart?.Invoke();
                }
            }

            // Reconnect / chain watchdogs — mixed host-only and peer-local; each method gates internally
            CheckQuorumMissPresumedDrop();
            CheckDisconnectedPlayerTimeout();
            CheckChainStallTimeout();
            InjectDisconnectedPlayerInputs();
            InjectCatchupPlayerInputs();
            UpdateReconnect();

            if (!IsHost) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Check handshake timeout
            foreach (var kvp in _peerSyncStates)
            {
                var state = kvp.Value;
                int timeout = state.IsLateJoin ? LATE_JOIN_HANDSHAKE_TIMEOUT_MS : SYNC_TIMEOUT_MS;
                if (!state.Completed && now - state.LastSyncSentTime > timeout)
                {
                    state.Attempt++;
                    SendSyncRequest(kvp.Key, state);
                }
            }

            // Periodic ping (after game start)
            if (Phase == SessionPhase.Playing && now - _lastPingTime >= PING_INTERVAL_MS)
            {
                _lastPingTime = now;
                _pingSequence++;
                var ping = _pingMessageCache;
                ping.Timestamp = now;
                ping.Sequence = _pingSequence;
                using (var serialized = _messageSerializer.SerializePooled(ping))
                {
                    foreach (var kvp in _peerToPlayer)
                    {
                        _transport.Send(kvp.Key, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
                    }
                }
            }
        }

        public void FlushSendQueue()
        {
            _transport?.FlushSendQueue();
        }

        private void HandleDataReceived(int peerId, byte[] data, int length)
        {
            if (_pendingPeers.Contains(peerId))
            {
                var firstMsg = _messageSerializer.Deserialize(data, length);
                _pendingPeers.Remove(peerId);
                if (firstMsg is PlayerJoinMessage playerJoin)
                {
                    _peerDeviceIds[peerId] = playerJoin.DeviceId ?? string.Empty;
                    // Dispatch on _gameStarted (not Phase == Playing).
                    //   Countdown peers go to LateJoin too — a standard handshake completing
                    //   after StartGame would land in a wire/PlayerId mismatch (no GameStartMessage
                    //   sent to the new peer; the race guard in CompletePeerSync catches the residual case).
                    if (_gameStarted)
                    {
                        HandleLateJoin(peerId);
                    }
                    else
                    {
                        // Pending-aware capacity gate — closes the Lobby join race when concurrent peers approach the cap.
                        if (EffectivePlayerCount >= MaxPlayerCapacity)
                        {
                            _logger?.KWarning($"[KlothoNetworkService][HandleDataReceived] Room full, peer {peerId} rejected: gameStarted={_gameStarted}, players={_players.Count}, assigned={_assignedPlayerIdCount}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                            DisconnectWithReason(peerId, 2); // RoomFull
                            return;
                        }
                        StartHandshake(peerId);
                    }
                }
                else if (firstMsg is SpectatorJoinMessage spectatorJoin)
                    HandleSpectatorJoin(peerId, spectatorJoin);
                else if (firstMsg is ReconnectRequestMessage reconnectReq)
                    HandleReconnectRequest(peerId, reconnectReq);
                else
                {
                    _logger?.KWarning($"[KlothoNetworkService] Malformed/unknown first message — peerId={peerId} disconnected");
                    _transport.DisconnectPeer(peerId);
                }
                return;
            }

            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.KWarning($"[KlothoNetworkService] Malformed payload from peerId={peerId} — disconnect");
                _transport.DisconnectPeer(peerId);
                return;
            }

            // Spectator peers: process FullStateRequest without the player-side throttle
            if (message is FullStateRequestMessage spectatorFullReq)
            {
                for (int i = 0; i < _spectators.Count; i++)
                {
                    if (_spectators[i].PeerId == peerId)
                    {
                        OnFullStateRequested?.Invoke(peerId, spectatorFullReq.RequestTick);
                        return;
                    }
                }
            }

            switch (message)
            {
                case CommandMessage cmdMsg:
                    HandleCommandMessage(cmdMsg, peerId);
                    break;

                case SyncHashMessage hashMsg:
                    HandleSyncHashMessage(hashMsg);
                    break;

                case ResyncFailureReportMessage resyncFailMsg:
                    HandleResyncFailureReportMessage(resyncFailMsg);
                    break;

                case MatchAbortMessage matchAbortMsg:
                    HandleMatchAbortMessage(matchAbortMsg);
                    break;

                case GameStartMessage startMsg:
                    HandleGameStartMessage(startMsg);
                    break;

                case PlayerConfigMessage playerConfigMsg:
                    HandlePlayerConfigMessage(playerConfigMsg);
                    break;

                case PlayerReadyMessage readyMsg:
                    HandlePlayerReadyMessage(readyMsg, peerId);
                    break;

                case PingMessage pingMsg:
                    HandlePingMessage(peerId, pingMsg);
                    break;

                case PongMessage pongMsg:
                    HandlePongMessage(peerId, pongMsg);
                    break;

                case SyncRequestMessage syncReqMsg:
                    HandleSyncRequest(peerId, syncReqMsg);
                    break;

                case SyncReplyMessage syncRepMsg:
                    HandleSyncReply(peerId, syncRepMsg);
                    break;

                case SyncCompleteMessage syncCompMsg:
                    HandleSyncComplete(peerId, syncCompMsg);
                    break;

                case FullStateRequestMessage fullReqMsg:
                    HandleFullStateRequest(peerId, fullReqMsg);
                    break;

                case FullStateResponseMessage fullResMsg:
                    HandleFullStateResponse(fullResMsg);
                    break;

                case ReconnectAcceptMessage reconnectAcceptMsg:
                    HandleReconnectAccept(reconnectAcceptMsg);
                    break;

                case ReconnectRejectMessage reconnectRejectMsg:
                    HandleReconnectReject(reconnectRejectMsg);
                    break;

                case LateJoinAcceptMessage lateJoinAcceptMsg:
                    HandleLateJoinAccept(lateJoinAcceptMsg);
                    break;

                case LateJoinNotificationMessage lateJoinNotification:
                    HandleLateJoinNotification(lateJoinNotification);
                    break;

                case PlayerStateNotificationMessage playerStateMsg:
                    HandlePlayerStateNotification(playerStateMsg);
                    break;

                case SpectatorInputMessage catchupMsg:
                    HandleCatchupInputMessage(catchupMsg);
                    break;

                case RecommendedExtraDelayUpdateMessage extraDelayMsg:
                    HandleRecommendedExtraDelayUpdate(extraDelayMsg);
                    break;

                case ReactiveExtraDelayReportMessage reactiveReport:
                    HandleReactiveExtraDelayReport(peerId, reactiveReport);
                    break;
            }
        }

        // Guest reports its effective extra-delay to the host so the host folds
        // the locally-observed reactive correction (rollback-burst) into the broadcast baseline. Host is
        // excluded (no reactive). Star topology: BroadcastMessagePooled from a guest reaches only peerId 0
        // (the host). Changes are sparse (escalate cooldown / decay dwell) — dedupe on unchanged value.
        private int _lastReportedEffective = -1;
        private void HandleEngineExtraDelayChanged(int effective)
        {
            if (IsHost) return;
            if (effective == _lastReportedEffective) return;
            _lastReportedEffective = effective;
            BroadcastMessagePooled(new ReactiveExtraDelayReportMessage { EffectiveExtraDelay = effective },
                DeliveryMethod.ReliableOrdered);
            _logger?.KInformation($"[Metrics][ReactiveReport] {{\"role\":\"p2p-guest\",\"dir\":\"send\",\"effective\":{effective}}}");
        }

        // Guest reports its effective extra-delay; the host folds it into the
        // per-peer target baseline and re-evaluates the max-over-peers broadcast. Host-only.
        private void HandleReactiveExtraDelayReport(int peerId, ReactiveExtraDelayReportMessage msg)
        {
            if (!IsHost) return;
            _reportedEffective[peerId] = msg.EffectiveExtraDelay < 0 ? 0 : msg.EffectiveExtraDelay;
            _logger?.KInformation($"[Metrics][ReactiveReport] {{\"role\":\"p2p-host\",\"dir\":\"absorb\",\"peerId\":{peerId},\"reportedEffective\":{_reportedEffective[peerId]}}}");
            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
                MaybePushExtraDelayUpdate(playerId, peerId);
        }

        // ── Gameplay messages ────────────────────

        private void HandleCommandMessage(CommandMessage msg, int fromPeerId = -1)
        {
            var cmdSpan = msg.CommandDataSpan;
            if (cmdSpan.Length < 4)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleCommandMessage] Command data too short: length={cmdSpan.Length}, playerId={msg.PlayerId}, tick={msg.Tick}");
                return;
            }

            // Guest >> Host >> other guests
            if (IsHost && fromPeerId != -1)
            {
                // If the (tick, playerId) slot is sealed locally (host has already filled with
                // an empty placeholder and chain advanced past it), suppress relay so other peers
                // keep the same empty placeholder. Without this guard, a late real packet from
                // the source peer reaches guests un-sealed and overwrites their empty → host vs
                // guest InputBuffer divergence (silent desync, no fallback at this stage).
                bool isSealedHere = _engine != null && _engine.IsCommandSealed(msg.Tick, msg.PlayerId);
                if (isSealedHere)
                {
                    _relaySealDropCount++;
                    return;
                }
                RelayMessage(msg, fromPeerId, DeliveryMethod.ReliableOrdered);
            }

            // DO NOT remove _lateJoinCatchups on first command receipt. Guest's first command
            // (Spawn at JoinTick) arrives within ~ms, well before guest has caught up via input
            // batches. Removal is now done in HandleFrameVerifiedForCatchup once
            // info.LastSentTick >= info.JoinTick — i.e., once host has actually delivered enough
            // input for guest to self-sustain.

            // Our own command received via the network has already been processed locally — avoid duplicates
            if (fromPeerId != -1 && msg.PlayerId == LocalPlayerId)
                return;

            var reader = new SpanReader(cmdSpan);
            var command = _commandFactory.DeserializeCommandRaw(ref reader);
            if (command == null)
            {
                _logger?.KWarning($"[KlothoNetworkService][HandleCommandMessage] DeserializeCommandRaw returned null (dataLen={cmdSpan.Length})");
                return;
            }

            // Quorum-miss watchdog false-positive: real input arrived for a player that was
            // presumed-dropped → remove from pool + rollback to restore real command path.
            // Network arrivals only — the watchdog's own activation fill echoes back here
            // synchronously (SendCommand self-dispatch, fromPeerId == -1) with PlayerId=X, and
            // the echo at the chain-stop tick lands exactly on the dynamic stall tick — the
            // presumed-drop would self-release in the very call stack that armed it. Path B's
            // per-tick fill echo matches the stall tick too, so every fill source must be
            // excluded here.
            if (fromPeerId != -1)
                OnRealCommandReceivedDuringPresumedDrop(command);

            OnCommandReceived?.Invoke(command);

            // Frame advantage is remote timing info — local echoes (SendCommand self-dispatch,
            // fromPeerId == -1) must not feed this machine's tick into the remote-tick median.
            // Guarding on PlayerId is NOT equivalent: host proxy-fills (Reconnect/Catchup) echo
            // with another player's PlayerId but our own SenderTick.
            // A proxy-timing broadcast (host filling for a disconnected/catching-up
            // player) carries the SENDING machine's tick/advantage, not the slot owner's — skip
            // the vote so it does not pollute _remoteTicks[that player] (wire-side bias source).
            if (fromPeerId != -1 && (msg.TimingFlags & TIMING_FLAG_PROXY) == 0)
                OnFrameAdvantageReceived?.Invoke(msg.PlayerId, msg.SenderTick, msg.SenderAdvantage);
        }

        private void HandleSyncHashMessage(SyncHashMessage msg)
        {
            _syncHashes[(msg.Tick, msg.PlayerId)] = msg.Hash;

            // Compare when the local hash for this tick has already been computed;
            // otherwise SendSyncHash performs the comparison once it is (deferred send path).
            if (_syncHashes.TryGetValue((msg.Tick, LocalPlayerId), out long localHash))
                CompareAndReportSyncHash(msg.Tick, msg.PlayerId, localHash, msg.Hash);
        }

        /// <summary>
        /// Single comparison point for local vs remote sync hashes. Fires OnDesyncDetected on
        /// mismatch and always fires OnSyncHashCompared — the engine promotes its last-matched
        /// sync anchor on matched comparisons (event-based promotion, no grace window).
        /// </summary>
        private void CompareAndReportSyncHash(int tick, int remotePlayerId, long localHash, long remoteHash)
        {
            bool matched = localHash == remoteHash;
            if (!matched)
            {
                _logger?.KWarning($"[KlothoNetworkService][SyncHash] Desync at tick {tick}: local=0x{localHash:X16}, remote(player{remotePlayerId})=0x{remoteHash:X16}");
                OnDesyncDetected?.Invoke(remotePlayerId, tick, localHash, remoteHash);
            }
            OnSyncHashCompared?.Invoke(tick, remotePlayerId, matched);
        }

        private void HandleGameStartMessage(GameStartMessage msg)
        {
            _logger?.KInformation($"[KlothoNetworkService][HandleGameStartMessage] Game start: seed={msg.RandomSeed}, startTime={msg.StartTime}, players={msg.PlayerIds.Count}");

            // Apply server-authoritative SessionConfig fields in place. Engine and NetworkService
            // share the same SessionConfig reference, so mutating the instance propagates to both
            // readers automatically. Match-start one-shot; SessionConfig stays immutable afterward.
            // Host self-dispatch: msg values originate from _sessionConfig, so this is effectively a no-op.
            if (_sessionConfig is SessionConfig cfg)
            {
                cfg.RandomSeed = msg.RandomSeed;
                cfg.MaxPlayers = msg.MaxPlayers;
                cfg.MinPlayers = msg.MinPlayers;
                cfg.MaxSpectators = msg.MaxSpectators;
                cfg.AllowLateJoin = msg.AllowLateJoin;
                cfg.LateJoinDelayTicks = msg.LateJoinDelayTicks;
                cfg.ReconnectTimeoutMs = msg.ReconnectTimeoutMs;
                cfg.ReconnectMaxRetries = msg.ReconnectMaxRetries;
                cfg.LateJoinDelaySafety = msg.LateJoinDelaySafety;
                cfg.RttSanityMaxMs = msg.RttSanityMaxMs;
                cfg.MinStallAbortTicks = msg.MinStallAbortTicks;
                cfg.CountdownDurationMs = msg.CountdownDurationMs;
                cfg.AbortGraceMs = msg.AbortGraceMs;
                cfg.EndGracePolicy = (EndGracePolicy)msg.EndGracePolicy;
                cfg.EndGraceMs = msg.EndGraceMs;
                cfg.ClientShutdownGraceMs = msg.ClientShutdownGraceMs;
            }

            // Update the player list
            int prevCount = _players.Count;
            bool prevReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < msg.PlayerIds.Count; i++)
            {
                var player = new PlayerInfo
                {
                    PlayerId = msg.PlayerIds[i],
                    IsReady = true
                };
                _players.Add(player);
            }
            RaisePlayerCountIfChanged(prevCount);
            RaiseAllPlayersReadyIfChanged(prevReady);

            RandomSeed = msg.RandomSeed;
            _gameStartTime = msg.StartTime;
            Phase = SessionPhase.Countdown;
            OnCountdownStarted?.Invoke(msg.StartTime);
        }

        private void HandlePlayerReadyMessage(PlayerReadyMessage msg, int fromPeerId = -1)
        {
            _logger?.KInformation($"[KlothoNetworkService][HandlePlayerReadyMessage] Player ready: playerId={msg.PlayerId}, isReady={msg.IsReady}, fromPeerId={fromPeerId}");

            var player = _players.Find(p => p.PlayerId == msg.PlayerId);
            if (player != null)
            {
                bool prevReady = AllPlayersReady;
                player.IsReady = msg.IsReady;
                RaiseAllPlayersReadyIfChanged(prevReady);
            }

            if (IsHost && fromPeerId != -1)
                BroadcastMessagePooled(msg, DeliveryMethod.Reliable);

            // Host: start the game once every player is ready
            if (IsHost && AllPlayersReady && _players.Count >= _sessionConfig.MinPlayers)
            {
                StartGame();
            }
        }

        // ── Periodic RTT measurement ──────────────────────

        private void HandlePingMessage(int peerId, PingMessage msg)
        {
            var pong = _pongMessageCache;
            pong.Timestamp = msg.Timestamp;
            pong.Sequence = msg.Sequence;
            using (var serialized = _messageSerializer.SerializePooled(pong))
            {
                _transport.Send(peerId, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }
        }

        private void HandlePongMessage(int peerId, PongMessage msg)
        {
            // Calculate RTT
            long rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - msg.Timestamp;

            if (_peerToPlayer.TryGetValue(peerId, out int playerId))
            {
                var player = _players.Find(p => p.PlayerId == playerId);
                if (player != null)
                {
                    player.Ping = (int)rtt;

                    if (Phase == SessionPhase.Playing)
                    {
                        if (!_rttSmoothers.TryGetValue(playerId, out var smoother))
                        {
                            smoother = new PlayerRttSmoother();
                            _rttSmoothers[playerId] = smoother;
                        }
                        smoother.OnSample((int)rtt);
                        MaybePushExtraDelayUpdate(playerId, peerId);
                    }
                }
            }
        }

        private void RelayMessage(INetworkMessage message, int excludePeerId, DeliveryMethod deliveryMethod)
        {
            using (var serialized = _messageSerializer.SerializePooled(message))
            {
                foreach (var kvp in _peerToPlayer)
                {
                    if (kvp.Key != excludePeerId)
                        _transport.Send(kvp.Key, serialized.Data, serialized.Length, deliveryMethod);
                }
            }
        }

        private void BroadcastMessagePooled(INetworkMessage message, DeliveryMethod deliveryMethod)
        {
            using (var serialized = _messageSerializer.SerializePooled(message))
            {
                if (IsHost)
                    _transport?.Broadcast(serialized.Data, serialized.Length, deliveryMethod);
                else
                    _transport?.Send(0, serialized.Data, serialized.Length, deliveryMethod);
            }
        }

        // ── Player count accounting helpers ─────────────────────────

        // Phase-branched effective slot count.
        //   Pre-GameStart: _players.Count (slot reuse on leave) + pending handshakes.
        //   Post-GameStart: Math.Max(_assignedPlayerIdCount, _nextPlayerId-1) enforces both
        //     the capacity invariant and the bot-ID invariant — covers sparse distributions
        //     where _nextPlayerId outpaces the slot count after a Pre-GameStart leave.
        //   Host-only (guests do not maintain _gameStarted / _assignedPlayerIdCount).
        private int EffectivePlayerCount
        {
            get
            {
                int pending = CountPendingHandshakes();
                if (!_gameStarted)
                    return _players.Count + pending;

                int occupiedSlots = Math.Max(_assignedPlayerIdCount, _nextPlayerId - 1);
                return occupiedSlots + pending;
            }
        }

        // Pre-GameStart slot reuse — smallest unused PlayerId in [1, upper].
        //   P2P (LocalPlayerId == 0): host occupies slot 0, guests use [1, MaxPlayerCapacity - 1].
        //   Returns -1 only if all slots are full (callers' gate must prevent this; -1 = regression).
        private int FindSmallestUnusedPlayerId()
        {
            int upper = (LocalPlayerId == 0) ? MaxPlayerCapacity - 1 : MaxPlayerCapacity;
            for (int id = 1; id <= upper; id++)
            {
                bool used = false;
                for (int i = 0; i < _players.Count; i++)
                {
                    if (_players[i].PlayerId == id) { used = true; break; }
                }
                if (!used) return id;
            }
            return -1;
        }

        private readonly byte[] _disconnectReasonBuf = new byte[1];

        // Reject a peer by disconnecting with the reason carried on the disconnect packet. The client
        // reads it via the transport's last-disconnect payload alongside the disconnect notification.
        private void DisconnectWithReason(int peerId, byte reason)
        {
            _disconnectReasonBuf[0] = reason;
            _transport.DisconnectPeer(peerId, _disconnectReasonBuf);
        }

        // Phase-branched slot reservation + reject action capsule.
        //   Pre-GameStart: smallest unused ID (slot reuse).
        //   Post-GameStart: monotonic _nextPlayerId++ (permanent occupation).
        //   On reject: DisconnectWithReason + immediate _peerSyncStates.Remove — the transport disconnect
        //   is async; without explicit removal, the stale entry keeps counting in CountPendingHandshakes.
        //   The reject reason rides the disconnect packet payload; the client maps it to a JoinFailReason.
        private bool TryReservePlayerSlot(int peerId, out int newPlayerId)
        {
            if (!_gameStarted)
            {
                newPlayerId = FindSmallestUnusedPlayerId();
                if (newPlayerId < 0)
                {
                    _logger?.KError($"[KlothoNetworkService] FindSmallestUnusedPlayerId returned -1: peer={peerId}, players={_players.Count}, pending={CountPendingHandshakes()}, max={MaxPlayerCapacity}");
                    DisconnectWithReason(peerId, 2); // RoomFull
                    _peerSyncStates.Remove(peerId);
                    return false;
                }
            }
            else
            {
                if (Math.Max(_assignedPlayerIdCount, _nextPlayerId - 1) >= MaxPlayerCapacity)
                {
                    _logger?.KError($"[KlothoNetworkService] Post-GameStart slot overflow: assigned={_assignedPlayerIdCount}, nextId={_nextPlayerId}, max={MaxPlayerCapacity}, peer={peerId}");
                    DisconnectWithReason(peerId, 2); // RoomFull
                    _peerSyncStates.Remove(peerId);
                    newPlayerId = -1;
                    return false;
                }
                newPlayerId = _nextPlayerId++;
                _assignedPlayerIdCount++;
            }
            return true;
        }

    }
}
