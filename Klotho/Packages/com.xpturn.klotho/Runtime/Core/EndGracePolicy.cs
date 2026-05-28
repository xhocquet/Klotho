namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Simulation behavior during the post-match grace window.
    /// Selected per session via ISessionConfig.EndGracePolicy.
    /// </summary>
    public enum EndGracePolicy
    {
        /// <summary>
        /// (Default) Simulation keeps advancing ticks. KlothoState remains Running.
        /// Input/heartbeat/replay continuity preserved. GameOverFired guard prevents
        /// further match-end firings on the game side.
        /// </summary>
        Continue,

        /// <summary>
        /// Simulation halts at the match-end tick. KlothoState transitions to Ending.
        /// ExecuteTick is blocked, input is buffered but not processed. Network send/recv
        /// is preserved (heartbeat/keepalive). Use when post-result physics drift or
        /// stray events would clutter the result screen.
        /// </summary>
        Pause,
    }
}
