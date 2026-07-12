using System;
using System.Buffers.Binary;
using System.Text;

using Xunit;

using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend, LobbyTicket
using xpTURN.Klotho.Samples.Identity.Sd;  // DevLobbyCore, LobbyWire, MatchResultRosterEntry, IMatchResultSink, ...

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// MatchResult(9)/MatchResultAck(10) wire round-trip (roster side-channel + terminationKind
    /// + abort-notification blob), the decoder OOB / sanity-cap guards, and the lobby-side
    /// <see cref="DevLobbyCore.HandleMatchResult"/> contract: idempotent matchId de-dup, ack-only-after-handoff,
    /// and sink-throw failure isolation (withhold ack → retry allowed).
    /// </summary>
    public sealed class MatchResultTests
    {
        // ── wire round-trip ──────────────────────────────────────────────────
        [Fact]
        public void MatchResult_RoundTrip_NormalEnd_WithRosterAndPayload()
        {
            var roster = new[]
            {
                new MatchResultRosterEntry { PlayerId = 1, Account = "acc-1", DisplayName = "Alice" },
                new MatchResultRosterEntry { PlayerId = 2, Account = "acc-2", DisplayName = "Bob" },
            };
            var payload = new byte[] { 9, 8, 7, 6, 5 };
            var buf = LobbyWire.EncodeMatchResult(42, "srv", 3, "match-7", 2,
                                                  LobbyWire.TerminationNormalEnd, roster, roster.Length, payload);

            Assert.Equal(LobbyWire.MatchResult, LobbyWire.PeekKind(buf, buf.Length));
            Assert.True(LobbyWire.TryDecodeMatchResult(buf, buf.Length, 1024, out var m));
            Assert.Equal(42, m.RequestId);
            Assert.Equal("srv", m.ServerId);
            Assert.Equal(3, m.RoomId);
            Assert.Equal("match-7", m.MatchInstanceId);
            Assert.Equal(2, m.StageId);
            Assert.Equal(LobbyWire.TerminationNormalEnd, m.TerminationKind);
            Assert.Equal(2, m.RosterCount);
            Assert.Equal(1, m.Roster[0].PlayerId);
            Assert.Equal("acc-1", m.Roster[0].Account);
            Assert.Equal("Alice", m.Roster[0].DisplayName);
            Assert.Equal("acc-2", m.Roster[1].Account);
            Assert.Equal("Bob", m.Roster[1].DisplayName);
            Assert.Equal(payload, m.Payload);
        }

        // The reporter encodes a MatchResult ONCE with requestId=0 (the journal's v0 marker), journals that image,
        // then patches the real requestId in place for the send buffer — instead of serializing twice. Pins that
        // the patched send buffer is byte-identical to a direct encode with the real id, and that the journal image
        // still decodes as requestId=0.
        [Fact]
        public void MatchResult_JournalEncodeZero_ThenPatchRequestId_IsByteIdenticalToDirectEncode()
        {
            var roster = new[]
            {
                new MatchResultRosterEntry { PlayerId = 1, Account = "acc-1", DisplayName = "Alice" },
            };
            var payload = new byte[] { 1, 2, 3 };

            // Loop thread encodes once with requestId=0 — exactly the bytes AppendJournal writes to disk (the v0 marker).
            byte[] wire = LobbyWire.EncodeMatchResult(0, "srv", 3, "match-7", 2,
                                                      LobbyWire.TerminationNormalEnd, roster, roster.Length, payload);
            Assert.True(LobbyWire.TryDecodeMatchResult(wire, wire.Length, 1024, out var journalMsg));
            Assert.Equal(0, journalMsg.RequestId); // on-disk journal record decodes as v0

            // Same buffer patched in place for the send path (offset 1, 4 bytes LE) — must equal a fresh encode with the id.
            BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(1), 42);
            byte[] direct = LobbyWire.EncodeMatchResult(42, "srv", 3, "match-7", 2,
                                                        LobbyWire.TerminationNormalEnd, roster, roster.Length, payload);
            Assert.Equal(direct, wire); // byte-identical: the second full serialization is eliminated
            Assert.True(LobbyWire.TryDecodeMatchResult(wire, wire.Length, 1024, out var sendMsg));
            Assert.Equal(42, sendMsg.RequestId); // send buffer carries the real requestId
        }

        [Fact]
        public void MatchResult_RoundTrip_Aborted_WithAbortNotification()
        {
            var roster = new[] { new MatchResultRosterEntry { PlayerId = 5, Account = "a", DisplayName = "d" } };
            byte[] payload = LobbyWire.EncodeAbortNotification(LobbyWire.AbortReasonStateDivergence, culpritPlayerId: 5);
            var buf = LobbyWire.EncodeMatchResult(1, "srv", 0, "m", 1,
                                                  LobbyWire.TerminationAborted, roster, roster.Length, payload);

            Assert.True(LobbyWire.TryDecodeMatchResult(buf, buf.Length, 1024, out var m));
            Assert.Equal(LobbyWire.TerminationAborted, m.TerminationKind);
            Assert.True(LobbyWire.TryDecodeAbortNotification(m.Payload, out var ab));
            Assert.Equal(LobbyWire.AbortReasonStateDivergence, ab.AbortReason);
            Assert.Equal(5, ab.CulpritPlayerId);
        }

        [Fact]
        public void MatchResult_RoundTrip_EmptyRoster_NullPayload()
        {
            var buf = LobbyWire.EncodeMatchResult(7, "s", 1, "m", 0,
                                                  LobbyWire.TerminationNormalEnd, null, 0, null);
            Assert.True(LobbyWire.TryDecodeMatchResult(buf, buf.Length, 1024, out var m));
            Assert.Equal(0, m.RosterCount);
            Assert.Empty(m.Roster);
            Assert.Null(m.Payload); // null → length 0 → decodes back to null
        }

        [Fact]
        public void MatchResultAck_RoundTrip()
        {
            var buf = LobbyWire.EncodeMatchResultAck(99, true);
            Assert.Equal(LobbyWire.MatchResultAck, LobbyWire.PeekKind(buf, buf.Length));
            Assert.True(LobbyWire.TryDecodeMatchResultAck(buf, buf.Length, out var m));
            Assert.Equal(99, m.RequestId);
            Assert.True(m.Ok);
        }

        [Fact]
        public void AbortNotification_RoundTrip_NoCulprit()
        {
            byte[] blob = LobbyWire.EncodeAbortNotification(LobbyWire.AbortReasonStateDivergence, culpritPlayerId: -1);
            Assert.True(LobbyWire.TryDecodeAbortNotification(blob, out var ab));
            Assert.Equal(LobbyWire.AbortReasonStateDivergence, ab.AbortReason);
            Assert.Equal(-1, ab.CulpritPlayerId);
            Assert.False(LobbyWire.TryDecodeAbortNotification(null, out _)); // null → false, no throw
        }

        // ── decoder OOB / sanity-cap guards ──────────────────────────────────
        [Fact]
        public void MatchResult_RosterCountOverCap_Rejected()
        {
            var roster = new[]
            {
                new MatchResultRosterEntry { PlayerId = 1, Account = "a", DisplayName = "d" },
                new MatchResultRosterEntry { PlayerId = 2, Account = "b", DisplayName = "e" },
            };
            var buf = LobbyWire.EncodeMatchResult(1, "s", 0, "m", 0,
                                                  LobbyWire.TerminationNormalEnd, roster, roster.Length, null);
            // maxRoster below the actual count → rejected by the sanity cap (same guard that catches a forged huge count).
            Assert.False(LobbyWire.TryDecodeMatchResult(buf, buf.Length, maxRoster: 1, out _));
            Assert.True(LobbyWire.TryDecodeMatchResult(buf, buf.Length, maxRoster: 2, out _)); // exactly-at-cap ok
        }

        [Fact]
        public void MatchResult_ForgedHugeRosterCount_Rejected_NoOverflow()
        {
            // Valid header + empty roster, then overwrite the rosterCount field with int.MaxValue: the pre-multiply
            // sanity cap rejects it before rosterCount * entrySize can overflow. Field offset is deterministic for
            // these single-char strings: kind(1)+reqId(4)+[len(4)+"s"]+roomId(4)+[len(4)+"m"]+stageId(4)+kind(1).
            var buf = LobbyWire.EncodeMatchResult(1, "s", 0, "m", 0,
                                                  LobbyWire.TerminationNormalEnd, null, 0, null);
            int off = 1 + 4 + (4 + Encoding.UTF8.GetByteCount("s")) + 4 + (4 + Encoding.UTF8.GetByteCount("m")) + 4 + 1;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off), int.MaxValue);
            Assert.False(LobbyWire.TryDecodeMatchResult(buf, buf.Length, maxRoster: 1024, out _)); // rejected, no overflow/throw escapes
        }

        [Fact]
        public void MatchResult_TruncatedBuffer_Rejected()
        {
            var roster = new[]
            {
                new MatchResultRosterEntry { PlayerId = 1, Account = "acc", DisplayName = "name" },
                new MatchResultRosterEntry { PlayerId = 2, Account = "acc2", DisplayName = "name2" },
            };
            var buf = LobbyWire.EncodeMatchResult(1, "s", 0, "m", 0,
                                                  LobbyWire.TerminationNormalEnd, roster, roster.Length, new byte[] { 1, 2, 3 });
            // A length that cuts into the roster/payload → the real OOB guard / ReadString rejects (no throw escapes).
            Assert.False(LobbyWire.TryDecodeMatchResult(buf, buf.Length - 6, maxRoster: 1024, out _));
        }

        [Fact]
        public void MatchResult_WrongKind_PeekAndDecodeReject()
        {
            var ack = LobbyWire.EncodeMatchResultAck(1, true);
            Assert.NotEqual(LobbyWire.MatchResult, LobbyWire.PeekKind(ack, ack.Length));
            Assert.False(LobbyWire.TryDecodeMatchResult(ack, ack.Length, 1024, out _)); // kind mismatch → false
        }

        // ── DevLobbyCore.HandleMatchResult contract ──────────────────────────
        private readonly BcEd25519Backend _backend = new BcEd25519Backend();
        private long _now = 1_000_000;

        private const int SrvPeer = 1; // the registered dedi peer for "srv" (provenance P1)

        private DevLobbyCore NewCore(long backupTimeout = 1_000, long grace = 2_000)
        {
            var reg = new LobbyRoomRegistry();
            reg.AddServer("srv", "127.0.0.1", 7777, 2, 2);
            var core = new DevLobbyCore(SdDevLobby.CreateIssuer(_backend), _backend, SdDevIdentity.PublicKey,
                                        () => _now, 30_000, reg, backupTimeout, grace);
            core.HandleServerRegister("srv", "127.0.0.1", 7777, 2, 2, SrvPeer); // bind peer→server so results pass P1
            return core;
        }

        // Issue a match instance to "srv" via the real assign path → returns its generated instance id
        // (populates the provenance registry so a result for it passes P2).
        private string Issue(DevLobbyCore core, string matchId)
        {
            var r = core.TryAssign(matchId, matchId + "-nonce", _now + 300_000);
            Assert.True(r.Ok);
            return r.InstanceId;
        }

        private sealed class CapturingSink : IMatchResultSink
        {
            public int Count;
            public string LastServerId, LastMatchId;
            public int LastRoomId, LastStageId;
            public byte LastKind;
            public MatchResultRosterEntry[] LastRoster;
            public byte[] LastPayload;
            public Func<Exception> Throw; // when non-null, throw AFTER counting the call

            public void Submit(string serverId, string matchId, int roomId, int stageId,
                               byte terminationKind, MatchResultRosterEntry[] roster, byte[] payload)
            {
                Count++;
                if (Throw != null) throw Throw();
                LastServerId = serverId; LastMatchId = matchId; LastRoomId = roomId; LastStageId = stageId;
                LastKind = terminationKind; LastRoster = roster; LastPayload = payload;
            }
        }

        private static MatchResultRosterEntry[] Roster1()
            => new[] { new MatchResultRosterEntry { PlayerId = 1, Account = "acc", DisplayName = "n" } };

        [Fact]
        public void HandleMatchResult_Accept_ReturnsTrue_And_ForwardsFields()
        {
            var core = NewCore();
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);

            string iid = Issue(core, "match-A");
            var payload = new byte[] { 1, 2, 3 };
            bool ack = core.HandleMatchResult(SrvPeer, "srv", iid, 3, 2, LobbyWire.TerminationNormalEnd, Roster1(), payload);

            Assert.True(ack);
            Assert.Equal(1, sink.Count);
            Assert.Equal("srv", sink.LastServerId);
            Assert.Equal(iid, sink.LastMatchId);
            Assert.Equal(3, sink.LastRoomId);
            Assert.Equal(2, sink.LastStageId);
            Assert.Equal(LobbyWire.TerminationNormalEnd, sink.LastKind);
            Assert.Single(sink.LastRoster);
            Assert.Equal(payload, sink.LastPayload);
        }

        [Fact]
        public void HandleMatchResult_DuplicateMatchId_SinkCalledOnce_BothAck()
        {
            var core = NewCore();
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);

            string iid = Issue(core, "dup");
            bool ack1 = core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);
            bool ack2 = core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);

            Assert.True(ack1);
            Assert.True(ack2);          // idempotent ack
            Assert.Equal(1, sink.Count); // forwarded only once
        }

        [Fact]
        public void HandleMatchResult_SinkThrows_WithholdsAck_AndDoesNotMark()
        {
            var core = NewCore();
            var sink = new CapturingSink { Throw = () => new InvalidOperationException("backend down") };
            core.SetMatchResultSink(sink);

            string iid = Issue(core, "retry-me");
            bool ack1 = core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);
            Assert.False(ack1);          // withhold ack → dedi retries
            Assert.Equal(1, sink.Count);

            // Not marked processed (and the issue record NOT consumed) → a retry re-invokes the sink (now healthy) and acks.
            sink.Throw = null;
            bool ack2 = core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);
            Assert.True(ack2);
            Assert.Equal(2, sink.Count);
        }

        [Fact]
        public void HandleMatchResult_EmptyMatchId_WithholdsAck()
        {
            var core = NewCore();
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);
            Assert.False(core.HandleMatchResult(SrvPeer, "srv", "", 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null)); // null guard precedes P1/P2
            Assert.Equal(0, sink.Count);
        }

        [Fact]
        public void HandleMatchResult_DefaultSink_LogsAndAcks()
        {
            var core = NewCore(); // no sink injected → built-in reference logging sink
            string iid = Issue(core, "m");
            Assert.True(core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationAborted,
                                               Roster1(), LobbyWire.EncodeAbortNotification(LobbyWire.AbortReasonStateDivergence, -1)));
        }

        // ── F5 provenance: P1 (peer↔serverId) + P2 (instance↔assigned serverId) ──
        [Fact]
        public void HandleMatchResult_UnregisteredPeer_Rejected_NoMark_GenuineStillAccepted()
        {
            var core = NewCore();
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);
            string iid = Issue(core, "match-P1");

            // a peer that is NOT the registered dedi for "srv" forges a result "as" srv → P1 rejects.
            bool forged = core.HandleMatchResult(peerId: 999, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);
            Assert.False(forged);
            Assert.Equal(0, sink.Count);              // not forwarded

            // the forgery did NOT mark the instance → the genuine dedi's result still gets through.
            bool genuine = core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);
            Assert.True(genuine);
            Assert.Equal(1, sink.Count);
        }

        [Fact]
        public void HandleMatchResult_InstanceIssuedToOtherServer_Rejected_NoMark_GenuineStillAccepted()
        {
            var reg = new LobbyRoomRegistry();
            reg.AddServer("srv", "127.0.0.1", 7777, 2, 2);
            reg.AddServer("srv2", "127.0.0.1", 7778, 2, 2);
            var core = new DevLobbyCore(SdDevLobby.CreateIssuer(_backend), _backend, SdDevIdentity.PublicKey,
                                        () => _now, 30_000, reg, 1_000, 2_000);
            core.HandleServerRegister("srv", "127.0.0.1", 7777, 2, 2, 1);   // peer 1 → srv (victim)
            core.HandleServerRegister("srv2", "127.0.0.1", 7778, 2, 2, 2);  // peer 2 → srv2 (attacker's own server)
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);

            string victim = core.TryAssign("match-V", "nonce-V", _now + 300_000).InstanceId; // issued to srv

            // attacker sends from its OWN registered server (P1 passes: peer2→srv2) but replays the victim's
            // instance id → P2 rejects (victim was issued to srv, not srv2). This is the vector P1 alone misses.
            bool forged = core.HandleMatchResult(2, "srv2", victim, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);
            Assert.False(forged);
            Assert.Equal(0, sink.Count);

            bool genuine = core.HandleMatchResult(1, "srv", victim, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null);
            Assert.True(genuine);
            Assert.Equal(1, sink.Count);
        }

        [Fact]
        public void HandleMatchResult_UnissuedInstance_Rejected()
        {
            var core = NewCore();
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);
            // never went through TryAssign → not in the provenance registry → P2 rejects.
            Assert.False(core.HandleMatchResult(SrvPeer, "srv", "never-issued#x", 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null));
            Assert.Equal(0, sink.Count);
        }

        [Fact]
        public void HandleMatchResult_IssueRecordTtlExpired_Rejected()
        {
            var core = NewCore(backupTimeout: 100_000_000, grace: 100_000_000); // server stays available across the jump
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);
            string iid = Issue(core, "match-TTL");

            _now += 30 * 60 * 1000 + 1; // jump past IssuedInstanceTtlMs
            core.Sweep(_now);            // prunes the provenance record

            // P1 still passes (server registered), but the issue record is gone → P2 rejects.
            Assert.False(core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null));
            Assert.Equal(0, sink.Count);
        }

        [Fact]
        public void HandleMatchResult_DupAfterProcessed_StillAcks_EvenAfterIssueRecordRemoved()
        {
            var core = NewCore();
            var sink = new CapturingSink();
            core.SetMatchResultSink(sink);
            string iid = Issue(core, "match-idem");

            // 1st: processed → marked + issue record consumed.
            Assert.True(core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null));
            // retry (ack lost): dedup MUST short-circuit to an idempotent ack — NOT be rejected by P2 for the now-gone
            // issue record (this pins P2 after the dedup check; §3.2 ordering).
            Assert.True(core.HandleMatchResult(SrvPeer, "srv", iid, 0, 0, LobbyWire.TerminationNormalEnd, Roster1(), null));
            Assert.Equal(1, sink.Count);
        }
    }
}
