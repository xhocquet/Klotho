using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Client-side IServerDrivenNetworkService implementation for ServerDriven mode.
    /// Handles server connection, handshake, GameStart, and receiving/firing server messages.
    /// SubscribeEngine(): subscribes to engine.OnCatchupComplete.
    /// </summary>
    public class ServerDrivenClientService : IServerDrivenNetworkService
    {
        private IKLogger _logger;
        private INetworkTransport _transport;
        private ICommandFactory _commandFactory;
        private MessageSerializer _messageSerializer;

        private IKlothoEngine _engine;
        // Buffered extra-delay value when handshake handlers (Sync/LateJoin/Reconnect) fire before
        // SubscribeEngine is called. Applied on SubscribeEngine. Cleared after apply.
        private int? _pendingExtraDelayApply;
        private ExtraDelaySource _pendingExtraDelaySource;
        private ISimulationConfig _simConfig;
        private ISessionConfig _sessionConfig;
        private IReconnectCredentialsStore _reconnectCredentialsStore;
        private string _appVersion;
        private IDeviceIdProvider _deviceIdProvider;

        // Player management
        private readonly List<PlayerInfo> _players = new List<PlayerInfo>();

        // Session
        private long _sessionMagic;
        private SharedTimeClock _sharedClock;
        private int _roomId = -1;
        private SessionPhase _phase;
        private int _localPlayerId;
        private int _localTick;
        private int _randomSeed;
        private long _gameStartTime;

        // Input resend queue
        private readonly Queue<UnackedInput> _unackedInputs = new Queue<UnackedInput>();
        private long _lastResendTime;

        // Reconnect state
        private enum ReconnectState { None, WaitingForTransport, SendingRequest, WaitingForFullState, Failed }
        private ReconnectState _reconnectState;
        private long _reconnectStartTimeMs;
        private long _reconnectRequestSentTime;
        private int _reconnectRetryCount;
        private long _lastTransportReconnectTime;
        private const int RECONNECT_REQUEST_TIMEOUT_MS = 5000;
        private const int TRANSPORT_RECONNECT_INTERVAL_MS = 1000;

        // Message cache (GC avoidance)
        private readonly ClientInputMessage _clientInputCache = new ClientInputMessage();
        private readonly ClientInputBundleMessage _bundleCache = new ClientInputBundleMessage();
        private readonly PlayerReadyMessage _playerReadyCache = new PlayerReadyMessage();
        private readonly RoomHandshakeMessage _roomHandshakeCache = new RoomHandshakeMessage();
        private readonly PongMessage _pongMessageCache = new PongMessage();

        // ── IKlothoNetworkService properties ────────────────────

        public SessionPhase Phase
        {
            get => _phase;
            private set
            {
                var prev = _phase;
                _phase = value;
                _logger?.KInformation($"[SDClientService] Session phase: {_phase}, SharedClock: {SharedClock.SharedNow}ms");

                if (prev != value)
                    OnPhaseChanged?.Invoke(value);
            }
        }

        public SharedTimeClock SharedClock => _sharedClock;
        public int PlayerCount => _players.Count;
        public int SpectatorCount => 0;
        public int PendingLateJoinCatchupCount => 0;
        public bool AllPlayersReady => _players.TrueForAll(p => p.IsReady);
        public int LocalPlayerId => _localPlayerId;
        public bool IsHost => false;
        public int RandomSeed => _randomSeed;
        public IReadOnlyList<IPlayerInfo> Players => _players;

        // ── IServerDrivenNetworkService properties ────────────────

        public bool IsServer => false;

        // ── Events ──────────────────────────────────────────

        public event Action OnGameStart;
        public event Action<long> OnCountdownStarted;
        public event Action<IPlayerInfo> OnPlayerJoined;
        public event Action<IPlayerInfo> OnPlayerLeft;
        public event Action<ICommand> OnCommandReceived;
        public event Action<int, int, long, long> OnDesyncDetected;
        public event Action<int, int> OnFrameAdvantageReceived;
        public event Action<int> OnLocalPlayerIdAssigned;
        public event Action<int, int> OnFullStateRequested;
        public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
        public event Action<IPlayerInfo> OnPlayerDisconnected;
        public event Action<IPlayerInfo> OnPlayerReconnected;
        public event Action OnReconnecting;
        public event Action<ReconnectRejectReason> OnReconnectFailed;
        public event Action OnReconnected;
        public event Action<int, int> OnLateJoinPlayerAdded;

        // SD-only events
        public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
        public event Action<int> OnInputAckReceived;
        public event Action<int, byte[], long> OnServerFullStateReceived;
        public event Action<int, long> OnBootstrapBegin;
        public event Action<int, int, RejectionReason> OnCommandRejected;

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

        // ── Initialization ─────────────────────────────────────────

        public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger)
        {
            _logger = logger;
            _transport = transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _simConfig = new SimulationConfig();    // Default. Replaced in SubscribeEngine().
            _sessionConfig = new SessionConfig();   // Default. Replaced in SubscribeEngine().

            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        /// <summary>
        /// Inject the cold-start Reconnect credentials store. Optional — when null, cold-start
        /// credentials are not persisted.
        /// </summary>
        public void SetReconnectCredentialsStore(IReconnectCredentialsStore store, string appVersion, IDeviceIdProvider deviceIdProvider = null)
        {
            _reconnectCredentialsStore = store;
            _appVersion = appVersion;
            _deviceIdProvider = deviceIdProvider;
        }

        private string GetDeviceId() => _deviceIdProvider?.GetDeviceId() ?? string.Empty;

        private void SaveReconnectCredentialsIfApplicable()
        {
            if (_reconnectCredentialsStore == null || _transport == null)
                return;

            var creds = new PersistedReconnectCredentials
            {
                RemoteAddress = _transport.RemoteAddress,
                RemotePort = _transport.RemotePort,
                SessionMagic = _sessionMagic,
                LocalPlayerId = _localPlayerId,
                SavedAtUnixMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReconnectTimeoutMs = _sessionConfig.ReconnectTimeoutMs,
                RoomName = null,    // SD path uses RoomId; RoomName retained for P2P symmetry
                RoomId = _roomId,   // SD multi-room routing identifier
                AppVersion = _appVersion,
                DeviceId = GetDeviceId(),
            };
            _reconnectCredentialsStore.Save(creds);
        }

        /// <summary>
        /// Guest-only initialization — takes over a handshake completed by KlothoConnection,
        /// skipping JoinRoom + handshake and starting from Synchronized.
        /// roomId: the room ID assigned to the client in a multi-room setup (default -1 = single room).
        ///   Used in the SendFirstMessage → SendRoomHandshake resend path on Reconnect.
        /// </summary>
        public void InitializeFromConnection(ConnectionResult result, ICommandFactory commandFactory, IKLogger logger, int roomId = -1)
        {
            _logger = logger;
            _transport = result.Transport;
            _commandFactory = commandFactory;
            _messageSerializer = new MessageSerializer();
            _simConfig = new SimulationConfig();    // Default. Replaced in SubscribeEngine().
            _sessionConfig = new SessionConfig();   // Default. Replaced in SubscribeEngine().

            // Apply handshake result directly
            _localPlayerId = result.LocalPlayerId;
            _sessionMagic = result.SessionMagic;
            _roomId = roomId;
            _sharedClock = new SharedTimeClock(result.SharedEpoch, result.ClockOffset);
            Phase = SessionPhase.Synchronized;

            // KlothoConnection consumes the handshake messages before this service is wired up, so the
            // per-kind handlers below never run in the guest path. Forward the server-recommended
            // extra delay explicitly per JoinKind (buffered until SubscribeEngine).
            int seedExtraDelay;
            ExtraDelaySource seedSource;
            switch (result.Kind)
            {
                case JoinKind.LateJoin:
                    seedExtraDelay = result.LateJoinPayload.AcceptMessage.RecommendedExtraDelay;
                    seedSource = ExtraDelaySource.LateJoin;
                    break;
                case JoinKind.Reconnect:
                    seedExtraDelay = result.ReconnectPayload.AcceptMessage.RecommendedExtraDelay;
                    seedSource = ExtraDelaySource.Reconnect;
                    break;
                default:
                    seedExtraDelay = result.RecommendedExtraDelay;
                    seedSource = ExtraDelaySource.Sync;
                    break;
            }
            ApplyOrPendExtraDelay(seedExtraDelay, seedSource);

            _transport.OnDataReceived += HandleDataReceived;
            _transport.OnConnected += HandleConnected;
            _transport.OnDisconnected += HandleDisconnected;
        }

        public void SubscribeEngine(IKlothoEngine engine)
        {
            _engine = engine;
            _simConfig = engine.SimulationConfig;
            _sessionConfig = engine.SessionConfig;
            // Drain any extra-delay value buffered before the engine was ready (Sync/LateJoin/Reconnect
            // handshake handlers fire before SubscribeEngine in the SD client setup sequence).
            if (_pendingExtraDelayApply.HasValue)
            {
                engine.ApplyExtraDelay(_pendingExtraDelayApply.Value, _pendingExtraDelaySource);
                _pendingExtraDelayApply = null;
            }
            engine.OnCatchupComplete += HandleCatchupComplete;
        }

        // Applies the value immediately if the engine is ready, otherwise buffers it for SubscribeEngine.
        // Called from handshake handlers (Sync/LateJoin/Reconnect) where the engine may not yet exist.
        // Mid-match RecommendedExtraDelayUpdate handler does not use this — engine is guaranteed ready
        // when that message is received (server gates push on Phase == Playing).
        internal void ApplyOrPendExtraDelay(int delay, ExtraDelaySource source)
        {
            if (_engine != null)
            {
                _engine.ApplyExtraDelay(delay, source);
            }
            else
            {
                _pendingExtraDelayApply = delay;
                _pendingExtraDelaySource = source;
            }
        }

        /// <summary>
        /// Restores `_players` / `_sessionMagic` / `_randomSeed` from a LateJoinAcceptMessage received via KlothoConnection on the Late Join path.
        /// Must be called by KlothoSession.Create **before** `engine.Initialize`
        /// so that the `_activePlayerIds` copy loop inside Engine.Initialize is populated correctly.
        /// </summary>
        public void SeedLateJoinPlayers(LateJoinPayload payload)
        {
            var msg = payload.AcceptMessage;
            SeedPlayersFromCatchupPayload(msg.Magic, msg.RandomSeed, msg.PlayerCount, msg.PlayerIds, msg.PlayerConnectionStates);
        }

        /// <summary>
        /// Cold-start Reconnect counterpart of SeedLateJoinPlayers. The host echoes the existing
        /// PlayerId via ReconnectAcceptMessage.PlayerId rather than allocating a new one.
        /// _sessionMagic is restored from the persisted credentials (already set in InitializeFromConnection).
        /// </summary>
        public void SeedReconnectPlayers(ReconnectPayload payload)
        {
            var msg = payload.AcceptMessage;
            // SD has no Magic field on ReconnectAcceptMessage — _sessionMagic is already set by InitializeFromConnection
            // from creds. Pass current _sessionMagic to keep helper signature symmetric.
            SeedPlayersFromCatchupPayload(_sessionMagic, msg.RandomSeed, msg.PlayerCount, msg.PlayerIds, msg.PlayerConnectionStates);
        }

        private void SeedPlayersFromCatchupPayload(long sessionMagic, int randomSeed, int playerCount, List<int> playerIds, List<byte> playerConnectionStates)
        {
            _sessionMagic = sessionMagic;
            _randomSeed = randomSeed;
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < playerCount && i < playerIds.Count; i++)
            {
                var p = new PlayerInfo { PlayerId = playerIds[i], IsReady = true };
                if (playerConnectionStates != null && i < playerConnectionStates.Count)
                    p.ConnectionState = (PlayerConnectionState)playerConnectionStates[i];
                _players.Add(p);
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            Phase = SessionPhase.Playing;   // Session is already Playing at Late Join / cold-start Reconnect time
            SaveReconnectCredentialsIfApplicable();
        }

        // ── Session management ───────────────────────────────────────

        public void CreateRoom(string roomName, int maxPlayers)
        {
            throw new NotSupportedException("The client cannot call CreateRoom.");
        }

        public void JoinRoom(string roomName)
        {
            _roomId = int.TryParse(roomName, out var id) ? id : -1;
            _sharedClock = new SharedTimeClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            Phase = SessionPhase.Lobby;
        }

        /// <summary>
        /// Connects to the server. Call after JoinRoom.
        /// </summary>
        public void Connect(string address, int port)
        {
            if (!_transport.Connect(address, port))
            {
                _logger?.KError($"[SDClientService] Failed to start client transport for {address}:{port}");
                Phase = SessionPhase.Disconnected;
            }
        }

        public void LeaveRoom(bool keepReconnectCredentials = false)
        {
            _transport.OnDataReceived -= HandleDataReceived;
            _transport.OnConnected -= HandleConnected;
            _transport.OnDisconnected -= HandleDisconnected;

            // Discard cold-start Reconnect credentials on graceful session end.
            // Process-shutdown paths pass keepReconnectCredentials=true so a relaunch can Reconnect.
            if (!keepReconnectCredentials)
                _reconnectCredentialsStore?.Clear();

            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);
            _unackedInputs.Clear();
            _sessionMagic = 0;
            _gameStartTime = 0;
            _localPlayerId = 0;
            Phase = SessionPhase.Disconnected;
            _sharedClock = default;
        }

        public void SetReady(bool ready)
        {
            var msg = _playerReadyCache;
            msg.PlayerId = _localPlayerId;
            msg.IsReady = ready;
            SendToServer(msg, DeliveryMethod.Reliable);
        }

        public void SendCommand(ICommand command)
        {
            // SD client: delegate to SendClientInput
            SendClientInput(command.Tick, command);
        }

        public void RequestCommandsForTick(int tick)
        {
            // Not needed in SD mode
        }

        public void SendSyncHash(int tick, long hash)
        {
            // no-op: compared against server hash via local resimulation
        }

        public void SetLocalTick(int tick) { _localTick = tick; }

        public void ClearOldData(int tick)
        {
            // no-op: _syncHashes unused in SD, resend queue cleaned up via InputAckMessage
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
            using (var serialized = _messageSerializer.SerializePooled(msg))
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
        }

        // ── SD-only methods ──────────────────────────────────

        public void SendClientInput(int tick, ICommand command)
        {
            // Serialize command
            int cmdSize = command.GetSerializedSize();
            byte[] cmdBuf = StreamPool.GetBuffer(cmdSize);
            var cmdWriter = new SpanWriter(cmdBuf.AsSpan(0, cmdBuf.Length));
            command.Serialize(ref cmdWriter);

            var msg = _clientInputCache;
            msg.Tick = tick;
            msg.PlayerId = _localPlayerId;
            msg.CommandData = cmdBuf;
            msg.CommandDataLength = cmdWriter.Position;
            msg._sourceBuffer = null;

            // Unreliable send
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }

            // Store in resend queue (removed when server ACK is received)
            byte[] cmdCopy = new byte[cmdWriter.Position];
            Buffer.BlockCopy(cmdBuf, 0, cmdCopy, 0, cmdWriter.Position);
            if (_unackedInputs.Count == 0)
                _lastResendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _unackedInputs.Enqueue(new UnackedInput { Tick = tick, CommandData = cmdCopy });

            StreamPool.ReturnBuffer(cmdBuf);
        }

        public void SendFullStateRequest(int currentTick)
        {
            var msg = new FullStateRequestMessage { RequestTick = currentTick };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
            }
        }

        public void SendBootstrapReady(int playerId)
        {
            var msg = new PlayerBootstrapReadyMessage { PlayerId = playerId };
            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash)
        {
            throw new NotSupportedException("The client cannot call SendFullStateResponse.");
        }

        public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast)
        {
            throw new NotSupportedException("The client cannot call BroadcastFullState.");
        }

        public void ClearUnackedInputs()
        {
            _unackedInputs.Clear();
        }

        public int GetMinClientAckedTick()
        {
            throw new NotSupportedException("The client cannot call GetMinClientAckedTick.");
        }

        // ── Update ──────────────────────────────────────────

        public void Update()
        {
            _transport?.PollEvents();

            UpdateReconnect();
            ResendUnackedInputs();

            if (Phase == SessionPhase.Countdown && _sharedClock.IsValid && _sharedClock.SharedNow >= _gameStartTime)
            {
                Phase = SessionPhase.Playing;
                SaveReconnectCredentialsIfApplicable();
                OnGameStart?.Invoke();
            }
        }

        public void FlushSendQueue()
        {
            _transport?.FlushSendQueue();
        }

        // ── Message handling ─────────────────────────────────────

        private void HandleDataReceived(int peerId, byte[] data, int length)
        {
            var message = _messageSerializer.Deserialize(data, length);
            if (message == null)
            {
                _logger?.KWarning($"[ServerDrivenClientService] Malformed payload from peerId={peerId}, length={length}");
                return;
            }

            switch (message)
            {
                case SyncRequestMessage syncReq:
                    HandleSyncRequest(syncReq);
                    break;

                case SyncCompleteMessage syncComplete:
                    HandleSyncComplete(syncComplete);
                    break;

                case GameStartMessage gameStart:
                    HandleGameStartMessage(gameStart);
                    break;

                case PlayerReadyMessage readyMsg:
                    HandlePlayerReadyMessage(readyMsg);
                    break;

                case VerifiedStateMessage verifiedMsg:
                    HandleVerifiedStateMessage(verifiedMsg);
                    break;

                case InputAckMessage ackMsg:
                    HandleInputAckMessage(ackMsg);
                    break;

                case LateJoinAcceptMessage lateJoinMsg:
                    HandleLateJoinAccept(lateJoinMsg);
                    break;

                case LateJoinNotificationMessage lateJoinNotification:
                    HandleLateJoinNotification(peerId, lateJoinNotification);
                    break;

                case FullStateResponseMessage fullStateMsg:
                    HandleFullStateResponse(fullStateMsg);
                    break;

                case ReconnectAcceptMessage reconnectAccept:
                    HandleReconnectAccept(reconnectAccept);
                    break;

                case ReconnectRejectMessage reconnectReject:
                    HandleReconnectReject(reconnectReject);
                    break;

                case PlayerConfigMessage playerConfigMsg:
                    HandlePlayerConfigMessage(playerConfigMsg);
                    break;

                case BootstrapBeginMessage bootBegin:
                    OnBootstrapBegin?.Invoke(bootBegin.FirstTick, bootBegin.TickStartTimeMs);
                    break;

                case CommandRejectedMessage rejectMsg:
                    _logger?.KInformation($"[SDClientService] CommandRejected received: tick={rejectMsg.Tick}, cmdTypeId={rejectMsg.CommandTypeId}, reason={rejectMsg.ReasonEnum}");
                    OnCommandRejected?.Invoke(rejectMsg.Tick, rejectMsg.CommandTypeId, rejectMsg.ReasonEnum);
                    break;

                case RecommendedExtraDelayUpdateMessage extraDelayMsg:
                    HandleRecommendedExtraDelayUpdate(extraDelayMsg);
                    break;

                case PingMessage pingMsg:
                    HandlePingMessage(peerId, pingMsg);
                    break;
            }
        }

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

        private void HandlePlayerConfigMessage(PlayerConfigMessage msg)
        {
            // Deserialize ConfigData and store in the client engine
            var configMsg = _messageSerializer.Deserialize(msg.ConfigData, msg.ConfigData.Length) as Core.PlayerConfigBase;
            if (configMsg != null)
            {
                (_engine as KlothoEngine)?.HandlePlayerConfigReceived(msg.PlayerId, configMsg);
            }
        }

        private void HandleSyncRequest(SyncRequestMessage msg)
        {
            if (_sessionMagic == 0)
                _sessionMagic = msg.Magic;
            else if (msg.Magic != _sessionMagic)
                return;

            var reply = new SyncReplyMessage
            {
                Magic = msg.Magic,
                Sequence = msg.Sequence,
                Attempt = msg.Attempt,
                ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            using (var serialized = _messageSerializer.SerializePooled(reply))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Reliable);
            }
        }

        private void HandleSyncComplete(SyncCompleteMessage msg)
        {
            if (msg.Magic != _sessionMagic)
                return;

            _localPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            ApplyOrPendExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.Sync);
            Phase = SessionPhase.Synchronized;
            OnLocalPlayerIdAssigned?.Invoke(_localPlayerId);
        }

        private void HandleRecommendedExtraDelayUpdate(RecommendedExtraDelayUpdateMessage msg)
        {
            _engine?.ApplyExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.DynamicPush);
            _logger?.KInformation(
                $"[SDClientService][DynamicDelay] Update applied: newDelay={msg.RecommendedExtraDelay}, avgRtt={msg.AvgRttMs}ms");
        }

        private void HandleGameStartMessage(GameStartMessage msg)
        {
            _logger?.KInformation($"[SDClientService] Game start: seed={msg.RandomSeed}, players={msg.PlayerIds.Count}");

            // Apply server-authoritative SessionConfig fields in place. Engine and NetworkService
            // share the same SessionConfig reference, so mutating the instance propagates to both
            // readers automatically. Match-start one-shot; SessionConfig stays immutable afterward.
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

            int prevPlayerCount0 = _players.Count;
            bool prevAllReady0 = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < msg.PlayerIds.Count; i++)
            {
                _players.Add(new PlayerInfo
                {
                    PlayerId = msg.PlayerIds[i],
                    IsReady = true
                });
            }
            RaisePlayerCountIfChanged(prevPlayerCount0);
            RaiseAllPlayersReadyIfChanged(prevAllReady0);

            _randomSeed = msg.RandomSeed;

            if (msg.StartTime > 0)
            {
                _gameStartTime = msg.StartTime;
                Phase = SessionPhase.Countdown;
                OnCountdownStarted?.Invoke(msg.StartTime);
            }
            else
            {
                // No countdown: arm the initial-FullState routing flag now so the upcoming broadcast is
                // routed through HandleInitialFullStateReceived (the bootstrap-ready ack site). The
                // countdown path achieves the same via HandleCountdownStarted.
                (_engine as KlothoEngine)?.MarkExpectingInitialFullState();
                Phase = SessionPhase.Playing;
                SaveReconnectCredentialsIfApplicable();
                OnGameStart?.Invoke();
            }
        }

        private void HandlePlayerReadyMessage(PlayerReadyMessage msg)
        {
            var player = _players.Find(p => p.PlayerId == msg.PlayerId);
            if (player != null)
            {
                bool prevReady = AllPlayersReady;
                player.IsReady = msg.IsReady;
                RaiseAllPlayersReadyIfChanged(prevReady);
            }
        }

        private void HandleVerifiedStateMessage(VerifiedStateMessage msg)
        {
            // Deserialize confirmed inputs
            var commands = _commandFactory.DeserializeCommands(msg.ConfirmedInputsSpan);

            // Fire event to engine
            OnVerifiedStateReceived?.Invoke(msg.Tick, commands, msg.StateHash);
        }

        private void HandleInputAckMessage(InputAckMessage msg)
        {
            // Remove ticks before ACK from the resend queue
            while (_unackedInputs.Count > 0 && _unackedInputs.Peek().Tick <= msg.AckedTick)
                _unackedInputs.Dequeue();

            OnInputAckReceived?.Invoke(msg.AckedTick);
        }

        private void HandleLateJoinAccept(LateJoinAcceptMessage msg)
        {
            _localPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);
            _randomSeed = msg.RandomSeed;

            // Rebuild player list
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < msg.PlayerCount && i < msg.PlayerIds.Count; i++)
            {
                var player = new PlayerInfo
                {
                    PlayerId = msg.PlayerIds[i],
                    IsReady = true
                };
                if (i < msg.PlayerConnectionStates.Count)
                    player.ConnectionState = (PlayerConnectionState)msg.PlayerConnectionStates[i];
                _players.Add(player);
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);

            OnLocalPlayerIdAssigned?.Invoke(_localPlayerId);
            _engine?.ExpectFullState();
            ApplyOrPendExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.LateJoin);
            _logger?.KInformation(
                $"[SDClientService] Late join accepted: playerId={msg.PlayerId}, playerCount={msg.PlayerCount}");
        }

        /// <summary>
        /// Server-broadcast notification that a new player completed late-join handshake. Adds the
        /// player to _players + fires PlayerCount / AllPlayersReady / OnPlayerJoined surfaces so UI
        /// stays consistent with the server's roster.
        /// </summary>
        private void HandleLateJoinNotification(int peerId, LateJoinNotificationMessage msg)
        {
            // SD client only talks to the server (peerId 0). Drop messages forged from any other source.
            if (peerId != 0)
            {
                _logger?.KWarning($"[SDClientService][HandleLateJoinNotification] Ignored from non-server peerId={peerId}");
                return;
            }

            // Duplicate notification (e.g. reliable retry path race) must not double-add.
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PlayerId == msg.PlayerId)
                {
                    _logger?.KDebug($"[SDClientService][HandleLateJoinNotification] Duplicate ignored: playerId={msg.PlayerId}");
                    return;
                }
            }

            // PlayerName mirrors the SD host's CompleteLateJoinSync construction ($"Player{id}") so
            // the same PlayerId reads identically across peers. Ping is unmeasurable on clients and
            // stays at default 0.
            var newPlayer = new PlayerInfo
            {
                PlayerId = msg.PlayerId,
                PlayerName = $"Player{msg.PlayerId}",
                IsReady = true,
                ConnectionState = PlayerConnectionState.Connected,
            };
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Add(newPlayer);
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);

            OnPlayerJoined?.Invoke(newPlayer);

            _logger?.KInformation($"[SDClientService][HandleLateJoinNotification] Late join player added: playerId={msg.PlayerId}, joinTick={msg.JoinTick}");
        }

        private void HandleFullStateResponse(FullStateResponseMessage msg)
        {
            if (_reconnectState == ReconnectState.WaitingForFullState)
            {
                _reconnectState = ReconnectState.None;
                OnReconnected?.Invoke();
            }

            (_engine as KlothoEngine)?.CheckStaticGeometryFingerprint(msg.StaticFingerprint);
            OnServerFullStateReceived?.Invoke(msg.Tick, msg.StateData, msg.StateHash);
        }

        // ── Connection events ─────────────────────────────────────

        private void HandleConnected()
        {
            if (_reconnectState == ReconnectState.WaitingForTransport)
            {
                // Reconnect mode — send ReconnectRequest
                _logger?.KInformation($"[SDClientService] Reconnect mode: sending ReconnectRequest");
                _reconnectState = ReconnectState.SendingRequest;
                SendReconnectRequest();
                return;
            }

            Phase = SessionPhase.Syncing;
            var joinMsg = new PlayerJoinMessage { DeviceId = GetDeviceId() };
            _logger?.KInformation($"[SDClientService] Connected to server, sending PlayerJoinMessage (deviceId='{joinMsg.DeviceId}')");

            using (var serialized = _messageSerializer.SerializePooled(joinMsg))
            {
                SendFirstMessage(serialized.Data, serialized.Length);
            }
        }

        private void HandleDisconnected(DisconnectReason reason)
        {
            bool reconnectEligible = reason == DisconnectReason.NetworkFailure
                                  || reason == DisconnectReason.ReconnectRequested;

            if (Phase == SessionPhase.Playing && reconnectEligible)
            {
                // Disconnected during game by network failure → attempt reconnect
                _reconnectState = ReconnectState.WaitingForTransport;
                _reconnectStartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _reconnectRetryCount = 0;
                OnReconnecting?.Invoke();
            }
            else
            {
                Phase = SessionPhase.Disconnected;
            }
        }

        // ── Reconnect ───────────────────────────────────────────

        private void HandleReconnectAccept(ReconnectAcceptMessage msg)
        {
            if (_reconnectState != ReconnectState.SendingRequest)
                return;

            _localPlayerId = msg.PlayerId;
            _sharedClock = new SharedTimeClock(msg.SharedEpoch, msg.ClockOffset);

            // Rebuild player list
            int prevPlayerCount = _players.Count;
            bool prevAllReady = AllPlayersReady;
            _players.Clear();
            for (int i = 0; i < msg.PlayerCount && i < msg.PlayerIds.Count; i++)
            {
                var player = new PlayerInfo { PlayerId = msg.PlayerIds[i] };
                if (i < msg.PlayerConnectionStates.Count)
                    player.ConnectionState = (PlayerConnectionState)msg.PlayerConnectionStates[i];
                _players.Add(player);
            }
            RaisePlayerCountIfChanged(prevPlayerCount);
            RaiseAllPlayersReadyIfChanged(prevAllReady);

            _reconnectState = ReconnectState.WaitingForFullState;
            ApplyOrPendExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.Reconnect);
            _logger?.KInformation($"[SDClientService] Reconnect accepted: playerId={msg.PlayerId}");
        }

        private void HandleReconnectReject(ReconnectRejectMessage msg)
        {
            if (_reconnectState == ReconnectState.None)
                return;

            _reconnectState = ReconnectState.Failed;
            Phase = SessionPhase.Disconnected;

            // Any reject reason invalidates persisted credentials — discard.
            _reconnectCredentialsStore?.Clear();

            OnReconnectFailed?.Invoke((ReconnectRejectReason)msg.Reason);
        }

        private void UpdateReconnect()
        {
            if (_reconnectState == ReconnectState.None)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long elapsed = now - _reconnectStartTimeMs;

            if (_sessionConfig != null && elapsed > _sessionConfig.ReconnectTimeoutMs)
            {
                _reconnectState = ReconnectState.Failed;
                Phase = SessionPhase.Disconnected;
                OnReconnectFailed?.Invoke(ReconnectRejectReason.TimedOut);
                return;
            }

            switch (_reconnectState)
            {
                case ReconnectState.WaitingForTransport:
                    if (_transport.IsConnected)
                    {
                        _reconnectState = ReconnectState.SendingRequest;
                        SendReconnectRequest();
                    }
                    else if (now - _lastTransportReconnectTime > TRANSPORT_RECONNECT_INTERVAL_MS)
                    {
                        _lastTransportReconnectTime = now;
                        if (!_transport.Connect(_transport.RemoteAddress, _transport.RemotePort))
                        {
                            _logger?.KError($"[SDClientService] Reconnect transport start failed — aborting reconnect");
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke(ReconnectRejectReason.TransportStartFailed);
                            return;
                        }
                    }
                    break;

                case ReconnectState.SendingRequest:
                    if (now - _reconnectRequestSentTime > RECONNECT_REQUEST_TIMEOUT_MS)
                    {
                        _reconnectRetryCount++;
                        if (_sessionConfig != null && _reconnectRetryCount > _sessionConfig.ReconnectMaxRetries)
                        {
                            _reconnectState = ReconnectState.Failed;
                            Phase = SessionPhase.Disconnected;
                            OnReconnectFailed?.Invoke(ReconnectRejectReason.MaxRetries);
                            return;
                        }
                        SendReconnectRequest();
                    }
                    break;

                case ReconnectState.WaitingForFullState:
                    // FullState is handled in HandleFullStateResponse → OnServerFullStateReceived
                    break;
            }
        }

        private void SendReconnectRequest()
        {
            var msg = new ReconnectRequestMessage
            {
                SessionMagic = _sessionMagic,
                PlayerId = _localPlayerId,
                DeviceId = GetDeviceId(),
            };
            _logger?.KInformation($"[SDClientService] Sending ReconnectRequestMessage (playerId={msg.PlayerId}, deviceId='{msg.DeviceId}')");

            using (var serialized = _messageSerializer.SerializePooled(msg))
            {
                SendFirstMessage(serialized.Data, serialized.Length);
            }
            _reconnectRequestSentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private void SendFirstMessage(byte[] data, int length)
        {
            if (_roomId >= 0)
            {
                SendRoomHandshake();
            }
            _transport.Send(0, data, length, DeliveryMethod.ReliableOrdered);
        }

        private void SendRoomHandshake()
        {
            _roomHandshakeCache.RoomId = _roomId;
            using (var serialized = _messageSerializer.SerializePooled(_roomHandshakeCache))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        // ── Catchup complete ─────────────────────────────────────

        private void HandleCatchupComplete()
        {
            // Reset _lastServerVerifiedTick on Late Join catchup complete
            // Called from engine's OnCatchupComplete
            // No prefill needed since InputDelayTicks = 0
        }

        // ── Input resend (single-packet bundling) ──────────────

        private void ResendUnackedInputs()
        {
            if (_unackedInputs.Count == 0 || _simConfig == null)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastResendTime < _simConfig.InputResendIntervalMs)
                return;

            _lastResendTime = now;

            // Warn if MaxUnackedInputs exceeded
            if (_unackedInputs.Count > _simConfig.MaxUnackedInputs)
            {
                _logger?.KWarning(
                    $"[SDClientService] Unacked inputs {_unackedInputs.Count} (limit: {_simConfig.MaxUnackedInputs})");
            }

            // Bundle all unacked inputs into a single packet and resend
            var bundle = _bundleCache;
            bundle.PlayerId = _localPlayerId;
            bundle.Count = _unackedInputs.Count;
            bundle.EnsureCapacity(bundle.Count);

            int idx = 0;
            foreach (var input in _unackedInputs)
            {
                bundle.Entries[idx].Tick = input.Tick;
                bundle.Entries[idx].CommandData = input.CommandData;
                bundle.Entries[idx].CommandDataLength = input.CommandData.Length;
                idx++;
            }

            using (var serialized = _messageSerializer.SerializePooled(bundle))
            {
                _transport.Send(0, serialized.Data, serialized.Length, DeliveryMethod.Unreliable);
            }
        }

        // ── Helpers ───────────────────────────────────────────

        private void SendToServer(INetworkMessage message, DeliveryMethod deliveryMethod)
        {
            using (var serialized = _messageSerializer.SerializePooled(message))
            {
                _transport.Send(0, serialized.Data, serialized.Length, deliveryMethod);
            }
        }

        // ── Internal types ───────────────────────────────────────

        private struct UnackedInput
        {
            public int Tick;
            public byte[] CommandData;
        }

        // ── Suppress unused event warnings ─────────────────────────

        internal void SuppressWarnings()
        {
            OnCountdownStarted?.Invoke(0);
            OnPlayerJoined?.Invoke(null);
            OnPlayerLeft?.Invoke(null);
            OnCommandReceived?.Invoke(null);
            OnDesyncDetected?.Invoke(0, 0, 0, 0);
            OnFrameAdvantageReceived?.Invoke(0, 0);
            OnFullStateRequested?.Invoke(0, 0);
            OnFullStateReceived?.Invoke(0, null, 0, FullStateKind.Unicast);
            OnPlayerDisconnected?.Invoke(null);
            OnPlayerReconnected?.Invoke(null);
            OnReconnectFailed?.Invoke(ReconnectRejectReason.Unknown);
            OnReconnected?.Invoke();
            OnLateJoinPlayerAdded?.Invoke(0, 0);
        }
    }
}
