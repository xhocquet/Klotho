using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.SdSample
{
    public class ScoreSystem : ISystem
    {
        static readonly FixedString32 ReasonTimeout = FixedString32.FromString("timeout");

        public void Update(ref Frame frame)
        {
            var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();
            int matchDurationMs = (stats.MatchDuration * FP64.FromInt(1000)).ToInt();
            int matchEndTick = matchDurationMs / frame.DeltaTimeMs;
            if (frame.Tick != matchEndTick) return;

            int winnerId = ResolveWinner(ref frame);
            var evt = EventPool.Get<GameOverEvent>();
            evt.WinnerPlayerId = winnerId;
            evt.Reason = ReasonTimeout;
            frame.EventRaiser.RaiseEvent(evt);
        }

        static int ResolveWinner(ref Frame frame)
        {
            int bestScore = int.MinValue;
            int bestId = -1;
            bool tie = false;
            var filter = frame.Filter<PlayerComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var p = ref frame.Get<PlayerComponent>(entity);
                if (p.Score > bestScore)
                {
                    bestScore = p.Score;
                    bestId = p.PlayerId;
                    tie = false;
                }
                else if (p.Score == bestScore)
                {
                    tie = true;
                }
            }
            return tie ? -1 : bestId;
        }
    }
}
