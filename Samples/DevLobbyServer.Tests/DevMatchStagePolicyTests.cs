using Xunit;

using xpTURN.Samples.DevLobby; // DevMatchStagePolicy (the real selector, not a copy)

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// The dev lobby's stage selector. Two properties matter, for different reasons.
    /// <para>
    /// <b>Bot-spawn bound</b>: the stage doubles as the dedi's bot count, so a crafted matchId must not be able to
    /// drive it upward. The parse accepts ASCII digits only (<c>char.IsDigit</c> would accept fullwidth '３' U+FF13,
    /// whose distance from '0' is huge) and clamps to <c>MaxDevStage</c>.
    /// </para>
    /// <para>
    /// <b>Instance-id leak</b>: because the trailing character decides the stage, feeding a match
    /// INSTANCE id here would silently pick a different stage — half of all hex tokens end in '2'..'9'. The guard
    /// against that is <c>ReservePushInstanceIdTests.StagePolicy_ReceivesRendezvousMatchId_*</c>, which asserts what
    /// the coordinator passes in. The tests below only characterise the sensitivity that makes it dangerous.
    /// </para>
    /// </summary>
    public class DevMatchStagePolicyTests
    {
        [Theory] // the demo's own ids
        [InlineData("brawl-1", 1)]
        [InlineData("brawl-2", 2)]
        public void TrailingDigit_SelectsStage(string matchId, int expected)
            => Assert.Equal(expected, DevMatchStagePolicy.StageFor(matchId));

        [Theory] // no usable trailing digit → the default
        [InlineData("brawl")]
        [InlineData("brawl-0")]   // '0' is not a stage
        [InlineData("")]
        [InlineData(null)]
        public void NoTrailingDigit_DefaultsToStageOne(string matchId)
            => Assert.Equal(1, DevMatchStagePolicy.StageFor(matchId));

        [Fact] // the demo bakes Stage01/Stage02; widening this silently lets a matchId over-spawn bots
        public void MaxDevStage_IsTwo() => Assert.Equal(2, DevMatchStagePolicy.MaxDevStage);

        [Theory] // stage == botCount on the dedi, so this parse is an attack surface. Literal 2 on purpose:
        [InlineData("match-3")]  // asserting against MaxDevStage would be self-referential and could not
        [InlineData("match-5")]  // detect a widened clamp.
        [InlineData("match-9")]
        public void DigitPastTheLastStage_ClampsToTwo(string matchId)
            => Assert.Equal(2, DevMatchStagePolicy.StageFor(matchId));

        [Fact] // no ASCII digit may escape the bound, whatever the constant says
        public void EveryDigit_StaysWithinTheBakedStages()
        {
            for (char d = '0'; d <= '9'; d++)
            {
                int stage = DevMatchStagePolicy.StageFor("match-" + d);
                Assert.InRange(stage, 1, 2);
            }
        }

        [Theory] // char.IsDigit would accept these; 'fullwidth - ascii0' yields a huge value → unbounded bot spawn
        [InlineData("match-３")] // fullwidth THREE
        [InlineData("match-٩")] // arabic-indic NINE
        [InlineData("match-߉")] // nko NINE
        public void NonAsciiDigit_IsNotADigit(string matchId)
            => Assert.Equal(1, DevMatchStagePolicy.StageFor(matchId));

        // ── why leaking a match instance id here is dangerous ───────────────────
        [Fact]
        public void InstanceIdEndingInDigit_SilentlySelectsAnotherStage()
        {
            Assert.Equal(1, DevMatchStagePolicy.StageFor("brawl-1"));      // rendezvous key → correct
            Assert.Equal(2, DevMatchStagePolicy.StageFor("brawl-1#a2"));   // leaked instance id → wrong, and silent
        }

        [Fact]
        public void InstanceIdEndingInHexLetter_AccidentallyLooksCorrect() // ...which is what masks the bug
            => Assert.Equal(1, DevMatchStagePolicy.StageFor("brawl-1#ab"));
    }
}
