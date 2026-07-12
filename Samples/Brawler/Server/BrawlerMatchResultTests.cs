using System;
using System.Collections.Generic;

using xpTURN.Klotho.Core; // IMatchResultProvider

using Brawler; // BrawlerMatchResult, BrawlerPlayerResult, GameOverEvent, GameOverSystem

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// Brawler custom result data: BrawlerMatchResult codec round-trip (per-player stats +
    /// acquisitions, no identity), the GameOverEvent → IMatchResultProvider seam, and the deterministic
    /// placement ranking.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class BrawlerMatchResultTests
    {
        private static int _passed;
        private static int _failed;

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            Console.WriteLine("\n=== BrawlerMatchResult Tests ===\n");

            // ── codec round-trip (multi-player, all fields) ──────────────────
            var r = new BrawlerMatchResult { StageId = 2 };
            r.Players.Add(new BrawlerPlayerResult { PlayerId = 1, Placement = 2, StockCount = 1, KnockbackTaken = 120, AcquiredSkillMask = 0b11, OwnedConsumableMask = 0b1 });
            r.Players.Add(new BrawlerPlayerResult { PlayerId = 2, Placement = 1, StockCount = 3, KnockbackTaken = 40, AcquiredSkillMask = 0b01, OwnedConsumableMask = 0b0 });

            var back = BrawlerMatchResultExtensions.FromBytes(r.ToBytes());
            Assert("StageId round-trip", back.StageId == 2);
            Assert("player count", back.Players.Count == 2);
            var p1 = Find(back.Players, 1);
            var p2 = Find(back.Players, 2);
            Assert("p1 stats", p1.Placement == 2 && p1.StockCount == 1 && p1.KnockbackTaken == 120 && p1.AcquiredSkillMask == 0b11 && p1.OwnedConsumableMask == 0b1);
            Assert("p2 stats", p2.Placement == 1 && p2.StockCount == 3 && p2.KnockbackTaken == 40 && p2.AcquiredSkillMask == 0b01 && p2.OwnedConsumableMask == 0b0);

            // ── empty roster round-trip ──────────────────────────────────────
            var empty = new BrawlerMatchResult { StageId = 0 };
            var eback = BrawlerMatchResultExtensions.FromBytes(empty.ToBytes());
            Assert("empty players round-trip", eback.Players.Count == 0);

            // ── GameOverEvent → IMatchResultProvider seam (server-side cast) ─
            byte[] blob = r.ToBytes();
            var evt = new GameOverEvent { WinnerPlayerId = 2, MatchResultData = blob };
            IMatchResultProvider prov = evt;
            Assert("cast provides the blob", ReferenceEquals(prov.MatchResultData, blob));
            var decoded = BrawlerMatchResultExtensions.FromBytes(prov.MatchResultData);
            Assert("decoded via cast", decoded.Players.Count == 2 && decoded.StageId == 2);
            // A GameOverEvent with no result set → cast yields null (opt-in, no-regression).
            IMatchResultProvider none = new GameOverEvent { WinnerPlayerId = 1 };
            Assert("null blob when unset", none.MatchResultData == null);

            // ── deterministic placement (stock desc, knockback asc, playerId asc) ──
            var players = new List<BrawlerPlayerResult>
            {
                new BrawlerPlayerResult { PlayerId = 1, StockCount = 1, KnockbackTaken = 200 }, // fewest stocks → last
                new BrawlerPlayerResult { PlayerId = 2, StockCount = 3, KnockbackTaken = 10 },  // tie stocks, less kb → 1st
                new BrawlerPlayerResult { PlayerId = 3, StockCount = 3, KnockbackTaken = 50 },  // tie stocks, more kb → 2nd
            };
            GameOverSystem.AssignPlacements(players);
            Assert("placement p2 = 1", Find(players, 2).Placement == 1);
            Assert("placement p3 = 2", Find(players, 3).Placement == 2);
            Assert("placement p1 = 3", Find(players, 1).Placement == 3);

            // Full stock+kb tie → broken by lower PlayerId (strict total order → unique ranks).
            var tie = new List<BrawlerPlayerResult>
            {
                new BrawlerPlayerResult { PlayerId = 7, StockCount = 2, KnockbackTaken = 50 },
                new BrawlerPlayerResult { PlayerId = 4, StockCount = 2, KnockbackTaken = 50 },
            };
            GameOverSystem.AssignPlacements(tie);
            Assert("tie broken by lower PlayerId", Find(tie, 4).Placement == 1 && Find(tie, 7).Placement == 2);

            Console.WriteLine($"\n=== BrawlerMatchResult results: {_passed} passed, {_failed} failed ===");
            return _failed;
        }

        static BrawlerPlayerResult Find(List<BrawlerPlayerResult> players, int playerId)
        {
            foreach (var p in players) if (p.PlayerId == playerId) return p;
            throw new InvalidOperationException($"player {playerId} not found");
        }

        static void Assert(string name, bool condition)
        {
            if (condition) { Console.WriteLine($"  PASS: {name}"); _passed++; }
            else           { Console.WriteLine($"  FAIL: {name}"); _failed++; }
        }
    }
}
