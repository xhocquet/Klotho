using System;
using System.Threading;
using System.Threading.Tasks;

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// In-process <see cref="ILobbyRedeemClient"/> backed by <see cref="DevLobbyCore"/> — the test fake and
    /// a no-process demo option. Because it wraps the SAME core the external DevLobbyServer wraps, the
    /// redeem logic under test is the real logic (no parallel reimplementation).
    /// <para>
    /// <see cref="Mode"/> controls completion timing so tests can drive the validator's pending path
    /// deterministically as well as exercise a genuine cross-thread completion:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="Mode.Immediate"/> — completes synchronously (Task.FromResult). Deterministic.</item>
    /// <item><see cref="Mode.ManualGate"/> — returns a pending Task the test releases via <see cref="ReleaseGated"/>. Deterministic, single-threaded.</item>
    /// <item><see cref="Mode.ThreadPool"/> — completes on a ThreadPool thread (genuine cross-thread completion).</item>
    /// <item><see cref="Mode.Hang"/> — never completes (drives the timeout path).</item>
    /// <item><see cref="Mode.Throw"/> — faults the Task (drives the fail-closed path).</item>
    /// </list>
    /// </summary>
    public sealed class InProcLobbyRedeemClient : ILobbyRedeemClient
    {
        public enum Mode { Immediate, ManualGate, ThreadPool, Hang, Throw }

        private readonly DevLobbyCore _core;
        private Mode _mode;
        private int _callCount;
        private int _lastRoomId = int.MinValue;

        // ManualGate state — one pending request at a time is sufficient for the harness.
        private readonly object _gateLock = new object();
        private TaskCompletionSource<RedeemResult> _gated;
        private string _gatedTicket, _gatedSessionId, _gatedServerId;
        private int _gatedRoomId;

        public InProcLobbyRedeemClient(DevLobbyCore core, Mode mode = Mode.Immediate)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _mode = mode;
        }

        /// <summary>Total RedeemAsync calls observed (test asserts e.g. "redeem not called" on local reject).</summary>
        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>The roomId of the most recent RedeemAsync call (test asserts the validator carried request.RoomId).</summary>
        public int LastRoomId => Volatile.Read(ref _lastRoomId);

        public void SetMode(Mode mode) => _mode = mode;

        public Task<RedeemResult> RedeemAsync(string ticketWire, string sessionId, string serverId, int roomId, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            Volatile.Write(ref _lastRoomId, roomId);
            switch (_mode)
            {
                case Mode.Immediate:
                    return Task.FromResult(_core.Redeem(ticketWire, sessionId, serverId, roomId));

                case Mode.ThreadPool:
                    return Task.Run(() => _core.Redeem(ticketWire, sessionId, serverId, roomId));

                case Mode.Throw:
                    return Task.FromException<RedeemResult>(new InvalidOperationException("dev fake: transport failure"));

                case Mode.Hang:
                {
                    var hung = new TaskCompletionSource<RedeemResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => hung.TrySetCanceled());
                    return hung.Task;
                }

                case Mode.ManualGate:
                default:
                {
                    var tcs = new TaskCompletionSource<RedeemResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => tcs.TrySetCanceled());
                    lock (_gateLock)
                    {
                        _gated = tcs;
                        _gatedTicket = ticketWire;
                        _gatedSessionId = sessionId;
                        _gatedServerId = serverId;
                        _gatedRoomId = roomId;
                    }
                    return tcs.Task;
                }
            }
        }

        /// <summary>ManualGate: completes the pending request through <see cref="DevLobbyCore"/> on the
        /// caller's thread (deterministic, single-threaded). Returns false if no request is pending.</summary>
        public bool ReleaseGated()
        {
            TaskCompletionSource<RedeemResult> tcs;
            string ticket, session, server;
            int room;
            lock (_gateLock)
            {
                tcs = _gated; ticket = _gatedTicket; session = _gatedSessionId; server = _gatedServerId; room = _gatedRoomId;
                _gated = null;
            }
            if (tcs == null) return false;
            return tcs.TrySetResult(_core.Redeem(ticket, session, server, room));
        }

        /// <summary>ManualGate: completes the pending request on a ThreadPool thread, exercising a genuine
        /// cross-thread completion of the handle. Returns false if no request is pending.</summary>
        public bool ReleaseGatedOnThreadPool()
        {
            TaskCompletionSource<RedeemResult> tcs;
            string ticket, session, server;
            int room;
            lock (_gateLock)
            {
                tcs = _gated; ticket = _gatedTicket; session = _gatedSessionId; server = _gatedServerId; room = _gatedRoomId;
                _gated = null;
            }
            if (tcs == null) return false;
            Task.Run(() => tcs.TrySetResult(_core.Redeem(ticket, session, server, room)));
            return true;
        }
    }
}
