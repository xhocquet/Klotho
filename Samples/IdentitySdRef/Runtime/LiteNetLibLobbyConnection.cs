using System;
using System.Threading;

using xpTURN.Klotho.Logging;    // IKLogger
using xpTURN.Klotho.Network;    // DeliveryMethod
using xpTURN.Klotho.LiteNetLib; // LiteNetLibTransport

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Shared client→lobby LiteNetLib connection for the redeem / issue clients. Owns a dedicated poll-pump
    /// thread (separate from the game's server loop), the connect/reconnect state machine, and the single
    /// lobby peer id. Received packets are decoded synchronously inside the poll thread's
    /// <c>OnDataReceived</c> callback (the buffer is pooled and returned right after), so the owner's
    /// <paramref name="onData"/> must fully read what it needs before returning.
    /// </summary>
    internal sealed class LiteNetLibLobbyConnection : IDisposable
    {
        private const int PollIntervalMs = 15;
        private const int ReconnectIntervalMs = 1000;

        private readonly LiteNetLibTransport _transport;
        private readonly IKLogger _logger;
        private readonly string _host;
        private readonly int _port;
        private readonly Action<int, byte[], int> _onData;
        private readonly Action _onConnected;

        private volatile bool _running;
        private volatile bool _connected;
        private volatile int _lobbyPeerId = -1;
        private long _lastConnectAttemptMs;
        private Thread _pollThread;

        private readonly string _threadName;

        public LiteNetLibLobbyConnection(IKLogger logger, string host, int port, string connectionKey,
                                         Action<int, byte[], int> onData, Action onConnected,
                                         string threadName = "klotho-lobby-poll")
        {
            _logger = logger;
            _host = host;
            _port = port;
            _onData = onData;
            _onConnected = onConnected;
            _threadName = threadName;
            _transport = new LiteNetLibTransport(logger, null, connectionKey);
            _transport.OnPeerConnected += pid => { _lobbyPeerId = pid; _connected = true; _onConnected?.Invoke(); };
            _transport.OnPeerDisconnected += _ => { _connected = false; _lobbyPeerId = -1; };
            _transport.OnDisconnected += _ => { _connected = false; _lobbyPeerId = -1; };
            _transport.OnDataReceived += (peerId, data, len) => _onData?.Invoke(peerId, data, len);
        }

        public bool IsConnected => _connected;

        public void Start()
        {
            if (_running) return;
            _running = true;
            ConnectNow();
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = _threadName };
            _pollThread.Start();
        }

        /// <summary>Sends to the lobby if connected; returns false if not yet connected (the owner resends
        /// from <c>onConnected</c>, or the validator's timeout cancels the pending request → fail-closed).</summary>
        public bool TrySend(byte[] msg)
        {
            int pid = _lobbyPeerId;
            if (!_connected || pid < 0) return false;
            _transport.Send(pid, msg, DeliveryMethod.ReliableOrdered);
            return true;
        }

        private void ConnectNow()
        {
            _lastConnectAttemptMs = NowMs();
            try { _transport.Connect(_host, _port); }
            catch (Exception e) { _logger?.KWarning($"[LobbyConnection] connect failed: {e.Message}"); }
        }

        private void PollLoop()
        {
            while (_running)
            {
                try { _transport.PollEvents(); }
                catch (Exception e) { _logger?.KWarning($"[LobbyConnection] poll: {e.Message}"); }

                if (!_connected && NowMs() - _lastConnectAttemptMs >= ReconnectIntervalMs)
                    ConnectNow(); // reconnect after a drop / keep retrying until the first connect

                Thread.Sleep(PollIntervalMs);
            }
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Dispose()
        {
            _running = false;
            try { _pollThread?.Join(500); } catch { /* ignore */ }
            try { _transport.Disconnect(); } catch { /* ignore */ }
        }
    }
}
