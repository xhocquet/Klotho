using System.Text;
using NUnit.Framework;

using xpTURN.Klotho.Network;
using xpTURN.Klotho.Samples.Identity;

namespace xpTURN.Klotho.Samples.Identity.Tests
{
    /// <summary>
    /// P2P Ed25519 ticket validator unit tests. Drives the reference validator with the deterministic
    /// <see cref="FakeEd25519Backend"/> and a fixed clock/sessionId. Integration through the network
    /// harness (host self-validation, CompletePeerSync) lives in IdentityValidationTests. The fake is NOT
    /// real Ed25519 but exercises all verify-surrounding logic; real cross-engine byte-identity needs the
    /// BouncyCastle adapter.
    /// </summary>
    [TestFixture]
    public class P2pSignatureValidatorTests
    {
        private const string SID = "match-1";
        private const long NOW = 1_700_000_000_000L; // fixed injected clock (deterministic tests)

        private static byte[] Key(byte seed)
        {
            var k = new byte[32];
            for (int i = 0; i < 32; i++) k[i] = (byte)(seed + i);
            return k;
        }

        private static readonly byte[] LOBBY_KEY = Key(0x11);
        private static readonly byte[] WRONG_KEY = Key(0x77);

        private static DevLobbyTicketIssuer Issuer() => new DevLobbyTicketIssuer(new FakeEd25519Backend(), LOBBY_KEY);

        private static P2pEd25519IdentityValidator Validator(byte[] pub = null, long now = NOW, string sid = SID)
            => new P2pEd25519IdentityValidator(pub ?? LOBBY_KEY, sid, () => now, new FakeEd25519Backend());

        private static IdentityValidationOutcome Run(IPlayerIdentityValidator v, string ticket)
        {
            var req = new IdentityValidationRequest(ticket, "", 0L, 1, "", false, false, roomId: -1); // P2P: not room-scoped
            var handle = v.BeginValidate(req);
            Assert.IsTrue(handle.IsComplete, "P2P validator must complete synchronously");
            return handle.Outcome;
        }

        private static LobbyTicket Valid(string nonce, long? expiresAt = null, string account = "acc",
                                         string displayName = "Name", string sessionId = SID)
            => new LobbyTicket(account, displayName, sessionId, NOW - 1000, expiresAt ?? (NOW + 60_000), nonce);

        private static byte RejectCode(IdentityValidationOutcome o)
        {
            Assert.IsFalse(o.Accepted, "expected reject");
            return o.RejectWireCode;
        }

        // Valid ticket → Accept with the ticket's account/displayName.
        [Test]
        public void ValidTicket_Accepted()
        {
            var o = Run(Validator(), Issuer().Issue(Valid("n1")));
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual("acc", o.Account);
            Assert.AreEqual("Name", o.DisplayName);
        }

        // Tampered payload → IdentityInvalid(6).
        [Test]
        public void TamperedPayload_Rejected6()
        {
            string wire = Issuer().Issue(Valid("n2"));
            int dot = wire.IndexOf('.');
            // flip one char inside the payload segment to a different valid base64url char
            char c = wire[1];
            char flipped = c == 'A' ? 'B' : 'A';
            string tampered = wire.Substring(0, 1) + flipped + wire.Substring(2);
            Assert.AreNotEqual(wire, tampered);
            Assert.AreEqual(6, RejectCode(Run(Validator(), tampered)));
        }

        // Signed by a different key → IdentityInvalid(6).
        [Test]
        public void WrongKey_Rejected6()
        {
            // issuer signs with LOBBY_KEY; validator verifies with WRONG_KEY
            Assert.AreEqual(6, RejectCode(Run(Validator(pub: WRONG_KEY), Issuer().Issue(Valid("n3")))));
        }

        // Expired (expiresAt <= now, injected clock) → IdentityExpired(7).
        [Test]
        public void Expired_Rejected7()
        {
            var t = Valid("n4", expiresAt: NOW - 1); // already expired
            Assert.AreEqual(7, RejectCode(Run(Validator(), Issuer().Issue(t))));
        }

        // SessionId mismatch → IdentitySessionMismatch(8).
        [Test]
        public void SessionMismatch_Rejected8()
        {
            var t = Valid("n5", sessionId: "match-2"); // validator expects "match-1"
            Assert.AreEqual(8, RejectCode(Run(Validator(), Issuer().Issue(t))));
        }

        // Session-scoped replay: same nonce twice → 1st Accept, 2nd IdentityRejected(9).
        [Test]
        public void ReplayedNonce_SecondRejected9()
        {
            var v = Validator();          // reuse one validator instance (per-session nonce set)
            string wire = Issuer().Issue(Valid("n6"));
            Assert.IsTrue(Run(v, wire).Accepted, "first use accepts");
            Assert.AreEqual(9, RejectCode(Run(v, wire)), "second use is a replay");
        }

        // Empty ticket (provider unset guest) → IdentityRequired(10).
        [Test]
        public void EmptyTicket_Rejected10()
        {
            Assert.AreEqual(10, RejectCode(Run(Validator(), "")));
        }

        // Issuer determinism (one engine): same key+payload → byte-identical wire.
        [Test]
        public void Issue_Deterministic()
        {
            var issuer = Issuer();
            var t = Valid("n10");
            Assert.AreEqual(issuer.Issue(t), issuer.Issue(t));
        }

        // Verify-over-wire: foreign-format but validly-signed tickets verify (footgun proof).
        [Test]
        public void VerifyOverWire_ReorderedAndWhitespacedJson_Accepted()
        {
            // hand-crafted payload: keys reordered + extra whitespace; signed via IssueRaw
            string json = "{ \"nonce\":\"n12a\" , \"sessionId\": \"" + SID + "\",\n"
                        + "  \"displayName\":\"N\", \"account\" : \"acc\",\t"
                        + "\"expiresAt\":" + (NOW + 60_000) + ",\"issuedAt\": 100 }";
            string wire = Issuer().IssueRaw(Encoding.UTF8.GetBytes(json));
            var o = Run(Validator(), wire);
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual("acc", o.Account);
        }

        [Test]
        public void VerifyOverWire_UnknownExtraFields_Accepted()
        {
            // forward-compat: unknown keys (string/number/object) must be ignored, not rejected
            string json = "{\"account\":\"acc\",\"displayName\":\"N\",\"sessionId\":\"" + SID + "\","
                        + "\"issuedAt\":100,\"expiresAt\":" + (NOW + 60_000) + ",\"nonce\":\"n12b\","
                        + "\"region\":\"eu\",\"mmr\":1234,\"party\":{\"id\":\"p\",\"size\":3},\"flags\":[1,2,3]}";
            string wire = Issuer().IssueRaw(Encoding.UTF8.GetBytes(json));
            Assert.IsTrue(Run(Validator(), wire).Accepted);
        }

        // Malformed-input robustness: each case maps to IdentityInvalid(6) and never throws.
        [Test]
        public void Robustness_MalformedInputs_Reject6_NoThrow()
        {
            var v = Validator();
            var issuer = Issuer();

            // ① wrong segment count
            Assert.AreEqual(6, RejectCode(RunNoThrow(v, "noseparator")));        // 0 dots
            Assert.AreEqual(6, RejectCode(RunNoThrow(v, "a.b.c")));              // 2 dots

            // ② non-base64 segment
            Assert.AreEqual(6, RejectCode(RunNoThrow(v, "@@@@.@@@@")));

            // ③ signature length != 64
            string valid = issuer.Issue(Valid("n13c"));
            string payloadSeg = valid.Substring(0, valid.IndexOf('.'));
            string shortSig = payloadSeg + "." + LobbyTicketCodec.Base64UrlEncode(new byte[32]);
            Assert.AreEqual(6, RejectCode(RunNoThrow(v, shortSig)));

            // ④ valid signature over an unparseable (non-JSON) payload
            string badJson = issuer.IssueRaw(Encoding.UTF8.GetBytes("this is not json {"));
            Assert.AreEqual(6, RejectCode(RunNoThrow(v, badJson)));
        }

        private static IdentityValidationOutcome RunNoThrow(IPlayerIdentityValidator v, string ticket)
        {
            IdentityValidationOutcome outcome = default;
            Assert.DoesNotThrow(() => outcome = Run(v, ticket), "validator must never throw to the host loop");
            return outcome;
        }

        // Field bounds: account >62 UTF-8 B or empty → reject(6); long displayName tolerated.
        [Test]
        public void Account_TooLong_Rejected6()
        {
            var t = Valid("n14a", account: new string('a', 63)); // 63 ASCII bytes > 62
            Assert.AreEqual(6, RejectCode(Run(Validator(), Issuer().Issue(t))));
        }

        [Test]
        public void Account_Empty_Rejected6()
        {
            var t = Valid("n14b", account: ""); // signed empty account collides with no-lobby sentinel
            Assert.AreEqual(6, RejectCode(Run(Validator(), Issuer().Issue(t))));
        }

        [Test]
        public void Account_MultibyteOverBound_Rejected6()
        {
            // 21 Hangul chars × 3 UTF-8 bytes = 63 bytes > 62
            var t = Valid("n14c", account: new string('가', 21));
            Assert.AreEqual(6, RejectCode(Run(Validator(), Issuer().Issue(t))));
        }

        [Test]
        public void DisplayName_TooLong_AcceptedTruncationIsCoreConcern()
        {
            var t = Valid("n14d", displayName: new string('d', 63)); // long displayName tolerated
            var o = Run(Validator(), Issuer().Issue(t));
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual("acc", o.Account);
        }

        // ── Phase 5: expiry off-by-one + binding-edge + non-checked-field characterization ──
        // The accept rule is expiresAt > now (validator rejects on `ExpiresAt <= now`). Pin both sides
        // of the edge: exactly-now expires, one ms past now is still valid.

        // ── Entitlement carried in the signed ticket payload ─────────────────────────────────────

        private static IdentityValidationOutcome Reverify(P2pEd25519IdentityValidator v, string ticket)
            => ((IPropagatedTicketVerifier)v).ReverifyPropagatedTicket(ticket);

        private static LobbyTicket ValidEnt(string nonce, byte[] entitlement, long? expiresAt = null)
            => new LobbyTicket("acc", "Name", SID, NOW - 1000, expiresAt ?? (NOW + 60_000), nonce, entitlement);

        [Test] // join-time validation extracts the signed entitlement onto the accepted outcome
        public void ValidTicket_WithEntitlement_CarriesEntitlement()
        {
            var ent = new byte[] { 10, 20, 30 };
            var o = Run(Validator(), Issuer().Issue(ValidEnt("e1", ent)));
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual(ent, o.Entitlement);
        }

        [Test] // signature-only re-verification (the guest path) extracts the same entitlement bytes
        public void Reverify_WithEntitlement_CarriesEntitlement()
        {
            var ent = new byte[] { 1, 2, 3, 4 };
            var o = Reverify(Validator(), Issuer().Issue(ValidEnt("e2", ent)));
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual(ent, o.Entitlement);
        }

        [Test] // re-verification ignores expiry, so an expired ticket still yields its entitlement — this is
                // why host and guest extraction must be signature-only, to avoid a tick-0 seed divergence
        public void Reverify_ExpiredTicket_StillCarriesEntitlement()
        {
            var ent = new byte[] { 7, 7, 7 };
            string wire = Issuer().Issue(ValidEnt("e3", ent, expiresAt: NOW - 1)); // already expired
            // Join-time Evaluate would reject (expired, code 7)…
            Assert.AreEqual(7, RejectCode(Run(Validator(), wire)));
            // …but signature-only re-verify accepts and still carries the entitlement.
            var o = Reverify(Validator(), wire);
            Assert.IsTrue(o.Accepted);
            Assert.AreEqual(ent, o.Entitlement);
        }

        [Test] // entitlement is INSIDE the signed payload — changing it changes the signature (not appended unsigned)
        public void Entitlement_IsCoveredBySignature()
        {
            string wireA = Issuer().Issue(ValidEnt("e4a", new byte[] { 1 }));
            string wireB = Issuer().Issue(ValidEnt("e4b", new byte[] { 2 }));
            Assert.AreNotEqual(wireA, wireB); // differing entitlement → differing signed wire
        }

        [Test] // entitlement bytes round-trip through encode then parse
        public void Codec_Entitlement_RoundTrips()
        {
            var ent = new byte[] { 0, 255, 128, 42 };
            byte[] payload = LobbyTicketCodec.EncodePayload(ValidEnt("e5", ent));
            LobbyTicket parsed = LobbyTicketCodec.ParsePayload(payload);
            Assert.AreEqual(ent, parsed.Entitlement);
        }

        [Test] // an older ticket with no "entitlement" key parses to a null entitlement (forward compatible)
        public void Codec_OldTicketWithoutEntitlementKey_ParsesNull()
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                "{\"account\":\"acc\",\"displayName\":\"Name\",\"sessionId\":\"match-1\"," +
                "\"issuedAt\":1,\"expiresAt\":2,\"nonce\":\"old\"}");
            LobbyTicket parsed = LobbyTicketCodec.ParsePayload(payload);
            Assert.IsNull(parsed.Entitlement);
        }

        [Test] // an empty "entitlement" value parses to null, denoting an identity-only ticket
        public void Codec_EmptyEntitlement_ParsesNull()
        {
            byte[] payload = LobbyTicketCodec.EncodePayload(ValidEnt("e6", null));
            LobbyTicket parsed = LobbyTicketCodec.ParsePayload(payload);
            Assert.IsNull(parsed.Entitlement);
        }

        [Test] // expiresAt == now → expired (the comparison is inclusive: `<= now`)
        public void ExpiryBoundary_AtNow_Rejected7()
        {
            var t = Valid("nb1", expiresAt: NOW);
            Assert.AreEqual(7, RejectCode(Run(Validator(), Issuer().Issue(t))));
        }

        [Test] // expiresAt == now + 1 → strictly future → accepted
        public void ExpiryBoundary_OneMsAfterNow_Accepted()
        {
            var t = Valid("nb2", expiresAt: NOW + 1);
            Assert.IsTrue(Run(Validator(), Issuer().Issue(t)).Accepted);
        }

        [Test] // empty sessionId is just another mismatch against the expected match id → mismatch(8)
        public void EmptySessionId_Rejected8()
        {
            var t = Valid("nb3", sessionId: ""); // validator expects "match-1"
            Assert.AreEqual(8, RejectCode(Run(Validator(), Issuer().Issue(t))));
        }

        // Characterization (NOT a reject boundary): issuedAt is never read by the validator, so a
        // far-future issuedAt — and even expiresAt < issuedAt (malformed) — is accepted as long as
        // expiresAt > now. Documents the deliberate absence of an issuedAt check.
        [Test]
        public void IssuedAtInFuture_NotValidated_Accepted()
        {
            // issuedAt far ahead of expiresAt (malformed), expiry still valid → accepted
            var t = new LobbyTicket("acc", "Name", SID, NOW + 1_000_000, NOW + 60_000, "nb4");
            Assert.IsTrue(Run(Validator(), Issuer().Issue(t)).Accepted);
        }

        // Characterization: empty nonce is not special-cased — it is consumed like any nonce, so the
        // first use accepts and a replay rejects(9). (There is no "reject empty nonce" rule.)
        [Test]
        public void EmptyNonce_NotSpecial_AcceptedThenReplayRejected9()
        {
            var v = Validator();
            string wire = Issuer().Issue(Valid("")); // empty nonce
            Assert.IsTrue(Run(v, wire).Accepted, "empty nonce accepted on first use");
            Assert.AreEqual(9, RejectCode(Run(v, wire)), "empty nonce replays like any nonce");
        }
    }
}
