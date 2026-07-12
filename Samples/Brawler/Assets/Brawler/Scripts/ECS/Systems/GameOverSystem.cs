using System.Collections.Generic;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    /// <summary>
    /// In PostUpdate, detects the game-over condition and enqueues GameOverEvent.
    ///   - When a character with StockCount == 0 appears → the player with the highest stock count among the rest wins ("stocks")
    ///   - When the time limit is exceeded → the player with the most stocks wins; on a tie the lower PlayerId wins ("timeout")
    /// The event is emitted only once.
    /// </summary>
    public class GameOverSystem : ISystem
    {
        static readonly FixedString32 ReasonStocks  = FixedString32.FromString("stocks");
        static readonly FixedString32 ReasonTimeout = FixedString32.FromString("timeout");

        readonly EventSystem _events;
        readonly int _stageId; // stamped into the match result blob; the authoritative stageId also rides the wire

        public GameOverSystem(EventSystem events, int stageId = 0)
        {
            _events = events;
            _stageId = stageId;
        }

        public void Update(ref Frame frame)
        {
            ref var state = ref frame.GetSingleton<GameTimerStateComponent>();

            if (state.GameOverFired) return;

            if (state.StartTick < 0) state.StartTick = frame.Tick;

            if (TryStocksGameOver(ref frame, ref state)) return;
            TryTimeoutGameOver(ref frame, ref state);
        }

        // ────────────────────────────────────────────
        // Stock depletion detection
        // ────────────────────────────────────────────
        bool TryStocksGameOver(ref Frame frame, ref GameTimerStateComponent state)
        {
            // Expected participant count — engine-populated SessionParticipantComponent slot entities.
            // Fail-closed when zero (component not yet written) to avoid firing during bootstrap.
            int expected = 0;
            var slotFilter = frame.Filter<SessionParticipantComponent>();
            while (slotFilter.Next(out _)) expected++;
            if (expected == 0) return false;

            int presentCount = 0;
            int aliveCount  = 0;
            int winnerPlayerId = -1;
            int winnerStocks   = -1;

            var filter = frame.Filter<CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
                presentCount++;
                if (c.StockCount > 0)
                {
                    aliveCount++;
                    if (c.StockCount > winnerStocks ||
                       (c.StockCount == winnerStocks && c.PlayerId < winnerPlayerId))
                    {
                        winnerStocks   = c.StockCount;
                        winnerPlayerId = c.PlayerId;
                    }
                }
            }

            // Gate: all expected participants must have a CharacterComponent before judging stocks.
            if (presentCount < expected) return false;

            if (aliveCount > 1) return false;

            if (aliveCount == 0)
            {
                FireGameOver(ref frame, ref state, -1, ReasonStocks);
                return true;
            }

            FireGameOver(ref frame, ref state, winnerPlayerId, ReasonStocks);
            return true;
        }

        // ────────────────────────────────────────────
        // Timeout detection
        // ────────────────────────────────────────────
        void TryTimeoutGameOver(ref Frame frame, ref GameTimerStateComponent state)
        {
            var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>();
            int gameDurationMs = rules.GameDurationSeconds * 1000;

            int elapsedMs = (frame.Tick - state.StartTick) * frame.DeltaTimeMs;
            if (elapsedMs < gameDurationMs) return;

            // Find the player with the most stocks (lower PlayerId wins on a tie)
            int winnerPlayerId = -1;
            int winnerStocks   = -1;

            var filter = frame.Filter<CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (c.StockCount > winnerStocks ||
                   (c.StockCount == winnerStocks && c.PlayerId < winnerPlayerId))
                {
                    winnerStocks   = c.StockCount;
                    winnerPlayerId = c.PlayerId;
                }
            }

            FireGameOver(ref frame, ref state, winnerPlayerId, ReasonTimeout);
        }

        void FireGameOver(ref Frame frame, ref GameTimerStateComponent state, int winnerId, FixedString32 reason)
        {
            state.GameOverFired = true;

            // Persist the terminal state into the engine-read MatchEndStateComponent (its
            // winner/reason were previously held only on the event, lost on event-buffer wipe/clear). The
            // engine reads this for the fire-forward backstop and the Pause-grace gate. Single write point
            // keeps it in sync with the GameOverEvent fields.
            ref var matchEnd = ref frame.GetSingleton<MatchEndStateComponent>();
            matchEnd.Ended          = true;
            matchEnd.WinnerPlayerId = winnerId;

            var goEvt = EventPool.Get<GameOverEvent>();
            goEvt.WinnerPlayerId = winnerId;
            goEvt.Reason         = reason;
            // Assemble the game-owned result blob from verified state (always assign — a pooled reuse must
            // never surface a stale blob; the generated Reset does not clear this non-[KlothoOrder] field).
            goEvt.MatchResultData = AssembleResult(ref frame);
            _events.Enqueue(goEvt);
        }

        // Per-player stats + acquisitions keyed by PlayerId. Pure read-out of verified ECS state —
        // no identity (Account/DisplayName ride the wire roster side-channel). Runs once per match at match-end.
        byte[] AssembleResult(ref Frame frame)
        {
            var result = new BrawlerMatchResult { StageId = _stageId };
            var filter = frame.Filter<CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
                result.Players.Add(new BrawlerPlayerResult
                {
                    PlayerId            = c.PlayerId,
                    StockCount          = c.StockCount,
                    KnockbackTaken      = c.KnockbackPower,
                    AcquiredSkillMask   = c.AcquiredSkillMask,
                    OwnedConsumableMask = c.OwnedConsumableMask,
                });
            }
            AssignPlacements(result.Players);
            return result.ToBytes();
        }

        // 1-based placement by (StockCount desc, KnockbackTaken asc, PlayerId asc) — a strict total order via
        // PlayerId, so ranks are unique. Order-independent (counts strictly-better peers), hence stable
        // regardless of filter iteration order.
        internal static void AssignPlacements(List<BrawlerPlayerResult> players) // internal: unit-tested directly
        {
            for (int i = 0; i < players.Count; i++)
            {
                int rank = 1;
                var pi = players[i];
                for (int j = 0; j < players.Count; j++)
                {
                    if (i == j) continue;
                    if (RanksAbove(players[j], pi)) rank++;
                }
                pi.Placement = rank;
                players[i] = pi; // write back (value struct)
            }
        }

        static bool RanksAbove(in BrawlerPlayerResult a, in BrawlerPlayerResult b)
        {
            if (a.StockCount != b.StockCount) return a.StockCount > b.StockCount;
            if (a.KnockbackTaken != b.KnockbackTaken) return a.KnockbackTaken < b.KnockbackTaken;
            return a.PlayerId < b.PlayerId;
        }
    }
}
