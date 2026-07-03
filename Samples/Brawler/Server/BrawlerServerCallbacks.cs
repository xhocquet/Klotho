using System.IO;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;


using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Serialization;
using Brawler;

namespace xpTURN.Klotho.BrawlerDedicatedServer
{
    public class BrawlerServerCallbacks : ISimulationCallbacks
    {
        private readonly IKLogger _logger;

        private readonly List<FPStaticCollider> _staticColliders;
        private readonly FPNavMesh _navMesh;
        private readonly List<IDataAsset> _dataAssets;

        private readonly int _maxPlayers;
        private readonly int _botCount;

        public BrawlerServerCallbacks(IKLogger logger,
                                        List<FPStaticCollider> staticColliders,
                                        FPNavMesh navMesh,
                                        int maxPlayers,
                                        int botCount,
                                        List<IDataAsset> dataAssets = null)
        {
            _logger = logger;
            _staticColliders = staticColliders;
            _navMesh = navMesh;
            _dataAssets = dataAssets;

            _maxPlayers = maxPlayers;
            _botCount = botCount;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            BotFSMSystem botFSMSystem = null;

            var query       = new FPNavMeshQuery(_navMesh, _logger);
            var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, _logger);
            var funnel      = new FPNavMeshFunnel(_navMesh, query, _logger);
            var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, _logger);
            agentSystem.SetAvoidance(new FPNavAvoidance());

            botFSMSystem = new BotFSMSystem(agentSystem);
            botFSMSystem.SetQuery(query);

            BrawlerSimSetup.RegisterSystems(simulation, _logger, _dataAssets, _staticColliders, botFSMSystem);
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            _logger?.KInformation($"[BrawlerServerCallbacks] OnInitializeWorld: seed={engine.RandomSeed}");
            BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            // no-op: ServerInputCollector handles network input collection
        }

        // A late-joiner enters the world at its join tick — seed its entitlement loadout the same way
        // tick-0 players are seeded (OnInitializeWorld), so a restricted (e.g. "guest") late-joiner is
        // gated in-match. The server's verified entitlement is authoritative; clients receive the same
        // bytes via the late-join propagation, so every node seeds identical state at the join tick.
        public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
        {
            BrawlerSimSetup.SeedOneLoadout(ref frame, engine, playerId);
        }
    }
}
