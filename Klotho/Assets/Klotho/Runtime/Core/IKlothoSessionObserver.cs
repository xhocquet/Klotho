using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Aggregates session-level lifecycle callbacks. Implemented by the game; consumed by KlothoSession
    /// to bulk-subscribe at Create and bulk-unsubscribe at Stop. Replaces the per-event += wiring
    /// previously spread across StartHost / JoinGameAsync / ReconnectAsync / StopGame sites.
    ///
    /// Default (no-op) implementations let games override only the callbacks they care about.
    /// </summary>
    public interface IKlothoSessionObserver
    {
        // ── NetworkService callbacks ──

        /// <summary>A remote player dropped from the session and is awaiting reconnect.</summary>
        void OnPlayerDisconnected(IPlayerInfo player) { }

        /// <summary>A previously-disconnected remote player resumed the session.</summary>
        void OnPlayerReconnected(IPlayerInfo player) { }

        /// <summary>The local client lost transport and started a reconnect attempt.</summary>
        void OnReconnecting() { }

        /// <summary>A local reconnect attempt failed. <paramref name="reason"/> is one of <see cref="ReconnectRejectReason"/> constants.</summary>
        void OnReconnectFailed(byte reason) { }

        /// <summary>A local reconnect attempt succeeded; transport is restored.</summary>
        void OnReconnected() { }

        // ── Engine callbacks ──

        /// <summary>Late-join or reconnect catchup finished; the local simulation chain is in sync.</summary>
        void OnCatchupComplete() { }

        /// <summary>A full-state resync completed at <paramref name="tick"/>; verified state replaced prediction.</summary>
        void OnResyncCompleted(int tick) { }

        /// <summary>The match transitioned into the Running state. Fires once per match start.</summary>
        void OnGameStart() { }

        /// <summary>The match terminated abnormally (chain stall, state divergence, reconnect failure, etc.).</summary>
        void OnMatchAborted(AbortReason reason) { }

        /// <summary>The match ended normally at <paramref name="tick"/>; <paramref name="endEvt"/> carries the winner and end reason.</summary>
        void OnMatchEnded(int tick, IMatchEndEvent endEvt) { }

        /// <summary>A corrective reset rolled the simulation back to a known-good state; the match continues.</summary>
        void OnMatchReset(ResetReason reason) { }

        /// <summary>
        /// Fired at the end of <c>KlothoSession.Stop()</c> after framework cleanup
        /// (Engine.Stop / NetworkService.LeaveRoom / observer unsubscribe). Game-side teardown
        /// (transport disconnect, session reference null-out, UI cleanup) should run here.
        /// </summary>
        void OnSessionStopped() { }
    }
}
