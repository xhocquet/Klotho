namespace xpTURN.Samples.DevLobby
{
    /// <summary>
    /// DEV stage selection: the matchId's trailing ASCII digit picks the stage (…-1 → stage 1, …-2 → stage 2), so a
    /// distinct matchId demos a distinct stage regardless of which room the lobby assigns. No / '0' trailing digit
    /// → stage 1 (the default).
    /// <para>
    /// Kept apart from <c>Program.cs</c>'s payload assembly on purpose: this half is pure (no Brawler dependency),
    /// so the tests can compile and exercise the REAL selector rather than a copy of it. The stage feeds the dedi's
    /// bot count, so the parse is a small attack surface — see <see cref="StageFor"/>.
    /// </para>
    /// </summary>
    internal static class DevMatchStagePolicy
    {
        /// <summary>Brawler demo bakes Stage01/Stage02 — clamp so a crafted matchId can't over-spawn.</summary>
        internal const int MaxDevStage = 2;

        /// <summary>Stage for a RENDEZVOUS matchId. Never pass a match instance id: it ends in a hex token
        /// character, so half of all tokens end in '2'..'9' and would silently select another stage.</summary>
        internal static int StageFor(string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return 1;

            char last = matchId[matchId.Length - 1];
            // ASCII digits only — char.IsDigit accepts ANY Unicode decimal digit (e.g. fullwidth '３' U+FF13),
            // and 'fullwidth - ascii0' yields a huge value → huge stage/botCount → unbounded bot spawn on the dedi.
            if (last < '0' || last > '9') return 1;

            int d = last - '0';
            if (d < 1) return 1;
            return d < MaxDevStage ? d : MaxDevStage; // clamp: a digit past the demo's stage count caps at MaxDevStage
        }
    }
}
