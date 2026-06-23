using System;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    public enum SpectatorState
    {
        Idle,
        Connecting,
        Synchronizing,
        Watching,
        Disconnected
    }

    public class SpectatorStartInfo
    {
        public int RandomSeed;
        public int TickInterval;
        public int PlayerCount;
        public List<int> PlayerIds;
    }

    public interface ISpectatorService
    {
        SpectatorState State { get; }

        void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKlothoEngine engine, IKLogger logger);

        void Connect(string hostAddress, int port, int roomId = -1);

        void Disconnect();

        void Update();

        int DelayFrames { get; }

        int LatestReceivedTick { get; }

        /// <summary>
        /// Live player count. Seeded from <see cref="SpectatorAcceptMessage.PlayerIds"/> on bootstrap,
        /// then mutated by lobby join/leave notifications (pre-game) and <c>LateJoinNotificationMessage</c>
        /// arrivals (mid-match).
        /// </summary>
        int PlayerCount { get; }

        event Action<SpectatorStartInfo> OnSpectatorStarted;

        event Action<int, ICommand> OnConfirmedInputReceived;

        event Action<string> OnSpectatorStopped;

        event Action<int, byte[], long, FullStateKind> OnFullStateReceived;

        event Action<ISessionConfig> OnSessionConfigReceived;

        /// <summary>
        /// Fired when <see cref="PlayerCount"/> changes. Argument is the new count.
        /// </summary>
        event Action<int> OnPlayerCountChanged;
    }
}
