using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

using xpTURN.Klotho.Network;
using xpTURN.Klotho.Samples.Identity;     // FakeEd25519Backend, DevLobbyTicketIssuer, LobbyTicket
using xpTURN.Klotho.Samples.Identity.Sd;  // SdRedeemIdentityValidator, DevLobbyCore, InProcLobbyRedeemClient, ...

namespace xpTURN.Klotho.Samples.Identity.Tests
{
    /// <summary>
    /// SD redeem validator unit tests — drives the reference validator directly (no network harness) with
    /// the deterministic <see cref="FakeEd25519Backend"/>, a fixed clock, and either the real
    /// <see cref="DevLobbyCore"/> (via the in-proc fake) or a tiny hand-rolled redeem client for cases the
    /// core does not naturally produce (out-of-range code, oversize account, transport fault). Network
    /// integration (parking, drain, slot reservation, roster propagation, late join) lives in
    /// IdentityValidationTests. The fake backend is symmetric (public key == private key) and never leaves
    /// tests; cross-engine byte-identity needs the BouncyCastle adapter.
    /// </summary>
    [TestFixture]
    public class SdRedeemValidatorTests
    {
        private const string MATCH  = "sd-match-1";
        private const string SERVER = "sd-server-1";
        private const long   NOW    = 1_700_000_000_000L;
        private const long   WINDOW = 30_000;

        private long _now;

        [SetUp]
        public void SetUp() => _now = NOW;

        private static byte[] Key(byte seed)
        {
            var k = new byte[32];
            for (int i = 0; i < 32; i++) k[i] = (byte)(seed + i);
            return k;
        }

        private static readonly byte[] LOBBY_KEY = Key(0x11);

        private DevLobbyTicketIssuer Issuer() => new DevLobbyTicketIssuer(new FakeEd25519Backend(), LOBBY_KEY);

        // Registry pre-bound MATCH → (SERVER, room0) Active, mimicking a prior lobby assignment so redeem's
        // match-binding check passes (redeem consults the registry's match ledger, not a static map).
        private static LobbyRoomRegistry Registry()
        {
            var reg = new LobbyRoomRegistry();
            reg.AddServer(SERVER, "127.0.0.1", 7777, 2, 2);
            reg.MatchLedger[MATCH] = (SERVER, 0);
            reg.Servers[SERVER].Rooms[0].State = LobbyRoomRegistry.RoomState.Active;
            reg.Servers[SERVER].Rooms[0].SessionId = MATCH;
            return reg;
        }

        private DevLobbyCore Core() => new DevLobbyCore(
            Issuer(), new FakeEd25519Backend(), LOBBY_KEY, () => _now, WINDOW, Registry());

        private SdRedeemIdentityValidator Validator(ILobbyRedeemClient redeem, int timeoutMs = 2000, long skewMs = 0)
            => new SdRedeemIdentityValidator(LOBBY_KEY, new FakeEd25519Backend(), redeem, SERVER, () => _now, timeoutMs, skewMs);

        // A valid signed ticket bound to MATCH (issued with the lobby key the validator/core verify with).
        private string Ticket(string nonce, string account = "acc", string displayName = "Name",
                              long? expiresAt = null, string sessionId = MATCH)
            => Issuer().Issue(new LobbyTicket(account, displayName, sessionId, NOW - 1000, expiresAt ?? (NOW + 60_000), nonce));

        // Poll the handle to completion (mimics the core's per-tick drain). Generous budget; a miss is a
        // real failure, not flake.
        private static IdentityValidationOutcome Drive(IIdentityValidation h, int budgetMs = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (!h.IsComplete && sw.ElapsedMilliseconds < budgetMs) Thread.Sleep(2);
            Assert.IsTrue(h.IsComplete, "validation did not complete within budget");
            return h.Outcome;
        }

        private IdentityValidationOutcome Run(IPlayerIdentityValidator v, string ticket)
            => Drive(v.BeginValidate(new IdentityValidationRequest(ticket, "", 0L, 1, "", false, false, roomId: 0))); // bound room (Registry() binds MATCH→room0)

        private static byte RejectCode(IdentityValidationOutcome o)
        {
            Assert.IsFalse(o.Accepted, "expected reject");
            return o.RejectWireCode;
        }

        // ── valid path ──────────────────────────────────────────
        [Test]
        public void ValidTicket_RedeemAccepts()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var o = Run(Validator(redeem), Ticket("n1", account: "acc1", displayName: "Alice"));
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual("acc1", o.Account);
            Assert.AreEqual("Alice", o.DisplayName);
            Assert.AreEqual(1, redeem.CallCount);
        }

        // ── local 1st pass rejects without a redeem round-trip ────────────────────
        [Test]
        public void EmptyTicket_Rejected10_NoRedeem()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            Assert.AreEqual(10, RejectCode(Run(Validator(redeem), "")));
            Assert.AreEqual(0, redeem.CallCount, "empty ticket must not reach the lobby");
        }

        [Test]
        public void LocalTamper_Rejected6_NoRedeem()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            string wire = Ticket("n3");
            string tampered = (wire[0] == 'A' ? 'B' : 'A') + wire.Substring(1); // flip the first payload char to a guaranteed-different one
            Assert.AreEqual(6, RejectCode(Run(Validator(redeem), tampered)));
            Assert.AreEqual(0, redeem.CallCount, "a locally-rejected forgery must not reach the lobby");
        }

        [Test] // local expiry (lenient skew), redeem not called
        public void LocalExpiry_Rejected7_NoRedeem()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            string wire = Ticket("n15", expiresAt: NOW - 5_000); // well past now even with skew
            Assert.AreEqual(7, RejectCode(Run(Validator(redeem, skewMs: 2000), wire)));
            Assert.AreEqual(0, redeem.CallCount);
        }

        // ── redeem authority / clamp / bounds / fail-closed (hand-rolled redeem clients) ──
        [Test] // explicit redeem reject is forwarded
        public void RedeemReject_Forwarded9()
            => Assert.AreEqual(9, RejectCode(Run(Validator(new FixedRedeem(RedeemResult.Reject(9))), Ticket("n2r"))));

        [Test] // out-of-range redeem code clamped to 9
        public void RedeemCodeOutOfRange_ClampedTo9()
            => Assert.AreEqual(9, RejectCode(Run(Validator(new FixedRedeem(RedeemResult.Reject(2))), Ticket("n13"))));

        [Test] // redeem response wins over the ticket payload
        public void RedeemResponse_WinsOverTicketPayload()
        {
            var o = Run(Validator(new FixedRedeem(RedeemResult.Accept("Y", "DispY"))),
                        Ticket("n17", account: "X", displayName: "DispX"));
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual("Y", o.Account);
            Assert.AreEqual("DispY", o.DisplayName);
        }

        [Test]
        public void RedeemAccountTooLong_Rejected6()
            => Assert.AreEqual(6, RejectCode(Run(Validator(new FixedRedeem(RedeemResult.Accept(new string('a', 63), "d"))), Ticket("n10a"))));

        [Test]
        public void RedeemAccountEmpty_Rejected6()
            => Assert.AreEqual(6, RejectCode(Run(Validator(new FixedRedeem(RedeemResult.Accept("", "d"))), Ticket("n10b"))));

        [Test] // transport fault → fail closed
        public void RedeemThrows_FailClosed11()
            => Assert.AreEqual(11, RejectCode(Run(Validator(new ThrowRedeem()), Ticket("n8"))));

        // ── core-backed: match binding / nonce replay / idempotent recovery ────────
        [Test] // match not assigned to this server
        public void CrossMatch_Rejected8()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            string wire = Ticket("n9", sessionId: "other-match"); // not in the match→server map
            Assert.AreEqual(8, RejectCode(Run(Validator(redeem), wire)));
        }

        [Test] // same nonce beyond the idempotency window → replay reject
        public void NonceReplayBeyondWindow_Rejected9()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var v = Validator(redeem);
            string wire = Ticket("n4", expiresAt: NOW + 10_000_000);
            Assert.IsTrue(Run(v, wire).Accepted, "first redeem accepts");
            _now += WINDOW + 1; // advance past the idempotency window
            Assert.AreEqual(9, RejectCode(Run(v, wire)), "beyond-window replay is rejected");
        }

        [Test] // same ticket within the window → idempotent recovery (same accept)
        public void IdempotentRecoveryWithinWindow_BothAccept()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var v = Validator(redeem);
            string wire = Ticket("n5", account: "accR");
            var a = Run(v, wire);
            var b = Run(v, wire); // within window
            Assert.IsTrue(a.Accepted && b.Accepted);
            Assert.AreEqual("accR", b.Account);
        }

        // ── async / cross-thread ──────────────────────────────────────────────────
        [Test] // completion on a ThreadPool thread (genuine cross-thread barrier)
        public void CrossThreadCompletion_Accepts()
        {
            var gate = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.ManualGate);
            var v = Validator(gate);
            var h = v.BeginValidate(new IdentityValidationRequest(Ticket("n1x", account: "accB"), "", 0L, 1, "", false, false, roomId: 0));
            Assert.IsFalse(h.IsComplete, "pending until released");
            Assert.IsTrue(gate.ReleaseGatedOnThreadPool());
            var o = Drive(h);
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual("accB", o.Account);
        }

        [Test] // validator-internal timeout (hung redeem) → 11
        public void ValidatorInternalTimeout_Rejected11()
        {
            var hang = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Hang);
            var o = Run(Validator(hang, timeoutMs: 120), Ticket("n6a"));
            Assert.AreEqual(11, RejectCode(o));
        }

        [Test] // single-completion: Dispose(cancel) then a late reply must not overwrite the outcome
        public void SingleCompletion_CancelThenLateReply_StaysFailed()
        {
            var gate = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.ManualGate);
            var v = Validator(gate);
            var h = v.BeginValidate(new IdentityValidationRequest(Ticket("n14", account: "accH"), "", 0L, 1, "", false, false, roomId: 0));
            h.Dispose();                 // core timeout / disconnect cancels the in-flight redeem
            var o = Drive(h);            // continuation completes with 11
            gate.ReleaseGated();         // late reply — must be ignored
            Thread.Sleep(30);
            Assert.AreEqual(11, o.RejectWireCode);
            Assert.AreEqual(11, h.Outcome.RejectWireCode, "outcome must not change after completion");
        }

        // ── Phase 5: expiry off-by-one (local + lobby) · idempotency-window edge · binding/characterization ──

        [Test] // local 1st-pass expiry edge (skew 0): expiresAt == now → expired(7), no redeem round-trip
        public void LocalExpiryBoundary_AtNow_Rejected7_NoRedeem()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var v = Validator(redeem); // skew 0 → local check is `ExpiresAt <= now`
            Assert.AreEqual(7, RejectCode(Run(v, Ticket("nb1", expiresAt: NOW))));
            Assert.AreEqual(0, redeem.CallCount, "expired-at-now must not reach the lobby");
        }

        [Test] // one ms past now clears the local pass → the lobby (authority) decides → accept
        public void LocalExpiryBoundary_OneMsAfterNow_PassesToRedeem()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var v = Validator(redeem);
            Assert.IsTrue(Run(v, Ticket("nb2", expiresAt: NOW + 1)).Accepted);
            Assert.AreEqual(1, redeem.CallCount);
        }

        [Test] // skew lets a just-expired ticket clear the LOCAL pass, but the lobby rejects at == now
        public void LobbyExpiryBoundary_AtNow_LocalSkewPasses_LobbyRejects7()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var v = Validator(redeem, skewMs: 5_000); // local: ExpiresAt + 5000 > now → passes
            Assert.AreEqual(7, RejectCode(Run(v, Ticket("nb3", expiresAt: NOW)))); // lobby: NOW <= NOW → expired
            Assert.AreEqual(1, redeem.CallCount, "the local skew lets it reach the lobby; the lobby is the authority");
        }

        [Test] // exact idempotency-window edge: delta == window is still recovery (now - AtMs <= window)
        public void IdempotencyWindow_ExactEdge_Recovers()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var v = Validator(redeem);
            string wire = Ticket("nb4", account: "accW", expiresAt: NOW + 10_000_000); // expiry well past the window
            Assert.IsTrue(Run(v, wire).Accepted, "first redeem accepts");
            _now += WINDOW; // exactly at the window edge
            var o = Run(v, wire);
            Assert.IsTrue(o.Accepted, "delta == window is inclusive recovery");
            Assert.AreEqual("accW", o.Account);
        }

        [Test] // empty sessionId is not in the match→server map → mismatch(8)
        public void EmptySessionId_Rejected8()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            Assert.AreEqual(8, RejectCode(Run(Validator(redeem), Ticket("nb5", sessionId: ""))));
        }

        // Characterization (NOT a reject boundary): issuedAt is read by neither the local pass nor the
        // lobby — a far-future issuedAt (and expiresAt < issuedAt) is accepted while expiresAt > now.
        [Test]
        public void IssuedAtInFuture_NotValidated_Accepted()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var wire = Issuer().Issue(new LobbyTicket("acc", "Name", MATCH, NOW + 1_000_000, NOW + 60_000, "nb6"));
            Assert.IsTrue(Run(Validator(redeem), wire).Accepted);
        }

        // Characterization: empty nonce is consumed like any nonce — first accepts, beyond-window replay
        // rejects(9). (There is no "reject empty nonce" rule.)
        [Test]
        public void EmptyNonce_NotSpecial_AcceptedThenBeyondWindowRejected9()
        {
            var redeem = new InProcLobbyRedeemClient(Core(), InProcLobbyRedeemClient.Mode.Immediate);
            var v = Validator(redeem);
            string wire = Ticket("", expiresAt: NOW + 10_000_000); // empty nonce, expiry past the window
            Assert.IsTrue(Run(v, wire).Accepted, "empty nonce accepted on first use");
            _now += WINDOW + 1;
            Assert.AreEqual(9, RejectCode(Run(v, wire)), "empty nonce replays like any nonce");
        }

        // Fixed-outcome redeem client for cases DevLobbyCore does not naturally produce.
        private sealed class FixedRedeem : ILobbyRedeemClient
        {
            private readonly RedeemResult _r;
            public FixedRedeem(RedeemResult r) { _r = r; }
            public Task<RedeemResult> RedeemAsync(string ticket, string sessionId, string serverId, int roomId, CancellationToken ct)
                => Task.FromResult(_r);
        }

        private sealed class ThrowRedeem : ILobbyRedeemClient
        {
            public Task<RedeemResult> RedeemAsync(string ticket, string sessionId, string serverId, int roomId, CancellationToken ct)
                => Task.FromException<RedeemResult>(new System.InvalidOperationException("transport failure"));
        }
    }
}
