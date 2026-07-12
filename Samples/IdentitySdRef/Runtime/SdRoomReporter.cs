using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using xpTURN.Klotho.Core;    // KlothoEngine, IMatchEndEvent, IMatchResultProvider, AbortReason
using xpTURN.Klotho.Logging; // IKLogger
using xpTURN.Klotho.Network; // RoomManager, Room, RoomState, ServerNetworkService, IPlayerInfo

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// In-process dedicated-server room reporter (P1). Owns a <see cref="LiteNetLibLobbyReportClient"/> and a
    /// background thread that periodically snapshots the live rooms and pushes a <c>roomReport</c> to the lobby.
    /// <c>serverRegister</c> is the report client's responsibility (sent on connect/reconnect); on reconnect
    /// the client calls back <see cref="OnLobbyConnected"/> so a fresh report follows promptly.
    /// <para>
    /// Core stays untouched: rooms are read via the public <c>GetRoom</c>/<c>State</c>/<c>PlayerCount</c>
    /// surface on this background thread, concurrently with the server loop. Reads are SCALARS ONLY (never
    /// enumerate <c>Players</c>) and defensive (null-check + try/catch → Empty/0) — a one-tick stale value is
    /// fine for this control-plane signal (eventual consistency; the next report corrects).
    /// </para>
    /// <para>
    /// Result propagation: when wired via <c>RoomManagerConfig.OnRoomCreated</c> (see <see cref="AttachRoom"/>),
    /// the reporter also captures a verified match result / abort notification on the room's engine thread and
    /// pushes it to the lobby as <c>MatchResult(9)</c>, flushed AHEAD of the roomReport so it beats the room's
    /// reclaim signal. Delivery is at-least-once (ack + retry, keyed by match instance) with a local
    /// append-only journal as the crash backstop.
    /// </para>
    /// </summary>
    public sealed class SdRoomReporter : IDisposable
    {
        private const int PollGranularityMs = 100; // fine enough for prompt reconnect (dirty) response

        private readonly RoomManager _roomManager;
        private readonly LiteNetLibLobbyReportClient _client;
        private readonly long _intervalMs;
        private readonly int _maxRooms;
        private readonly IKLogger _logger;
        private readonly RoomStateReport[] _snapshot; // reused (always _maxRooms entries)
        private readonly LobbyMatchConfigSource _reservations; // null = lobbyless (no reserve table to release)
        private readonly byte[] _prevState; // last-reported per-room wire state (RoomStateEmpty=0 default = init Empty)
        private readonly string _serverId;

        // Per-value WARN latch for the RoomState→wire mapping: the mapping is pure/static (no logging, so it
        // stays unit-testable), and the caller warns ONCE per unmapped value here — TryMapRoomState runs per-room
        // per-report, so an un-latched warn would be hundreds of lines/min. Loop-thread-only (CollectSnapshot).
        // (The abort-reason mapping needs no latch: its sole caller, CaptureAbort, runs ≤1/match.)
        private readonly HashSet<int> _warnedRoomStates = new HashSet<int>();   // Loop thread only

        private volatile bool _running;
        private volatile bool _dirty;
        private Thread _thread;

        // ── result propagation ──────────────────────────────────────────────────
        // Capture runs on a room's engine/update thread (OnMatchEnded/OnMatchAborted, OnPlayerJoined/Left); the
        // send/retry table lives on this reporter's Loop thread; acks arrive on the report client's poll thread.
        // The only cross-thread hand-offs are the two ConcurrentQueues; _pending is Loop-thread-only.
        private const long MatchResultResendMs   = 3000;                    // un-acked resend cadence (async telemetry; no waiting user)
        private const long MatchResultGiveUpMs   = 120000;                  // 2 min — generous; journal backstops beyond
        private const int  MatchResultMaxPending = 256;                     // un-acked queue cap (count)
        private const long MatchResultMaxPendingBytes = 8L * 1024 * 1024;   // un-acked queue cap (bytes — huge blobs can't bypass count)

        private readonly ConcurrentQueue<ResultCapture> _captureQueue = new ConcurrentQueue<ResultCapture>();
        private readonly ConcurrentQueue<int> _ackQueue = new ConcurrentQueue<int>();
        private readonly Dictionary<int, PendingResult> _pending = new Dictionary<int, PendingResult>(); // Loop-thread only
        private long _pendingBytes;           // Loop-thread only (mirrors _pending payload sizes)
        private int _nextRequestId;           // Loop-thread only
        private volatile bool _resendPending; // set on reconnect → Loop re-flushes all un-acked before the fresh report
        private readonly string _journalPath; // append-only crash backstop
        private readonly object _journalLock = new object();
        private FileStream _journal;          // opened lazily on first append

        /// <param name="advertiseHost">/<paramref name="advertisePort"/> — client-reachable address advertised
        /// in serverRegister (dedi's listen port; host is the dev advertised address, not 0.0.0.0).</param>
        /// <param name="maxRooms">/<paramref name="maxPlayersPerRoom"/> — this dedi's actual capacity (D4).</param>
        /// <param name="reservations">Optional lobby-driven match config source — forwarded to the report
        /// client so it receives <c>ReservePush</c> and replies <c>ReserveAck</c>. Also the match-instance-key source for
        /// result capture. Null = lobbyless (no reserve table, no result emit).</param>
        /// <param name="resultJournalPath">Optional path for the match-result crash-backstop journal. Null →
        /// a per-server+port path next to the executable (see ctor) — deliberately NOT the temp dir, and port-
        /// suffixed so serverId-sharing dev dedis don't collide on one append file.</param>
        public SdRoomReporter(RoomManager roomManager, IKLogger logger,
                              string lobbyHost, int lobbyPort,
                              string serverId, string advertiseHost, int advertisePort,
                              int maxRooms, int maxPlayersPerRoom, long intervalMs,
                              LobbyMatchConfigSource reservations = null,
                              string resultJournalPath = null)
        {
            _roomManager = roomManager ?? throw new ArgumentNullException(nameof(roomManager));
            _logger = logger;
            _intervalMs = intervalMs;
            _maxRooms = maxRooms;
            _snapshot = new RoomStateReport[maxRooms];
            _reservations = reservations;
            _prevState = new byte[maxRooms];
            _serverId = serverId;
            _journalPath = string.IsNullOrEmpty(resultJournalPath)
                // Default next to the executable (NOT the temp dir, which the OS may purge out from under this
                // durable backstop), and port-suffixed so two dedis that share a serverId (the dev single/multi-
                // room samples both pass SdDevIdentity.DevServerId) don't collide on one append file — the loser
                // of a FileShare.Read open runs journal-less. The listen port is unique per concurrent dedi and
                // stable across restart (a restarted server re-appends its own journal, preserving re-collection).
                ? Path.Combine(AppContext.BaseDirectory, $"klotho-matchresults-{Sanitize(serverId)}-{advertisePort}.journal")
                : resultJournalPath;
            _client = new LiteNetLibLobbyReportClient(logger, lobbyHost, lobbyPort,
                serverId, advertiseHost, advertisePort, maxRooms, maxPlayersPerRoom,
                onConnected: OnLobbyConnected, reservations: reservations,
                onMatchResultAck: OnResultAck);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "klotho-lobby-report" };
            _thread.Start();
        }

        // report-client onConnected hook (reconnect → fresh report + re-flush un-acked results).
        private void OnLobbyConnected()
        {
            _dirty = true;
            _resendPending = true;
        }

        private void OnResultAck(int requestId) => _ackQueue.Enqueue(requestId); // poll thread → Loop thread

        private void Loop()
        {
            long lastSentMs = 0;
            while (_running)
            {
                // Backstop: no per-tick throw (encode in PumpResults, CollectSnapshot, or an unforeseen fault) may
                // kill this thread — a dead reporter stops roomReports and the lobby reclaims the whole server. The
                // send chokepoints keep their own guards (TrySendResult / SendRoomReport); this catches the rest.
                try
                {
                    long now = NowMs();

                    // Result propagation FIRST — the flush-before-report ordering invariant puts a
                    // captured result on the wire ahead of the room's Empty/Disposing roomReport (the lobby's reclaim
                    // trigger), so ReliableOrdered delivers it first even at grace=0.
                    PumpResults(now);

                    if (_dirty || now - lastSentMs >= _intervalMs)
                    {
                        _dirty = false;
                        int count = CollectSnapshot();
                        try { _client.SendRoomReport(_snapshot, count); }
                        catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] send failed: {e.Message}"); }
                        lastSentMs = now;
                    }
                }
                catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] loop tick failed: {e.Message}"); }
                Thread.Sleep(PollGranularityMs);
            }
        }

        // Reports ALL room indices 0..MaxRooms-1 (Empty/0 for null slots) so the lobby distinguishes "missing
        // report" from "explicit Empty". SCALARS ONLY; defensive per-room read.
        private int CollectSnapshot()
        {
            for (int roomId = 0; roomId < _maxRooms; roomId++)
            {
                RoomRead read = RoomRead.Unknown;
                byte state = LobbyWire.RoomStateEmpty;
                int count = 0;
                try
                {
                    Room r = _roomManager.GetRoom(roomId);          // null when Empty/never-created
                    ServerNetworkService ns = r?.NetworkService;     // null guard: publish reorder (ARM64) / teardown
                    read = Classify(r != null, ns != null);
                    if (read == RoomRead.Live)
                    {
                        if (TryMapRoomState(r.State, out state))
                        {
                            count = ns.PlayerCount;                   // _players.Count — scalar int, never enumerate
                        }
                        else
                        {
                            // Unknown core member — hold last state (self-heals when the room really disposes:
                            // GetRoom → null → read = Empty → release fires then). WARN latched per value.
                            read = RoomRead.Unknown;
                            WarnUnmappedRoomStateOnce((int)r.State);
                        }
                    }
                }
                catch { read = RoomRead.Unknown; }

                // A failed read is NOT an Empty room. Reporting Empty here would fire the release gate below on a
                // LIVE room and drop its reserve entry — which is also the only source of the result's match key
                // (LobbyMatchConfigSource.TryGetMatchInfo), so the match's result would be neither sent nor
                // journalled. Hold the last reported state instead; the next report corrects it (the same
                // eventual-consistency rule this reporter already applies to a one-tick stale PlayerCount).
                SnapshotDecision d = DecideSnapshot(read, _prevState[roomId], state);
                if (_reservations != null && d.Release)
                    _reservations.Release(roomId);
                _prevState[roomId] = d.NewPrevState;

                _snapshot[roomId].RoomId = roomId;
                _snapshot[roomId].State = d.ReportedState;
                if (d.RefreshPlayerCount)                             // Unknown holds the last reported PlayerCount
                    _snapshot[roomId].PlayerCount = count;
            }
            return _maxRooms;
        }

        /// <summary>Outcome of one defensive per-room read. <c>Unknown</c> (service not published yet / teardown /
        /// read threw) is deliberately distinct from <c>Empty</c> — see the release gate.</summary>
        internal enum RoomRead : byte { Empty, Live, Unknown }

        internal static RoomRead Classify(bool roomExists, bool serviceReady)
            => !roomExists ? RoomRead.Empty
             : serviceReady ? RoomRead.Live
             : RoomRead.Unknown;

        /// <summary>Room-ended → release its dedi-side reservation entry. A materialized entry left behind NAKs the
        /// next match assigned to this reused room until the reservation TTL expires (5 min), so the release must
        /// land on the same transition the lobby reclaims the slot on — which is Disposing OR Empty, not Empty
        /// alone. Gating on the live→terminal transition (rather than "== Empty") keeps two windows safe: the
        /// reserve-before-join window (Empty→Empty, room not yet created) is untouched, and terminal→terminal
        /// (Disposing→Empty) does not re-release a reservation pushed in between. Release is lock-guarded.</summary>
        internal static bool ShouldRelease(byte prevState, RoomRead read, byte state)
        {
            if (read == RoomRead.Unknown) return false;              // a failed read releases nothing
            bool wasLive = prevState == LobbyWire.RoomStateActive || prevState == LobbyWire.RoomStateDraining;
            bool nowTerminal = state == LobbyWire.RoomStateDisposing || state == LobbyWire.RoomStateEmpty;
            return wasLive && nowTerminal;
        }

        /// <summary>Pure per-room snapshot decision, extracted so the Unknown-hold / release gate / prev-advance are
        /// exercised by unit tests against the REAL logic (not a re-coded copy). Inputs: the defensive read outcome,
        /// the last-reported wire state, and the effective wire state (<c>RoomStateEmpty</c> for an Empty read; the
        /// mapped state for Live). A failed (Unknown) read is NOT an Empty room — it holds the last reported state
        /// (report prev, do NOT advance prev, do NOT release, keep the last PlayerCount), so a transient
        /// null-service/throw on a LIVE room never fires the release gate nor reports a spurious Empty (which the
        /// lobby would reclaim on). Empty/Live report and advance to <paramref name="state"/> and release per
        /// <see cref="ShouldRelease"/>.</summary>
        internal readonly struct SnapshotDecision
        {
            public readonly byte ReportedState;
            public readonly bool Release;
            public readonly byte NewPrevState;
            public readonly bool RefreshPlayerCount;
            public SnapshotDecision(byte reportedState, bool release, byte newPrevState, bool refreshPlayerCount)
            {
                ReportedState = reportedState; Release = release; NewPrevState = newPrevState; RefreshPlayerCount = refreshPlayerCount;
            }
        }

        internal static SnapshotDecision DecideSnapshot(RoomRead read, byte prevState, byte state)
        {
            if (read == RoomRead.Unknown)
                return new SnapshotDecision(prevState, release: false, newPrevState: prevState, refreshPlayerCount: false);
            return new SnapshotDecision(state, ShouldRelease(prevState, read, state), newPrevState: state, refreshPlayerCount: true);
        }

        // Explicit core RoomState → wire byte (CORE values). Explicit map (not a cast) so a core enum reorder is
        // caught here rather than silently corrupting the wire. Returns false for an UNKNOWN member so the caller
        // drops to RoomRead.Unknown instead of masquerading it as Empty (which would fire the release gate on a
        // live room and drop the result key — a silent result loss). case Empty is EXPLICIT (returns true): the
        // GetRoom filter keeps it off the normal path, but a dispose-race (TOCTOU: State read twice, room disposed
        // in between) can deliver a real Empty here, and that is a correct translation — routing it through default
        // would misfire an "unknown core member" WARN once per race. Pure/static (no logging) so it is unit-
        // testable (internal); the caller latches the WARN.
        internal static bool TryMapRoomState(RoomState s, out byte wire)
        {
            switch (s)
            {
                case RoomState.Empty:     wire = LobbyWire.RoomStateEmpty;     return true;  // dispose-race (TOCTOU) — correct translation
                case RoomState.Active:    wire = LobbyWire.RoomStateActive;    return true;
                case RoomState.Draining:  wire = LobbyWire.RoomStateDraining;  return true;
                case RoomState.Disposing: wire = LobbyWire.RoomStateDisposing; return true;
                default:                  wire = LobbyWire.RoomStateEmpty;     return false; // unknown member only → caller: RoomRead.Unknown + WARN
            }
        }

        // Explicit core AbortReason → wire abortReason byte. Same discipline as TryMapRoomState: an explicit map,
        // not a raw (byte) cast, so the lobby wire / journal are decoupled from a core enum reorder. case Unknown
        // is EXPLICIT (returns true) so a real engine Unknown(0) is distinguishable from an unmapped value, which
        // returns false + the DEDICATED Unmapped(255) fallback (distinct by value, so it is identifiable even in
        // the journal where the caller's WARN is not). NOTE: the sole caller (CaptureAbort) filters to
        // StateDivergence before calling, so the default/false branch is unreachable today — it exists for a
        // future filter relaxation; do not flag it as dead. Pure/static (unit-testable); the caller latches the WARN.
        internal static bool TryMapAbortReason(AbortReason r, out byte wire)
        {
            switch (r)
            {
                case AbortReason.Unknown:           wire = LobbyWire.AbortReasonUnknown;         return true; // real value — distinct from unmapped
                case AbortReason.ChainStallTimeout: wire = LobbyWire.AbortReasonChainStall;      return true;
                case AbortReason.StateDivergence:   wire = LobbyWire.AbortReasonStateDivergence; return true;
                case AbortReason.ReconnectFailed:   wire = LobbyWire.AbortReasonReconnectFailed; return true;
                default:                            wire = LobbyWire.AbortReasonUnmapped;        return false; // unmapped → caller WARN; value ≠ Unknown(0)
            }
        }

        private void WarnUnmappedRoomStateOnce(int state) // Loop thread only — no lock
        {
            if (_warnedRoomStates.Add(state))
                _logger?.KWarning($"[SdRoomReporter] unmapped core RoomState={state} → RoomRead.Unknown (holding last state; result key preserved) — mapping needs a case");
        }

        // ── per-room subscription (wired via RoomManagerConfig.OnRoomCreated) ────────────────────────────
        /// <summary>Attaches result capture + identity-ledger subscriptions to a freshly-created room (called
        /// from <c>RoomManagerConfig.OnRoomCreated</c>, before the room goes Active). The engine / network-service
        /// events fire on the room's own update thread; capture builds an immutable snapshot there and queues it
        /// to this reporter's send thread — the background thread never reads live ECS.</summary>
        public void AttachRoom(Room room)
        {
            if (room == null) return;
            var ledger = new MatchIdentityLedger(); // per-room; PlayerId → {Account, DisplayName}, leaver NOT removed

            ServerNetworkService ns = room.NetworkService;
            if (ns != null)
            {
                ns.OnPlayerJoined += ledger.Record; // record/refresh (same room thread as capture → no lock)
                ns.OnPlayerLeft   += ledger.Record; // refresh, NOT remove — the leaver's Account must survive to capture
            }

            KlothoEngine engine = room.Engine;
            if (engine != null)
            {
                int roomId = room.RoomId;
                engine.OnMatchEnded   += (tick, endEvt) => CaptureNormalEnd(roomId, ledger, endEvt);
                engine.OnMatchAborted += reason         => CaptureAbort(roomId, engine, ledger, reason);
            }

            // Drain hook set here (not via RoomManagerConfig.OnRoomDraining) so the closure captures THIS room's
            // ledger directly — no roomId-keyed map to re-join it. CreateRoomAt assigns room.OnDraining before
            // invoking OnRoomCreated (this method), so this overwrite is the last writer and wins; it fires once
            // when the room drains (both branches), on the room's own update thread.
            room.OnDraining = r => HandleDraining(r, ledger);
        }

        // Capture runs on the room's engine thread. Wrapped in try/catch — OnMatchEnded is a "do not throw"
        // contract; a game blob-getter / assembly fault must not propagate into engine dispatch.
        private void CaptureNormalEnd(int roomId, MatchIdentityLedger ledger, IMatchEndEvent endEvt)
        {
            try
            {
                byte[] blob = (endEvt as IMatchResultProvider)?.MatchResultData;
                if (blob == null) return; // event carries no game result → no emit (opt-in, no-regression)
                Emit(roomId, LobbyWire.TerminationNormalEnd, ledger, blob);
            }
            catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] result capture failed room={roomId}: {e.Message}"); }
        }

        private void CaptureAbort(int roomId, KlothoEngine engine, MatchIdentityLedger ledger, AbortReason reason)
        {
            try
            {
                // Terminal report is once per match: if a NormalEnd already captured for this match (abort during
                // the end-grace window — engine State stays Running through grace, so the engine's own IsEnded()
                // guard does NOT block this callback), skip.
                if (engine != null && engine.IsMatchEnded) return;
                // Only server-authoritative aborts are lobby-relevant (StateDivergence); client-local aborts
                // (ChainStall/ReconnectFailed) are handled by the server continuing as authority. A NEW server-
                // authoritative AbortReason MUST be added to this filter, else it silently drops (no lobby notify).
                if (reason != AbortReason.StateDivergence) return;
                // Explicit map (not a raw (byte) cast) so the wire/journal are decoupled from a core enum reorder.
                // The filter above guarantees StateDivergence → true here; the false path is unreachable today
                // (kept for a future filter relaxation) — warns rather than silently mislabels (no latch: ≤1/match).
                if (!TryMapAbortReason(reason, out byte wireReason))
                    _logger?.KWarning($"[SdRoomReporter] unmapped core AbortReason={(int)reason} → wire Unmapped(255) — mapping needs a case");
                // culpritPlayerId is not exposed by OnMatchAborted(reason) — the roster side-channel carries every
                // participant's Account, so the lobby can resolve a culprit by id when one is threaded later.
                byte[] payload = LobbyWire.EncodeAbortNotification(wireReason, culpritPlayerId: -1);
                Emit(roomId, LobbyWire.TerminationAborted, ledger, payload);
            }
            catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] abort capture failed room={roomId}: {e.Message}"); }
        }

        // ── abandoned-match capture (room.OnDraining closure, set in AttachRoom, captures the per-room ledger) ──
        /// <summary>Runs on the room's update thread the tick it goes Draining (BOTH drain branches). The
        /// abandoned case (all peers left, the match ran but no end was ever requested) is the only one that
        /// emits; normal-end / abort already went through the engine events. <c>try/catch</c> is mandatory: the
        /// core does not wrap <c>OnDraining</c>, so a throw would surface out of <c>Room.Update</c> (where only
        /// ServerLoop's worker try/catch swallows it as a context-less KError) — catching here keeps the loss
        /// observable as a contextful WARN and loses no tick work upstream. Fires exactly once per room
        /// (Room.Update guards on State==Active), and the ledger arrives captured — no once-only removal guard needed.</summary>
        private void HandleDraining(Room room, MatchIdentityLedger ledger)
        {
            try
            {
                if (room == null) return;
                try
                {
                    // Abandoned (all peers left, the match ran, no end ever requested) is the only branch that
                    // emits here; normal-end / abort already went through the engine events before the drain.
                    if (IsAbandoned(room.EndRequestedAtUtc.HasValue, room.DrainPhase))
                    {
                        // culprit -1 = "no single culprit" (everyone left). Same value as StateDivergence's -1
                        // ("unknown who"), different meaning — distinguished only by abortReason. The abandoned
                        // match's responsible party is the whole roster, carried on the side-channel.
                        byte[] payload = LobbyWire.EncodeAbortNotification(LobbyWire.AbortReasonAbandoned, culpritPlayerId: -1);
                        Emit(room.RoomId, LobbyWire.TerminationAborted, ledger, payload);
                    }
                }
                finally
                {
                    // Deterministic reservation release on EVERY drain (both branches). The sampling gate in
                    // CollectSnapshot misses a room whose whole life (Active→Draining→Disposing→Empty) elapses
                    // within one ~3s report interval, leaving a materialized entry that NAKs the next match to
                    // this room until its TTL. Release here (AFTER the abandoned Emit reads the key; normal-end/
                    // abort read it earlier at match-end) closes that window; the sampling gate then no-ops on the
                    // already-removed entry.
                    if (_reservations != null)
                    {
                        _reservations.Release(room.RoomId);
                        _logger?.KInformation($"[SdRoomReporter] drain-release room={room.RoomId}");
                    }
                }
            }
            catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] abandon capture failed room={room?.RoomId}: {e.Message}"); }
        }

        // Pure discriminator, extracted so it is unit-testable without constructing the reporter (whose
        // ctor opens a real report client). Abandoned iff the match STARTED (DrainPhase == Playing) and no end was
        // ever requested (EndRequestedAtUtc == null → not a normal-end/abort). Equality only: SessionPhase
        // .Disconnected sorts AFTER Playing, so an ordinal (< Playing) test would miss it.
        internal static bool IsAbandoned(bool endRequested, SessionPhase drainPhase)
            => !endRequested && drainPhase == SessionPhase.Playing;

        private void Emit(int roomId, byte terminationKind, MatchIdentityLedger ledger, byte[] payload)
        {
            if (_reservations == null) return; // lobbyless → no match key → no emit (no-regression; stays silent)
            if (!_reservations.TryGetMatchInfo(roomId, out string matchInstanceId, out int stageId) || string.IsNullOrEmpty(matchInstanceId))
            {
                // ANOMALY (not lobbyless): a reservation was expected but the match key is gone — the reserve entry
                // was released early (an overload race, or a release-gate defect). WARN so this loss is
                // observable in ops (the only signal for the overload race, which e2e cannot reproduce).
                _logger?.KWarning($"[SdRoomReporter] Emit: no match key for room={roomId} (reservation gone) — result dropped, kind={terminationKind}");
                return; // no reservation for this room → nothing to key the result by → skip
            }
            MatchResultRosterEntry[] roster = ledger.Snapshot();
            var cap = new ResultCapture(matchInstanceId, _serverId, roomId, stageId, terminationKind, roster, payload);
            _captureQueue.Enqueue(cap); // hand to the send thread; journal append+fsync + encode happen there (off the engine thread)
        }

        // Crash backstop: append a pre-encoded MatchResult record (encoded once by the Loop-thread caller with
        // requestId=0, BEFORE it patches the real id in for the send buffer) as a length prefix + the record, and
        // flush to disk, so a give-up / crash before the lobby acks still leaves a durable copy for offline
        // re-collection. Runs on the reporter's Loop thread (not the room engine thread), so the fsync no longer
        // stalls the simulation. Reference impl: plain append; acked-entry compaction and re-collection tooling
        // are game-owned.
        //
        // requestId is always 0 on disk — it means nothing here, so it is the reserved slot for a journal record
        // version should this format ever need one (old records then read as v0). Do NOT append a new field
        // instead: a newer decoder reading an older record runs off the end and rejects the whole record.
        // Records carry no timestamp and no CRC. A crash mid-write leaves a partial record, after which the next
        // length prefix is garbage — a reader must stop at the first malformed record and trust only what precedes.
        private void AppendJournal(byte[] rec)
        {
            try
            {
                lock (_journalLock)
                {
                    _journal ??= new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    Span<byte> lenPrefix = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(lenPrefix, rec.Length);
                    _journal.Write(lenPrefix);
                    _journal.Write(rec, 0, rec.Length);
                    _journal.Flush(flushToDisk: true);
                }
            }
            catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] journal append failed: {e.Message}"); }
        }

        // ── send thread: drain acks, flush captures, retry un-acked (called first each Loop tick) ────────
        private void PumpResults(long now)
        {
            // 1) drain acks (poll thread → here): acked results are done.
            while (_ackQueue.TryDequeue(out int ackedId))
                RemovePending(ackedId);

            // 2) flush new captures: assign a requestId, encode, enforce the cap, add to pending, send.
            bool resend = _resendPending; _resendPending = false;
            while (_captureQueue.TryDequeue(out ResultCapture cap))
            {
                int requestId = ++_nextRequestId;
                // Encode ONCE with requestId=0 (the journal's v0 version marker); journal it as-is (crash backstop,
                // journal-before-send), then patch the real requestId in place for the send buffer — the journal
                // image and the wire differ only by the 4 bytes at offset 1, so the second full encode is removed.
                byte[] wire = LobbyWire.EncodeMatchResult(0, cap.ServerId, cap.RoomId, cap.MatchInstanceId,
                                                          cap.StageId, cap.TerminationKind, cap.Roster,
                                                          cap.Roster.Length, cap.Payload);
                AppendJournal(wire); // on the Loop thread now — the fsync no longer stalls the room engine thread (F6)

                // A single result that alone exceeds the whole pending byte budget would make EnforcePendingCap
                // drain the ENTIRE un-acked table (nothing can fit beside it), then get re-evicted on the next
                // capture — one oversized blob destroys every other result's retry. Reject it here: the journal
                // already holds it (offline re-collection) and pending is left untouched. `> cap` is the exact
                // threshold at which even an empty table still can't hold it (0 + len > cap).
                if (wire.Length > MatchResultMaxPendingBytes)
                {
                    _logger?.KWarning($"[SdRoomReporter] oversized result requestId={requestId} len={wire.Length} > cap {MatchResultMaxPendingBytes} — journalled, not queued for live send");
                    continue;
                }

                BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(1), requestId);
                EnforcePendingCap(wire.Length);
                _pending[requestId] = new PendingResult(wire, now);
                _pendingBytes += wire.Length;
                TrySendResult(requestId, now);
            }

            // 3) retry / give-up scan; reconnect re-flush of all un-acked.
            List<int> giveUp = null;
            foreach (var kv in _pending)
            {
                PendingResult p = kv.Value;
                if (now - p.FirstQueuedMs >= MatchResultGiveUpMs) { (giveUp ??= new List<int>()).Add(kv.Key); continue; }
                if (resend || p.LastSentMs == 0 || now - p.LastSentMs >= MatchResultResendMs)
                    TrySendResult(kv.Key, now);
            }
            if (giveUp != null)
                foreach (int id in giveUp)
                {
                    RemovePending(id);
                    _logger?.KWarning($"[SdRoomReporter] match result give-up after {MatchResultGiveUpMs}ms (journal backstop retains it), requestId={id}");
                }
        }

        private void TrySendResult(int requestId, long now)
        {
            if (!_pending.TryGetValue(requestId, out PendingResult p)) return;
            // Guard the single send chokepoint (called from BOTH the flush and the retry loops). An unguarded throw
            // here aborts PumpResults before step 3's give-up scan, so a poison entry is never dropped and re-throws
            // every tick — starving the roomReport until the lobby reclaims the server. Swallow to a WARN and leave
            // the entry for retry (same outcome as a not-connected send).
            try { if (_client.SendMatchResult(p.Wire)) p.LastSentMs = now; } // not connected → leave for retry / reconnect flush
            catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] send result failed requestId={requestId}: {e.Message}"); }
        }

        private void RemovePending(int requestId)
        {
            if (_pending.TryGetValue(requestId, out PendingResult p))
            {
                _pendingBytes -= p.Wire.Length;
                _pending.Remove(requestId);
            }
        }

        // Bound the un-acked table by count AND bytes (a few huge blobs must not bypass the count cap). Over the
        // bound → drop the oldest (lowest requestId; ids are monotonic) + WARN (never silent; journal keeps it).
        private void EnforcePendingCap(int incomingLen)
        {
            while (_pending.Count > 0 &&
                   (_pending.Count >= MatchResultMaxPending || _pendingBytes + incomingLen > MatchResultMaxPendingBytes))
            {
                int oldest = int.MaxValue;
                foreach (int k in _pending.Keys) if (k < oldest) oldest = k;
                RemovePending(oldest);
                _logger?.KWarning($"[SdRoomReporter] un-acked result queue cap exceeded — dropped oldest requestId={oldest} (journal backstop retains it)");
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(500); } catch { /* ignore */ }
            _client.Dispose();
            lock (_journalLock) { _journal?.Dispose(); _journal = null; }
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "server";
            char[] chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_') chars[i] = '_';
            return new string(chars);
        }

        // Immutable capture handed engine-thread → send-thread. Roster/payload are snapshots (never live ECS).
        private sealed class ResultCapture
        {
            public readonly string MatchInstanceId; public readonly string ServerId; public readonly int RoomId;
            public readonly int StageId; public readonly byte TerminationKind;
            public readonly MatchResultRosterEntry[] Roster; public readonly byte[] Payload;
            public ResultCapture(string matchInstanceId, string serverId, int roomId, int stageId, byte terminationKind,
                                 MatchResultRosterEntry[] roster, byte[] payload)
            {
                MatchInstanceId = matchInstanceId; ServerId = serverId; RoomId = roomId; StageId = stageId;
                TerminationKind = terminationKind; Roster = roster; Payload = payload;
            }
        }

        private sealed class PendingResult
        {
            public readonly byte[] Wire; public readonly long FirstQueuedMs; public long LastSentMs;
            public PendingResult(byte[] wire, long firstQueuedMs) { Wire = wire; FirstQueuedMs = firstQueuedMs; LastSentMs = 0; }
        }

        // Per-room match-scoped identity ledger. Written on the room's update thread (OnPlayerJoined/Left)
        // and read at capture on that SAME thread → no lock. A leaver is NOT removed (its Account must survive
        // into the result); PlayerId is stable across reconnect.
        private sealed class MatchIdentityLedger
        {
            private readonly Dictionary<int, MatchResultRosterEntry> _byPlayer = new Dictionary<int, MatchResultRosterEntry>();
            public void Record(IPlayerInfo p)
            {
                if (p == null) return;
                _byPlayer[p.PlayerId] = new MatchResultRosterEntry
                {
                    PlayerId = p.PlayerId,
                    Account = p.Account ?? string.Empty,
                    DisplayName = p.DisplayName ?? string.Empty,
                };
            }
            public MatchResultRosterEntry[] Snapshot()
            {
                var arr = new MatchResultRosterEntry[_byPlayer.Count];
                int i = 0;
                foreach (var kv in _byPlayer) arr[i++] = kv.Value;
                return arr;
            }
        }
    }
}
