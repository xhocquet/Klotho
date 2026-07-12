using xpTURN.Klotho.Logging; // IKLogger

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// One participant's identity in a match-result report. Carried on the wire OUTSIDE the
    /// game-owned opaque result blob — the game blob stays pure deterministic stats keyed by PlayerId, and
    /// the lobby / backend joins these identities to it by PlayerId. Snapshot of the SD server's
    /// match-scoped identity ledger (includes players who already left; see SdRoomReporter).
    /// </summary>
    public struct MatchResultRosterEntry
    {
        public int PlayerId;
        public string Account;
        public string DisplayName;
    }

    /// <summary>
    /// Lobby-side seam for a verified match result (or abort notification) pushed by a dedicated
    /// server. The reference DevLobby implementation logs / stores in-proc; a real lobby forwards to
    /// its game backend (persistence, leaderboard, rewards). Called on the lobby's single-threaded poll loop
    /// — implementations MUST be fast and non-blocking (offload heavy backend work); a throw is isolated by
    /// the caller and withholds the ack so the dedicated server retries.
    /// <para>
    /// Ack contract: returning from <see cref="Submit"/> means the lobby has taken responsibility for the
    /// result (in-proc acceptance for the reference; durable-queue enqueue for production). The caller acks
    /// only after a clean return, marking the match instance processed for idempotent de-dup.
    /// </para>
    /// </summary>
    public interface IMatchResultSink
    {
        /// <summary>
        /// Accepts one match result. <paramref name="terminationKind"/> is <c>LobbyWire.TerminationNormalEnd</c>
        /// (payload = game result blob) or <c>LobbyWire.TerminationAborted</c> (payload = abort notification;
        /// no game result). <paramref name="roster"/> carries per-player identities (may be empty).
        /// Throw to signal the result was NOT accepted (the caller withholds the ack → dedi retry).
        /// <para>
        /// For <c>TerminationAborted</c>, decode the payload with <c>LobbyWire.TryDecodeAbortNotification</c>: its
        /// <c>abortReason</c> distinguishes an engine abort (e.g. <c>StateDivergence(2)</c>) from an ABANDONED
        /// match (<c>AbortReasonAbandoned(10)</c> — all peers left). ⚠️ <c>culpritPlayerId == -1</c>
        /// means different things per reason: "unknown who" for StateDivergence, but "no SINGLE culprit — the
        /// whole roster is responsible" for Abandoned. Do not treat -1 uniformly; gate leaver-penalty / match-void
        /// policy on <c>abortReason</c>. A never-started match is NOT reported as Abandoned (the dedi filters on
        /// DrainPhase == Playing); the lobby's reserve TTL reclaims those.
        /// </para>
        /// </summary>
        /// <param name="matchInstanceId">This match instance's unique key — <b>NOT the rendezvous matchId</b> the
        /// clients shared to meet in a room. A rendezvous matchId repeats across matches, so it cannot key a
        /// result; the lobby mints this when it binds the room. Format: <c>{rendezvousMatchId}#{token}</c>, or the
        /// bare rendezvous matchId when the lobby's ids are already unique. To join a backend record on the
        /// rendezvous key, take everything before the last <c>'#'</c>; if there is no <c>'#'</c>, the whole string
        /// is the rendezvous key. Do not assume the two are the same value.</param>
        void Submit(string serverId, string matchInstanceId, int roomId, int stageId,
                    byte terminationKind, MatchResultRosterEntry[] roster, byte[] payload);
    }

    /// <summary>
    /// Built-in reference <see cref="IMatchResultSink"/>: console/log observability for the dev lobby + tests.
    /// This is the default sink DevLobbyCore installs when no game backend is injected, and the single source of
    /// the accepted-result log format (a decorator like DevFailingMatchResultSink delegates here rather than
    /// re-formatting, so the fields stay in one place).
    /// </summary>
    public sealed class ReferenceLoggingSink : IMatchResultSink
    {
        private readonly IKLogger _logger;
        public ReferenceLoggingSink(IKLogger logger) => _logger = logger;

        public void Submit(string serverId, string matchInstanceId, int roomId, int stageId,
                           byte terminationKind, MatchResultRosterEntry[] roster, byte[] payload)
        {
            if (terminationKind == LobbyWire.TerminationAborted)
            {
                LobbyWire.TryDecodeAbortNotification(payload, out var ab);
                _logger?.KInformation($"[DevLobby] matchResult ABORT instance='{matchInstanceId}' server='{serverId}' room={roomId} stage={stageId} reason={ab.AbortReason} culprit={ab.CulpritPlayerId} roster={roster?.Length ?? 0}");
            }
            else
            {
                _logger?.KInformation($"[DevLobby] matchResult END instance='{matchInstanceId}' server='{serverId}' room={roomId} stage={stageId} blob={payload?.Length ?? 0}B roster={roster?.Length ?? 0}");
            }
        }
    }
}
