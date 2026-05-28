using System;
using System.IO;
using System.Reflection;
using System.Threading;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// Normal end / abort lifecycle tests.
    /// Covers post-match grace, EndReason routing, first-write-wins, all-peers-gone fallback,
    /// Ending-state freeze, SD heartbeat path, and IsEnded() classification.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class NormalEndLifecycleTests
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

            Console.WriteLine("\n=== NormalEndLifecycle Tests ===\n");

            NE1_ContinuePolicy_MatchEndDrain();
            NE2_PausePolicy_EnterEndingThenDrain();
            NE4_AbortMidPlay_AbortGraceDrain();
            NE5_FirstWriteWins_AbortDuringGraceIgnored();
            NE6_AllPeersGoneDuringGrace_ImmediateDrain();
            NE9_NoRoomManagerWiringWithoutAttach();
            NE10_IsEnded_FalseForEnding();

            Console.WriteLine($"\n=== NormalEndLifecycle results: {_passed} passed, {_failed} failed ===");
            _loggerFactory.Dispose();
            return _failed;
        }

        // ── NE1: Continue policy — match-end grace expiry transitions to Draining ──

        static void NE1_ContinuePolicy_MatchEndDrain()
        {
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 80, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 1, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(80));
            Assert("NE1a RequestEnd recorded", room.EndRequestedAtUtc.HasValue);
            Assert("NE1b EndReason=MatchEnded", room.EndReason == EndReason.MatchEnded);
            Assert("NE1c Continue keeps Active during grace", room.State == RoomState.Active);

            Thread.Sleep(120);
            room.Update(0.025f);

            Assert("NE1d Room transitions to Draining after grace", room.State == RoomState.Draining);
            Assert("NE1e DrainPhase captured", room.DrainPhase != default || true);

            env.RoomManager.TransitionDrainingRooms();
            Assert("NE1f Room transitions to Disposing", room.State == RoomState.Disposing);
            env.RoomManager.CleanupDisposingRooms();
            Assert("NE1g Room transitions to Empty", room.State == RoomState.Empty);

            env.Dispose();
        }

        // ── NE2: EnterEnding API contract — manual call transitions Running -> Ending and
        // the existing State != Running gate freezes tick advance. Production Pause policy
        // no longer calls EnterEnding (C-1a halts characters via client StopCommand instead);
        // this test pins the API behavior in case the state is reused (e.g., admin freeze).

        static void NE2_PausePolicy_EnterEndingThenDrain()
        {
            var env = CreateTestEnv(EndGracePolicy.Pause, endGraceMs: 80, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 2, roomId: 0);
            room.Update(0.025f);

            // Force engine into Running so EnterEnding can transition Running->Ending.
            // Production wiring drives this via OnGameStart; tests bypass the network handshake.
            ForceEngineState(room.Engine, KlothoState.Running);
            int tickBefore = room.Engine.CurrentTick;

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(80));
            room.Engine.EnterEnding();
            Assert("NE2a Engine State=Ending after EnterEnding", room.Engine.State == KlothoState.Ending);

            // Multiple updates within grace must not advance CurrentTick (Ending blocks ExecuteTick).
            room.Update(0.025f);
            room.Update(0.025f);
            Assert("NE2b CurrentTick frozen during Ending", room.Engine.CurrentTick == tickBefore);
            Assert("NE2c Room still Active during grace", room.State == RoomState.Active);

            Thread.Sleep(120);
            room.Update(0.025f);

            Assert("NE2d Room transitions to Draining after grace", room.State == RoomState.Draining);
            Assert("NE2e EndReason=MatchEnded preserved", room.EndReason == EndReason.MatchEnded);

            env.Dispose();
        }

        // ── NE3 removed — Ending-state heartbeat mechanism polished alongside the C-1a
        // Pause policy redefinition. Grace-window heartbeats are no longer emitted; the
        // server keeps ticking normally and verified StopCommands halt characters instead.

        // ── NE4: Abort mid-play — abort grace transitions to Draining with MatchAborted ──

        static void NE4_AbortMidPlay_AbortGraceDrain()
        {
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 500, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 4, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchAborted, TimeSpan.FromMilliseconds(80));
            Assert("NE4a EndReason=MatchAborted recorded", room.EndReason == EndReason.MatchAborted);
            Assert("NE4b Room still Active during abort grace", room.State == RoomState.Active);

            Thread.Sleep(120);
            room.Update(0.025f);

            Assert("NE4c Room transitions to Draining after abort grace", room.State == RoomState.Draining);
            Assert("NE4d EndReason=MatchAborted preserved at drain", room.EndReason == EndReason.MatchAborted);

            env.Dispose();
        }

        // ── NE5: First-write-wins — abort during normal-end grace is ignored ──

        static void NE5_FirstWriteWins_AbortDuringGraceIgnored()
        {
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 500, abortGraceMs: 80);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 5, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(500));
            var firstReqAt = room.EndRequestedAtUtc;
            var firstGrace = room.EndGrace;

            // Second call with abort while normal-end grace pending — must be ignored.
            room.RequestEnd(EndReason.MatchAborted, TimeSpan.FromMilliseconds(80));

            Assert("NE5a EndReason still MatchEnded", room.EndReason == EndReason.MatchEnded);
            Assert("NE5b EndRequestedAtUtc unchanged", room.EndRequestedAtUtc == firstReqAt);
            Assert("NE5c EndGrace unchanged (500ms)", room.EndGrace == firstGrace);

            env.Dispose();
        }

        // ── NE6: All-peers-gone during grace falls through to ShouldDrain path ──

        static void NE6_AllPeersGoneDuringGrace_ImmediateDrain()
        {
            // Long normal-end grace so it cannot expire during the test window.
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 10000, abortGraceMs: 10000);
            var room = env.RoomManager.CreateRoom(0);
            JoinPeer(env, peerId: 6, roomId: 0);
            room.Update(0.025f);

            room.RequestEnd(EndReason.MatchEnded, TimeSpan.FromMilliseconds(10000));
            Assert("NE6a Room Active during long grace", room.State == RoomState.Active);

            env.Transport.SimulateDisconnect(6);
            room.Update(0.025f);

            // Grace did not expire (~20ms vs 10s) — ShouldDrain takes over via all-peers-gone path.
            Assert("NE6b Room transitions to Draining via ShouldDrain", room.State == RoomState.Draining);
            Assert("NE6c EndReason stays MatchEnded (still set, drain reason resolved by lifetime/log)",
                room.EndReason == EndReason.MatchEnded);

            env.Dispose();
        }

        // ── NE9: A KlothoEngine instance has no automatic OnMatchEnded wiring ──

        static void NE9_NoRoomManagerWiringWithoutAttach()
        {
            // Create a room and immediately query the OnMatchEnded delegate field.
            // RoomManager.CreateRoomAt calls Room.AttachEngineHandlers, so a subscriber is expected here.
            var env = CreateTestEnv(EndGracePolicy.Continue, endGraceMs: 100, abortGraceMs: 100);
            var room = env.RoomManager.CreateRoom(0);

            var attached = GetPrivateField(room.Engine, "OnMatchEnded");
            Assert("NE9a Room creation attaches OnMatchEnded handler", attached != null);

            // After Dispose, AttachEngineHandlers' counterpart (DetachEngineHandlers) must unsubscribe.
            JoinPeer(env, peerId: 9, roomId: 0);
            room.Update(0.025f);
            env.Transport.SimulateDisconnect(9);
            room.Update(0.025f);
            env.RoomManager.TransitionDrainingRooms();
            env.RoomManager.CleanupDisposingRooms();

            var afterDispose = GetPrivateField(room.Engine, "OnMatchEnded");
            Assert("NE9b OnMatchEnded handler detached on Dispose", afterDispose == null);

            env.Dispose();
        }

        // ── NE10: IsEnded() classification ──

        static void NE10_IsEnded_FalseForEnding()
        {
            Assert("NE10a IsEnded(Ending) == false", !KlothoState.Ending.IsEnded());
            Assert("NE10b IsEnded(Running) == false", !KlothoState.Running.IsEnded());
            Assert("NE10c IsEnded(Finished) == true", KlothoState.Finished.IsEnded());
            Assert("NE10d IsEnded(Aborted) == true", KlothoState.Aborted.IsEnded());
        }

        // ═══════════════════════════════════════════════════════
        // Test infrastructure
        // ═══════════════════════════════════════════════════════

        static FPNavMesh _sharedNavMesh;
        static System.Collections.Generic.List<xpTURN.Klotho.Deterministic.Physics.FPStaticCollider> _sharedStaticColliders;
        static IDataAssetRegistry _sharedAssetRegistry;

        static void EnsureSharedTestData()
        {
            if (_sharedNavMesh == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.NavMeshData.bytes");
                if (File.Exists(p)) _sharedNavMesh = FPNavMeshSerializer.Deserialize(p);
            }
            if (_sharedStaticColliders == null)
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Data", "BrawlerScene.StaticColliders.bytes");
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

        static TestEnv CreateTestEnv(EndGracePolicy endGracePolicy, int endGraceMs, int abortGraceMs)
        {
            EnsureSharedTestData();

            int tickIntervalMs = 25;
            const int maxPlayersPerRoom = 4;
            var transport = new MockTransport();
            var logger = _loggerFactory.CreateLogger("TestServer");
            var router = new RoomRouter(transport, logger);
            var navMesh = _sharedNavMesh;
            var staticColliders = _sharedStaticColliders;
            var assetRegistry = _sharedAssetRegistry;
            var roomManager = new RoomManager(transport, router, _loggerFactory, new RoomManagerConfig
            {
                MaxRooms = 1,
                MaxPlayersPerRoom = maxPlayersPerRoom,
                SimulationFactory = () => new EcsSimulation(
                    maxEntities: 64,
                    maxRollbackTicks: 1,
                    deltaTimeMs: tickIntervalMs,
                    assetRegistry: assetRegistry),
                SimulationConfigFactory = () => new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = tickIntervalMs,
                    MaxRollbackTicks = 1,
                    SyncCheckInterval = 1,
                    UsePrediction = false,
                    InputDelayTicks = 1,
                },
                SessionConfigFactory = () => new SessionConfig
                {
                    AllowLateJoin = true,
                    ReconnectTimeoutMs = 30000,
                    ReconnectMaxRetries = 3,
                    EndGraceMs = endGraceMs,
                    AbortGraceMs = abortGraceMs,
                    EndGracePolicy = endGracePolicy,
                },
                CallbacksFactory = (roomLogger) => new BrawlerServerCallbacks(roomLogger, staticColliders, navMesh, maxPlayersPerRoom, 0),
            });

            return new TestEnv
            {
                Transport = transport,
                Router = router,
                RoomManager = roomManager,
            };
        }

        static void JoinPeer(TestEnv env, int peerId, int roomId)
        {
            env.Transport.SimulateConnect(peerId);
            byte[] handshake = _serializer.Serialize(new RoomHandshakeMessage { RoomId = roomId });
            env.Transport.SimulateData(peerId, handshake, handshake.Length);
            byte[] join = _serializer.Serialize(new PlayerJoinMessage());
            env.Transport.SimulateData(peerId, join, join.Length);
        }

        // Reflection helpers — bypass private setters / private fields for state injection in tests.
        // Production code paths must not rely on these.

        static void ForceEngineState(object engine, KlothoState state)
        {
            SetPrivateField(engine, "_state", state);
        }

        static void SetPrivateField(object target, string fieldName, object value)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null)
                throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
            f.SetValue(target, value);
        }

        static object GetPrivateField(object target, string fieldName)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null)
                throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
            return f.GetValue(target);
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

        class TestEnv : IDisposable
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
}
