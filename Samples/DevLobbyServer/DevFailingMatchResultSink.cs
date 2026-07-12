#if DEBUG
using System;

using xpTURN.Klotho.Logging;              // IKLogger
using xpTURN.Klotho.Samples.Identity.Sd;  // IMatchResultSink, MatchResultRosterEntry, LobbyWire

namespace xpTURN.Samples.DevLobby
{
    /// <summary>
    /// DEV-ONLY (DEBUG builds): an <see cref="IMatchResultSink"/> DECORATOR that throws on the first N Submit
    /// calls and delegates to the wrapped sink afterwards. Wired via <c>--dev-sink-fail &lt;N&gt;</c> to exercise the
    /// retry/isolation path on a LIVE connection: a sink throw is isolated by <c>DevLobbyCore.HandleMatchResult</c>,
    /// the lobby withholds the ack and does NOT mark the match instance processed, so the dedicated server re-sends
    /// on its ~3s timer until this sink accepts — at which point the lobby acks and the dedi stops retrying.
    /// <para>
    /// On accept it logs a thin <c>[DEV-SINK]</c> marker (so the e2e can see the dev sink handled it) and delegates
    /// the actual result formatting to the inner sink — the accepted-result log format lives in ONE place
    /// (<see cref="ReferenceLoggingSink"/>), not copied here. NOT compiled into Release.
    /// </para>
    /// </summary>
    internal sealed class DevFailingMatchResultSink : IMatchResultSink
    {
        private readonly IMatchResultSink _inner;
        private readonly IKLogger _logger;
        private int _remainingFailures;

        public DevFailingMatchResultSink(int failCount, IMatchResultSink inner, IKLogger logger)
        {
            _remainingFailures = failCount;
            _inner = inner;
            _logger = logger;
        }

        public void Submit(string serverId, string matchInstanceId, int roomId, int stageId,
                           byte terminationKind, MatchResultRosterEntry[] roster, byte[] payload)
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                // Throw → HandleMatchResult isolates it, withholds the ack, leaves the instance unmarked → dedi retries.
                throw new InvalidOperationException($"[DEV] simulated sink failure (remaining={_remainingFailures})");
            }

            // Thin marker (dev-sink accepted) + delegate the field formatting to the inner sink (no re-format).
            _logger?.KInformation($"[DevLobby][DEV-SINK] accepted instance='{matchInstanceId}' → delegating");
            _inner.Submit(serverId, matchInstanceId, roomId, stageId, terminationKind, roster, payload);
        }
    }
}
#endif
