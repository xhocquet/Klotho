using System;

using Brawler; // BrawlerMatchConfig

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// BrawlerMatchConfig codec round-trip: the per-match dynamic config (BotCount) serialized into the
    /// opaque SimulationConfig.MatchConfigData byte[] and restored on every peer.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class BrawlerMatchConfigTests
    {
        private static int _passed;
        private static int _failed;

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            Console.WriteLine("\n=== BrawlerMatchConfig Tests ===\n");

            // Round-trip preserves BotCount for a range of values.
            foreach (int n in new[] { 0, 1, 3, 4, 42 })
            {
                byte[] bytes = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = n });
                int back = BrawlerMatchConfig.Decode(bytes).BotCount;
                Assert($"roundtrip BotCount={n}", back == n);
            }

            // Null / empty / malformed buffers → default (BotCount = 0), so an unset MatchConfigData is a no-op.
            Assert("null → 0", BrawlerMatchConfig.Decode(null).BotCount == 0);
            Assert("empty → 0", BrawlerMatchConfig.Decode(Array.Empty<byte>()).BotCount == 0);
            Assert("malformed(<size) → 0", BrawlerMatchConfig.Decode(new byte[] { 1, 2 }).BotCount == 0);

            // Encode is exactly the struct's serialized size (one int32 = 4 bytes).
            Assert("Encode length == 4", BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 7 }).Length == 4);

            Console.WriteLine($"\n=== BrawlerMatchConfig results: {_passed} passed, {_failed} failed ===");
            return _failed;
        }

        static void Assert(string name, bool condition)
        {
            if (condition) { Console.WriteLine($"  PASS: {name}"); _passed++; }
            else           { Console.WriteLine($"  FAIL: {name}"); _failed++; }
        }
    }
}
