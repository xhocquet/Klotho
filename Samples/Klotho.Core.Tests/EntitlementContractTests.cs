using Xunit;

using xpTURN.Klotho.Core;          // PlayerConfigBase
using xpTURN.Klotho.Network;       // IdentityValidationOutcome, PlayerConfigVerdict(Kind), IPlayerConfigEntitlementGuard, ServerNetworkService
using xpTURN.Klotho.Serialization; // SpanWriter, SpanReader

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Contract tests for the public entitlement seams: the opaque entitlement carried on an accepted
    /// identity outcome, the player-config verdict factory shapes, and the server-side guard wiring.
    /// These lock the public API surface headlessly (the full join-flow / guard-fires behaviour needs the
    /// Unity harness that can drive the sync handshake).
    /// </summary>
    public sealed class EntitlementContractTests
    {
        // Minimal concrete PlayerConfigBase so the Clamp verdict can carry a real replacement reference.
        private sealed class StubPlayerConfig : PlayerConfigBase
        {
            public override NetworkMessageType MessageTypeId => (NetworkMessageType)200; // UserDefined range
            protected override void SerializeData(ref SpanWriter writer) { }
            protected override void DeserializeData(ref SpanReader reader) { }
        }

        // ── IdentityValidationOutcome: opaque entitlement carry ──────────────────────────────

        [Fact] // the 3-arg accept carries the entitlement reference verbatim onto an accepted outcome
        public void Accept_WithEntitlement_CarriesReference()
        {
            var ent = new byte[] { 1, 2, 3 };
            var outcome = IdentityValidationOutcome.Accept("acct", "Name", ent);

            Assert.True(outcome.Accepted);
            Assert.Equal("acct", outcome.Account);
            Assert.Equal("Name", outcome.DisplayName);
            Assert.Same(ent, outcome.Entitlement);
        }

        [Fact] // the legacy 2-arg accept leaves the entitlement null (no-entitlement path unchanged)
        public void Accept_WithoutEntitlement_NullEntitlement()
        {
            var outcome = IdentityValidationOutcome.Accept("acct", "Name");

            Assert.True(outcome.Accepted);
            Assert.Null(outcome.Entitlement);
        }

        [Fact] // a reject never carries an entitlement and is not accepted
        public void Reject_NotAccepted_NullEntitlement()
        {
            var outcome = IdentityValidationOutcome.Reject(9);

            Assert.False(outcome.Accepted);
            Assert.Null(outcome.Entitlement);
            Assert.Equal(9, outcome.RejectWireCode);
        }

        // ── PlayerConfigVerdict: factory shapes ──────────────────────────────────────────────

        [Fact]
        public void Pass_HasPassKind_NoReplacement()
        {
            var v = PlayerConfigVerdict.Pass();

            Assert.Equal(PlayerConfigVerdictKind.Pass, v.Kind);
            Assert.Null(v.Replacement);
            Assert.Equal(0, v.RejectWireCode);
        }

        [Fact] // clamp carries the server-chosen replacement on the verdict (no out-param)
        public void Clamp_CarriesReplacement()
        {
            var replacement = new StubPlayerConfig();
            var v = PlayerConfigVerdict.Clamp(replacement);

            Assert.Equal(PlayerConfigVerdictKind.Clamp, v.Kind);
            Assert.Same(replacement, v.Replacement);
        }

        [Fact]
        public void Reject_CarriesWireCode()
        {
            var v = PlayerConfigVerdict.Reject(11);

            Assert.Equal(PlayerConfigVerdictKind.Reject, v.Kind);
            Assert.Null(v.Replacement);
            Assert.Equal(11, v.RejectWireCode);
        }

        // ── ServerNetworkService seam wiring (public surface) ────────────────────────────────

        [Fact] // the guard setter is part of the public surface and accepts null (unset = passthrough)
        public void SetPlayerConfigEntitlementGuard_AcceptsNull()
        {
            var svc = new ServerNetworkService();
            svc.Initialize(new FakeTransport(), null, null);
            svc.CreateRoom("test", 4);

            svc.SetPlayerConfigEntitlementGuard(null); // no throw
        }

        [Fact] // an unknown player has no entitlement (no join → FindPlayerById null → null blob)
        public void GetPlayerEntitlement_UnknownPlayer_Null()
        {
            var svc = new ServerNetworkService();
            svc.Initialize(new FakeTransport(), null, null);
            svc.CreateRoom("test", 4);

            Assert.Null(svc.GetPlayerEntitlement(999));
        }

        // ── reliable-command entitlement gate (public surface) ───────────────────────────────

        [Fact] // Accept factory has the Accept kind
        public void ReliableCommandVerdict_Accept_HasAcceptKind()
        {
            var v = ReliableCommandVerdict.Accept();
            Assert.Equal(ReliableCommandVerdictKind.Accept, v.Kind);
        }

        [Fact] // Drop factory has the Drop kind
        public void ReliableCommandVerdict_Drop_HasDropKind()
        {
            var v = ReliableCommandVerdict.Drop();
            Assert.Equal(ReliableCommandVerdictKind.Drop, v.Kind);
        }

        [Fact] // the gate setter is part of the public surface and accepts null (unset = accept all)
        public void SetReliableCommandEntitlementGate_AcceptsNull()
        {
            var svc = new ServerNetworkService();
            svc.Initialize(new FakeTransport(), null, null);
            svc.CreateRoom("test", 4);

            svc.SetReliableCommandEntitlementGate(null); // no throw
        }
    }
}
