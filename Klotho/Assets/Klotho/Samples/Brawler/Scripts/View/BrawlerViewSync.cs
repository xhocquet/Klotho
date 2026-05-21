using System;
using UnityEngine;

using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using Cysharp.Threading.Tasks;

namespace Brawler
{
    public class BrawlerViewSync : MonoBehaviour
    {
        [SerializeField] private PlatformView[] _movingPlatforms;
        [SerializeField] private GameHUD _gameHUD;
        [SerializeField] private ResultScreen _resultScreen;

        [Header("Camera")]
        [SerializeField] private BrawlerCameraController _cameraController;

        [Header("VFX")]
        [SerializeField] private GameObject _trapVfxPrefab;
        [SerializeField] private GameObject _bombVfxPrefab;

        public event Action OnLocalCharacterSpawned;
        public event Action OnLocalCharacterDespawned;

        private KlothoEngine _engine;
        private EcsSimulation _simulation;
        private EntityViewUpdater _evu;
        private ILogger _logger;

        private bool _platformsAssigned;

        public void Initialize(KlothoEngine engine, EcsSimulation simulation, EntityViewUpdater evu, ILogger logger = null)
        {
            _engine = engine;
            _simulation = simulation;
            _evu = evu;
            _logger = logger;

            _platformsAssigned = false;

            engine.OnTickExecuted += OnTickExecuted;
            engine.OnSyncedEvent  += OnSyncedEvent;

            if (_evu != null && _evu.PlayerViews != null)
            {
                _evu.PlayerViews.OnViewRegistered        += HandleViewRegistered;
                _evu.PlayerViews.OnLocalViewRegistered   += HandleLocalViewRegistered;
                _evu.PlayerViews.OnLocalViewUnregistered += HandleLocalViewUnregistered;
            }

            _gameHUD?.Initialize(engine);
            _resultScreen?.Initialize(engine);
        }

        public void Cleanup()
        {
            if (_engine != null)
            {
                _engine.OnTickExecuted -= OnTickExecuted;
                _engine.OnSyncedEvent  -= OnSyncedEvent;
            }

            if (_evu != null && _evu.PlayerViews != null)
            {
                _evu.PlayerViews.OnViewRegistered        -= HandleViewRegistered;
                _evu.PlayerViews.OnLocalViewRegistered   -= HandleLocalViewRegistered;
                _evu.PlayerViews.OnLocalViewUnregistered -= HandleLocalViewUnregistered;
            }

            _platformsAssigned = false;
            _engine = null;
            _simulation = null;
            _evu = null;
        }

        private void HandleViewRegistered(int playerId, EntityView view)
        {
            if (view is CharacterView ch)
                _gameHUD?.RegisterCharacterView(playerId, ch);
        }

        private void HandleLocalViewRegistered(EntityView view)
        {
            if (view is CharacterView ch)
            {
                _cameraController?.SetFollowTarget(ch.transform);
                OnLocalCharacterSpawned?.Invoke();
            }
        }

        private void HandleLocalViewUnregistered(EntityView view)
        {
            _cameraController?.ClearFollowTarget();
            OnLocalCharacterDespawned?.Invoke();
        }

        private void OnTickExecuted(int tick)
        {
            TryAssignPlatformEntities();
        }

        private void TryAssignPlatformEntities()
        {
            if (_platformsAssigned) return;
            if (_movingPlatforms == null || _movingPlatforms.Length == 0) return;
            if (_simulation == null) return;

            var frame = _simulation.Frame;

            int idx    = 0;
            var filter = frame.Filter<PlatformComponent, TransformComponent>();
            while (filter.Next(out var entity) && idx < _movingPlatforms.Length)
            {
                _movingPlatforms[idx]?.Initialize(_simulation, _engine);
                _movingPlatforms[idx]?.Assign(entity);
                idx++;
            }

            if (idx >= _movingPlatforms.Length)
                _platformsAssigned = true;
        }

        // ── Synced event: fires exactly once at the verified point ──
        // Events that tolerate delay (like Trap/Bomb) are promoted to Synced so duplicate dispatch is naturally blocked.

        private void OnSyncedEvent(int tick, SimulationEvent evt)
        {
            if (evt is TrapTriggeredEvent trap)
                OnTrapTriggered(trap);
            else if (evt is ItemPickedUpEvent pickup)
                OnItemPickedUp(pickup);
        }

        private void OnTrapTriggered(TrapTriggeredEvent evt)
        {
            var pos = new Vector3(evt.TrapPosition.x.ToFloat(), 0f, evt.TrapPosition.y.ToFloat());
            SpawnVfx(_trapVfxPrefab, pos).Forget();
        }

        private void OnItemPickedUp(ItemPickedUpEvent evt)
        {
            if (evt.ItemType == 2) // Bomb
            {
                var pos = new Vector3(evt.ItemPosition.x.ToFloat(), 0f, evt.ItemPosition.y.ToFloat());
                SpawnVfx(_bombVfxPrefab, pos).Forget();
            }
        }

        private static async UniTaskVoid SpawnVfx(GameObject prefab, Vector3 position)
        {
            if (prefab == null) return;
            var results = await InstantiateAsync(prefab, position, Quaternion.identity).ToUniTask();
            var ps = results[0].GetComponent<ParticleSystem>();
            if (ps != null && !ps.main.loop)
                Destroy(results[0], ps.main.duration + ps.main.startLifetime.constantMax);
            else
                Destroy(results[0], 3f);
        }

        private void OnDestroy()
        {
            Cleanup();
        }
    }
}
