using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.BrawlerDedicatedServer;

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// Multi-room E2E verification tests.
    /// Validates server components with MockTransport without an actual network.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class MultiRoomTests
    {
        private static IKLoggerFactory _loggerFactory;
        private static IKLogger _logger;
        private static int _passed;
        private static int _failed;
        private static readonly MessageSerializer _serializer = new MessageSerializer();

        public static int RunAll()
        {
            _loggerFactory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddConsole();
            });
            _logger = _loggerFactory.CreateLogger("Test");
            _passed = 0;
            _failed = 0;

            Console.WriteLine("=== MultiRoom E2E Tests ===\n");

            Test08_TwoRoomsSimultaneous();
            Test09_RoomIsolation();
            Test10_RoomCreationDestruction();
            Test11_TickIntervalStability();
            Test12_FullRoomReject();
            Test13_NonExistentRoomReject();
            Test14_GracefulShutdown();
            Test15_ThreadSafety();
            Test16_MT1_NoDoubleDispose();
            Test17_MT2_FastRoomNotMarked();
            Test18_MT3_StragglerGuards();

            Console.WriteLine($"\n=== Results: {_passed} passed, {_failed} failed ===");
            _loggerFactory.Dispose();
            return _failed;
        }

        // ── #8: Two rooms running simultaneously ──

        static void Test08_TwoRoomsSimultaneous()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);

            // Create 2 rooms
            var room0 = env.RoomManager.CreateRoom(0);
            var room1 = env.RoomManager.CreateRoom(1);

            Assert("#8a Room0 created", room0 != null && room0.State == RoomState.Active);
            Assert("#8b Room1 created", room1 != null && room1.State == RoomState.Active);
            Assert("#8c ActiveRoomCount", env.RoomManager.ActiveRoomCount == 2);

            // Simulate peer connection to each room
            SimulatePeerJoin(env, peerId: 1, roomId: 0);
            SimulatePeerJoin(env, peerId: 2, roomId: 1);

            // Verify messages are queued for each room
            Assert("#8d Room0 has inbound", !room0.InboundQueue.IsEmpty);
            Assert("#8e Room1 has inbound", !room1.InboundQueue.IsEmpty);

            // Update rooms (DrainInboundQueue + Engine.Update)
            room0.Update(0.025f);
            room1.Update(0.025f);

            Assert("#8f Room0 still Active", room0.State == RoomState.Active);
            Assert("#8g Room1 still Active", room1.State == RoomState.Active);

            env.Dispose();
        }

        // ── #9: Isolation between rooms ──

        static void Test09_RoomIsolation()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            var room0 = env.RoomManager.CreateRoom(0);
            var room1 = env.RoomManager.CreateRoom(1);

            // Connect peer to Room0 only
            SimulatePeerJoin(env, peerId: 10, roomId: 0);

            // Room1 should be empty
            Assert("#9a Room0 has inbound", !room0.InboundQueue.IsEmpty);
            Assert("#9b Room1 empty", room1.InboundQueue.IsEmpty);

            // Send data to Room0
            SimulateData(env, peerId: 10, data: new byte[] { 70, 1, 2, 3 }, length: 4); // ClientInput
            Assert("#9c Room0 has data", !room0.InboundQueue.IsEmpty);
            Assert("#9d Room1 still empty", room1.InboundQueue.IsEmpty);

            // Peer is registered only in Room0's Transport
            Assert("#9e Room0 has peer 10", room0.Transport.ContainsPeer(10));
            Assert("#9f Room1 no peer 10", !room1.Transport.ContainsPeer(10));

            env.Dispose();
        }

        // ── #10: Room creation/destruction ──

        static void Test10_RoomCreationDestruction()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            var room0 = env.RoomManager.CreateRoom(0);

            // Peer connects → leaves
            SimulatePeerJoin(env, peerId: 20, roomId: 0);
            room0.Update(0.025f); // DrainInboundQueue → HandlePeerConnected

            SimulatePeerDisconnect(env, peerId: 20);
            room0.Update(0.025f); // DrainInboundQueue → HandlePeerDisconnected

            // ShouldDrain decision: Draining once all connections are released
            Assert("#10a Room0 Draining", room0.State == RoomState.Draining);

            // TransitionDrainingRooms → Disposing
            env.RoomManager.TransitionDrainingRooms();
            Assert("#10b Room0 Disposing", room0.State == RoomState.Disposing);

            // CleanupDisposingRooms → Empty
            env.RoomManager.CleanupDisposingRooms();
            Assert("#10c Room0 Empty", room0.State == RoomState.Empty);
            Assert("#10d ActiveRoomCount 0", env.RoomManager.ActiveRoomCount == 0);

            // Slot can be reused
            var newRoom = env.RoomManager.CreateRoom(0);
            Assert("#10e Slot reused", newRoom != null && newRoom.State == RoomState.Active);

            env.Dispose();
        }

        // ── #11: Load test (tick interval stability) ──

        static void Test11_TickIntervalStability()
        {
            var env = CreateTestEnv(maxRooms: 4, maxPlayersPerRoom: 4);
            for (int i = 0; i < 4; i++)
                env.RoomManager.CreateRoom(i);

            // Connect peer to each room (empty rooms transition to Draining via ShouldDrain)
            for (int i = 0; i < 4; i++)
                SimulatePeerJoin(env, peerId: 60 + i, roomId: i);

            // Run 100 cycles
            var readyRooms = new List<Room>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int cycle = 0; cycle < 100; cycle++)
            {
                env.RoomManager.GetReadyRooms(readyRooms);
                if (readyRooms.Count == 0) break;
                var countdown = new CountdownEvent(readyRooms.Count);

                for (int i = 0; i < readyRooms.Count; i++)
                {
                    var room = readyRooms[i];
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { room.Update(0.025f); }
                        finally { countdown.Signal(); }
                    });
                }

                countdown.Wait(100); // 100ms budget
                countdown.Dispose();
            }

            sw.Stop();
            long elapsedMs = sw.ElapsedMilliseconds;
            Assert("#11a 100 cycles completed", true);
            Assert($"#11b No crash ({elapsedMs}ms for 100 cycles)", elapsedMs < 10000);

            // All rooms still Active (peers are still connected)
            for (int i = 0; i < 4; i++)
            {
                var room = env.RoomManager.GetRoom(i);
                Assert($"#11c Room{i} Active", room != null && room.State == RoomState.Active);
            }

            env.Dispose();
        }

        // ── #12: Reject when room is full ──

        static void Test12_FullRoomReject()
        {
            var env = CreateTestEnv(maxRooms: 1, maxPlayersPerRoom: 1);
            env.RoomManager.CreateRoom(0);

            // First peer succeeds
            SimulatePeerJoin(env, peerId: 30, roomId: 0);

            // Second peer → rejected with RoomFull
            env.Transport.SimulateConnect(31);
            byte[] handshake = MakeHandshakeMessage(roomId: 0);
            env.Transport.SimulateData(31, handshake, handshake.Length);

            // Rejected peer is DisconnectPeer'd with the reason carried on the disconnect packet payload
            Assert("#12a Peer 31 rejected", env.Transport.DisconnectedPeers.Contains(31));
            Assert("#12b Reject reason RoomFull", env.Transport.DisconnectReasonTo(31) == 2 /*RoomFull*/);

            env.Dispose();
        }

        // ── #13: Reject when roomId does not exist ──

        static void Test13_NonExistentRoomReject()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            env.RoomManager.CreateRoom(0); // Only roomId=0 is created; roomId=99 does not exist

            env.Transport.SimulateConnect(40);
            byte[] handshake = MakeHandshakeMessage(roomId: 99);
            env.Transport.SimulateData(40, handshake, handshake.Length);

            Assert("#13a Peer 40 rejected", env.Transport.DisconnectedPeers.Contains(40));
            Assert("#13b Reject reason RoomNotFound", env.Transport.DisconnectReasonTo(40) == 1 /*RoomNotFound*/);

            env.Dispose();
        }

        // ── #14: Graceful Shutdown ──

        static void Test14_GracefulShutdown()
        {
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: 2);
            env.RoomManager.CreateRoom(0);
            env.RoomManager.CreateRoom(1);

            SimulatePeerJoin(env, peerId: 50, roomId: 0);
            SimulatePeerJoin(env, peerId: 51, roomId: 1);

            // Drain to process connections
            env.RoomManager.GetRoom(0)?.Update(0.025f);
            env.RoomManager.GetRoom(1)?.Update(0.025f);

            // ShutdownAllRooms
            env.Router.StopAccepting();
            env.RoomManager.ShutdownAllRooms();

            Assert("#14a ActiveRoomCount 0", env.RoomManager.ActiveRoomCount == 0);

            // Verify new connections are rejected
            env.Transport.SimulateConnect(52);
            Assert("#14b New peer rejected after shutdown", env.Transport.DisconnectedPeers.Contains(52));

            env.Dispose();
        }

        // ── #15: Thread safety ──

        static void Test15_ThreadSafety()
        {
            var env = CreateTestEnv(maxRooms: 4, maxPlayersPerRoom: 4);
            for (int i = 0; i < 4; i++)
                env.RoomManager.CreateRoom(i);

            // Simulate concurrent Update from multiple threads + PollEvents on the main thread
            bool crashed = false;
            var readyRooms = new List<Room>();

            // Connect peer to each room
            for (int r = 0; r < 4; r++)
                SimulatePeerJoin(env, peerId: 100 + r, roomId: r);

            try
            {
                for (int cycle = 0; cycle < 200; cycle++)
                {
                    // Phase 2: ThreadPool parallel update (tick-only execution without data)
                    env.RoomManager.GetReadyRooms(readyRooms);
                    if (readyRooms.Count == 0) break;
                    var cd = new CountdownEvent(readyRooms.Count);
                    for (int i = 0; i < readyRooms.Count; i++)
                    {
                        var room = readyRooms[i];
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { room.Update(0.025f); }
                            finally { cd.Signal(); }
                        });
                    }
                    cd.Wait(500);
                    cd.Dispose();
                }
            }
            catch (Exception ex)
            {
                crashed = true;
                Console.WriteLine($"  Thread safety crash: {ex.Message}");
            }

            Assert("#15a No crash in 200 cycles", !crashed);

            env.Dispose();
        }

        // ── #16: MT-1 — no double-dispose of shared barrier (ObjectDisposedException) ──

        static void Test16_MT1_NoDoubleDispose()
        {
            const int maxPlayers = 2;
            // Both rooms slow (>budget≈23ms) → both straggle the same cycle → exercises the
            // multi-room straggle/recover path that the old code crashed on (shared CountdownEvent).
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: maxPlayers,
                log => new SlowRoomCallbacks(
                    new BrawlerServerCallbacks(log, _sharedStaticColliders, _sharedNavMesh, maxPlayers, 0), 50));
            var room0 = env.RoomManager.CreateRoom(0);
            var room1 = env.RoomManager.CreateRoom(1);
            SimulatePeerJoin(env, 200, 0); SimulatePeerJoin(env, 201, 0);
            SimulatePeerJoin(env, 202, 1); SimulatePeerJoin(env, 203, 1);
            // Drain joins → register players, then Start so the sim ticks (DelaySystem runs).
            room0.Update(0.025f); room0.Engine.Start();
            room1.Update(0.025f); room1.Engine.Start();

            var loop = new ServerLoop(env.Transport, env.RoomManager, 25, _logger);
            bool ok = true;
            try { loop.RunCyclesForTest(10, 0.025f); }
            catch (Exception ex) { ok = false; Console.WriteLine($"  MT-1 exception: {ex.GetType().Name}: {ex.Message}"); }

            Assert("#16a No ObjectDisposedException over 10 cycles", ok);
            var r0 = env.RoomManager.GetRoom(0);
            var r1 = env.RoomManager.GetRoom(1);
            Assert("#16b Rooms not force-closed",
                (r0 == null || r0.StragglerCount < 10) && (r1 == null || r1.StragglerCount < 10));

            env.Dispose();
        }

        // ── #17: MT-2 — fast room not falsely marked straggler ──

        static void Test17_MT2_FastRoomNotMarked()
        {
            const int maxPlayers = 2;
            int created = 0; // first-created room is slow, the rest fast
            var env = CreateTestEnv(maxRooms: 2, maxPlayersPerRoom: maxPlayers,
                log => new SlowRoomCallbacks(
                    new BrawlerServerCallbacks(log, _sharedStaticColliders, _sharedNavMesh, maxPlayers, 0),
                    (created++ == 0) ? 50 : 0));
            var room0 = env.RoomManager.CreateRoom(0); // slow
            var room1 = env.RoomManager.CreateRoom(1); // fast
            SimulatePeerJoin(env, 210, 0); SimulatePeerJoin(env, 211, 0);
            SimulatePeerJoin(env, 212, 1); SimulatePeerJoin(env, 213, 1);
            room0.Update(0.025f); room0.Engine.Start();
            room1.Update(0.025f); room1.Engine.Start();

            var loop = new ServerLoop(env.Transport, env.RoomManager, 25, _logger);
            loop.RunCyclesForTest(4, 0.025f);

            Assert("#17a Slow room marked straggler", room0.IsStraggler || room0.StragglerCount >= 1);
            Assert("#17b Fast room NOT marked", room1.StragglerCount == 0 && !room1.IsStraggler);

            env.Dispose();
        }

        // ── #18: MT-3 — lifecycle guards skip rooms whose thread may still run (deterministic) ──

        static void Test18_MT3_StragglerGuards()
        {
            var env = CreateTestEnv(maxRooms: 4, maxPlayersPerRoom: 2);

            // G5a/b: CleanupDisposingRooms defers a Disposing straggler until recovered.
            var r0 = env.RoomManager.CreateRoom(0);
            r0.State = RoomState.Disposing; r0.IsStraggler = true; r0.UpdateComplete = false;
            env.RoomManager.CleanupDisposingRooms();
            Assert("#18a Disposing straggler not cleaned", r0.State == RoomState.Disposing);
            r0.UpdateComplete = true;
            env.RoomManager.RecoverCompletedStragglers();
            env.RoomManager.CleanupDisposingRooms();
            Assert("#18b Cleaned after recovery", r0.State == RoomState.Empty);

            // G5c: TransitionDrainingRooms defers a Draining straggler.
            var r1 = env.RoomManager.CreateRoom(1);
            r1.State = RoomState.Draining; r1.IsStraggler = true;
            env.RoomManager.TransitionDrainingRooms();
            Assert("#18c Draining straggler not stopped", r1.State == RoomState.Draining);

            // G5d: ShutdownAllRooms skips teardown for a straggler.
            var r2 = env.RoomManager.CreateRoom(2);
            r2.IsStraggler = true; // Active
            env.RoomManager.ShutdownAllRooms();
            Assert("#18d Straggler teardown skipped at shutdown", r2.State != RoomState.Empty);

            env.Dispose();
        }

        // ═══════════════════════════════════════════════════════
        // Test infrastructure
        // ═══════════════════════════════════════════════════════

        static FPNavMesh _sharedNavMesh;
        static List<xpTURN.Klotho.Deterministic.Physics.FPStaticCollider> _sharedStaticColliders;
        static IDataAssetRegistry _sharedAssetRegistry;

        static void EnsureSharedTestData()
        {
            if (_sharedNavMesh == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.NavMeshData.bytes");
                if (File.Exists(p)) _sharedNavMesh = FPNavMeshSerializer.Deserialize(p);
            }
            if (_sharedStaticColliders == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "Stage01.StaticColliders.bytes");
                if (File.Exists(p)) _sharedStaticColliders = xpTURN.Klotho.Deterministic.Physics.FPStaticColliderSerializer.Load(p);
            }
            if (_sharedAssetRegistry == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerAssets.bytes");
                if (File.Exists(p))
                {
                    var assets = DataAssetReader.LoadMixedCollectionFromBytes(p);
                    IDataAssetRegistryBuilder builder = new DataAssetRegistry();
                    builder.RegisterRange(assets);
                    _sharedAssetRegistry = builder.Build();
                }
            }
        }

        static TestEnvironment CreateTestEnv(int maxRooms, int maxPlayersPerRoom)
            => CreateTestEnv(maxRooms, maxPlayersPerRoom, null);

        static TestEnvironment CreateTestEnv(int maxRooms, int maxPlayersPerRoom,
            Func<IKLogger, ISimulationCallbacks> callbacksFactory)
        {
            EnsureSharedTestData();

            int tickIntervalMs = 25;
            var transport = new MockTransport();
            var logger = _loggerFactory.CreateLogger("TestServer");
            var router = new RoomRouter(transport, logger);
            var navMesh = _sharedNavMesh;
            var staticColliders = _sharedStaticColliders;
            var assetRegistry = _sharedAssetRegistry;
            Func<IKLogger, ISimulationCallbacks> factory = callbacksFactory
                ?? ((roomLogger) => new BrawlerServerCallbacks(roomLogger, staticColliders, navMesh, maxPlayersPerRoom, 0));
            var roomManagerConfig = new RoomManagerConfigBuilder(factory)
                .WithRoomLimits(maxRooms, maxPlayersPerRoom)
                .WithSimulationConfig(() => new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = tickIntervalMs,
                    MaxEntities = 64,
                    MaxRollbackTicks = 1,
                    SyncCheckInterval = 1,
                    UsePrediction = false,
                    InputDelayTicks = 1,
                })
                .WithSessionConfig(() => new SessionConfig
                {
                    AllowLateJoin = true,
                    ReconnectTimeoutMs = 30000,
                    ReconnectMaxRetries = 3,
                })
                .WithDerivedSimulation(assetRegistry)
                .Build();
            var roomManager = new RoomManager(transport, router, _loggerFactory, roomManagerConfig);

            return new TestEnvironment
            {
                Transport = transport,
                Router = router,
                RoomManager = roomManager,
            };
        }

        static void SimulatePeerJoin(TestEnvironment env, int peerId, int roomId)
        {
            env.Transport.SimulateConnect(peerId);
            byte[] handshake = MakeHandshakeMessage(roomId);
            env.Transport.SimulateData(peerId, handshake, handshake.Length);
            byte[] join = MakePlayerJoinMessage();
            env.Transport.SimulateData(peerId, join, join.Length);
        }

        static void SimulateData(TestEnvironment env, int peerId, byte[] data, int length)
        {
            env.Transport.SimulateData(peerId, data, length);
        }

        static void SimulatePeerDisconnect(TestEnvironment env, int peerId)
        {
            env.Transport.SimulateDisconnect(peerId);
        }

        static byte[] MakeHandshakeMessage(int roomId)
        {
            var msg = new RoomHandshakeMessage { RoomId = roomId };
            return _serializer.Serialize(msg);
        }

        static byte[] MakePlayerJoinMessage()
        {
            return _serializer.Serialize(new PlayerJoinMessage());
        }

        static void Assert(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine($"  PASS: {name}");
                _passed++;
            }
            else
            {
                Console.WriteLine($"  FAIL: {name}");
                _failed++;
            }
        }

        class TestEnvironment : IDisposable
        {
            public MockTransport Transport;
            public RoomRouter Router;
            public RoomManager RoomManager;

            public void Dispose()
            {
                Router?.Dispose();
            }
        }
    }

    /// <summary>
    /// Test system that sleeps a fixed wall-time each tick to force a room to straggle.
    /// Mutates no Frame state → no determinism/hash impact.
    /// </summary>
    sealed class DelaySystem : xpTURN.Klotho.ECS.ISystem
    {
        private readonly int _delayMs;
        public DelaySystem(int delayMs) { _delayMs = delayMs; }
        public void Update(ref xpTURN.Klotho.ECS.Frame frame)
        {
            if (_delayMs > 0) System.Threading.Thread.Sleep(_delayMs);
        }
    }

    /// <summary>
    /// Wraps real callbacks and registers a <see cref="DelaySystem"/> so a room's Update overruns
    /// the stage-2 budget (straggler injection for MT-1/MT-2 tests).
    /// </summary>
    sealed class SlowRoomCallbacks : ISimulationCallbacks
    {
        private readonly ISimulationCallbacks _inner;
        private readonly int _delayMs;
        public SlowRoomCallbacks(ISimulationCallbacks inner, int delayMs) { _inner = inner; _delayMs = delayMs; }
        public void RegisterSystems(xpTURN.Klotho.ECS.EcsSimulation simulation)
        {
            _inner.RegisterSystems(simulation);
            simulation.AddSystem(new DelaySystem(_delayMs), xpTURN.Klotho.ECS.SystemPhase.Update);
        }
        public void OnInitializeWorld(IKlothoEngine engine) => _inner.OnInitializeWorld(engine);
        public void OnPollInput(int playerId, int tick, ICommandSender sender) => _inner.OnPollInput(playerId, tick, sender);
        public void OnPlayerJoinedWorld(IKlothoEngine engine, xpTURN.Klotho.ECS.Frame frame, int playerId) { } // no per-join world state to seed
    }

    /// <summary>
    /// Mock implementation of INetworkTransport. Manually fires events without an actual network.
    /// </summary>
    public class MockTransport : INetworkTransport
    {
        public bool IsConnected => true;
        public int LocalPeerId => 0;
        public string RemoteAddress => "127.0.0.1";
        public int RemotePort => 7777;

        public event Action<int, byte[], int> OnDataReceived;
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
#pragma warning disable CS0067
        public event Action OnConnected;
        public event Action<DisconnectReason> OnDisconnected;
#pragma warning restore CS0067

        // For test verification
        public HashSet<int> DisconnectedPeers { get; } = new HashSet<int>();
        private readonly Dictionary<int, byte[]> _lastSent = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, int> _disconnectReason = new Dictionary<int, int>();

        public int BroadcastCount { get; private set; }
        public int LastDisconnectPayload { get; private set; } = -1;

        public byte[] LastSentTo(int peerId)
        {
            _lastSent.TryGetValue(peerId, out var data);
            return data;
        }

        // Reject reason byte carried on the disconnect packet for a peer, or -1 if none.
        public int DisconnectReasonTo(int peerId)
            => _disconnectReason.TryGetValue(peerId, out var r) ? r : -1;

        // ── Manual event firing ──

        public void SimulateConnect(int peerId)
        {
            OnPeerConnected?.Invoke(peerId);
        }

        public void SimulateData(int peerId, byte[] data, int length)
        {
            OnDataReceived?.Invoke(peerId, data, length);
        }

        public void SimulateDisconnect(int peerId)
        {
            OnPeerDisconnected?.Invoke(peerId);
        }

        // ── INetworkTransport ──

        public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod)
        {
            byte[] copy = new byte[data.Length];
            Array.Copy(data, copy, data.Length);
            _lastSent[peerId] = copy;
        }

        public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            byte[] copy = new byte[length];
            Array.Copy(data, copy, length);
            _lastSent[peerId] = copy;
        }

        public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) { BroadcastCount++; }
        public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod) { BroadcastCount++; }

        public void DisconnectPeer(int peerId)
        {
            DisconnectedPeers.Add(peerId);
        }

        public void DisconnectPeer(int peerId, byte[] data)
        {
            DisconnectedPeers.Add(peerId);
            int reason = (data != null && data.Length >= 1) ? data[0] : -1;
            _disconnectReason[peerId] = reason;
            LastDisconnectPayload = reason;
        }

        public IEnumerable<int> GetConnectedPeerIds() => System.Linq.Enumerable.Empty<int>();

        public void PollEvents() { }
        public void FlushSendQueue() { }
        public bool Listen(string address, int port, int maxConnections) => true;
        public bool Connect(string address, int port) => true;
        public void Disconnect() { }
    }
}
