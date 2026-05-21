using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Maps playerId to EntityView for Owner-bearing views. EVU populates this from OwnerComponent
    /// on spawn / despawn — game code typically only subscribes to the events.
    ///
    /// Spectator mode reports LocalPlayerId=0 (KlothoEngine.LocalPlayerId fallback when networkService is null),
    /// which collides with the real host playerId=0. IsActuallyLocal disambiguates via IsSpectatorMode.
    /// </summary>
    public sealed class PlayerViewRegistry<TView> where TView : EntityView
    {
        private readonly Dictionary<int, TView> _views;
        private readonly IKlothoEngine _engine;

        public event Action<int, TView> OnViewRegistered;
        public event Action<int, TView> OnViewUnregistered;
        public event Action<TView>      OnLocalViewRegistered;
        public event Action<TView>      OnLocalViewUnregistered;

        public PlayerViewRegistry(IKlothoEngine engine, int capacity)
        {
            _engine = engine;
            _views = new Dictionary<int, TView>(capacity);
        }

        public TView Get(int playerId)
            => _views.TryGetValue(playerId, out var v) ? v : null;

        // Direct Dictionary.ValueCollection — struct enumerator, no boxing.
        public Dictionary<int, TView>.ValueCollection Values => _views.Values;
        public int Count => _views.Count;

        private bool IsActuallyLocal(int playerId)
            => !_engine.IsSpectatorMode && playerId == _engine.LocalPlayerId;

        internal void Register(int playerId, TView view)
        {
            if (view == null) return;
            if (_views.TryGetValue(playerId, out var existing) && existing == view)
            {
                _engine?.Logger?.ZLogDebug($"[ViewBind][Dedup] playerId={playerId}, viewIID={view.GetInstanceID()} (same instance, skip rebind)");
                return;
            }
            int prevIID = existing != null ? existing.GetInstanceID() : 0;
            _views[playerId] = view;

            bool isLocal = IsActuallyLocal(playerId);
            _engine?.Logger?.ZLogDebug($"[ViewBind][New] playerId={playerId}, viewIID={view.GetInstanceID()}, prevIID={prevIID}, isLocal={isLocal}");

            OnViewRegistered?.Invoke(playerId, view);
            if (isLocal) OnLocalViewRegistered?.Invoke(view);
        }

        internal void Unregister(int playerId, TView view)
        {
            if (_views.TryGetValue(playerId, out var current) && current != view)
            {
                _engine?.Logger?.ZLogDebug($"[ViewBind][UnregSkip] playerId={playerId}, requesterIID={view?.GetInstanceID()}, currentIID={current.GetInstanceID()}");
                return;
            }
            if (!_views.Remove(playerId))
            {
                _engine?.Logger?.ZLogDebug($"[ViewBind][UnregMiss] playerId={playerId} not in map");
                return;
            }

            bool isLocal = IsActuallyLocal(playerId);
            _engine?.Logger?.ZLogDebug($"[ViewBind][Unreg] playerId={playerId}, isLocal={isLocal}");

            OnViewUnregistered?.Invoke(playerId, view);
            if (isLocal) OnLocalViewUnregistered?.Invoke(view);
        }

        internal void Clear()
        {
            _views.Clear();
        }
    }
}
