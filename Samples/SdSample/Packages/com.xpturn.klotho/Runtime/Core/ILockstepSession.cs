using System;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Klotho session interface.
    /// An instance representing the combination of engine + simulation + network.
    /// Mirrors the public surface of <see cref="KlothoSession"/>; game code should depend
    /// on this interface, not the concrete class.
    /// </summary>
    public interface IKlothoSession
    {
        IKlothoEngine Engine { get; }
        ECS.EcsSimulation Simulation { get; }
        int LocalPlayerId { get; }
        KlothoState State { get; }
        int PlayerCount { get; }
        bool IsStopped { get; }

        void Update(float deltaTime);
        void InputCommand(ICommand command);
        void Stop(bool keepReconnectCredentials = false);

        void HostGame(string roomName, int maxPlayers);
        void JoinGame(string roomName);
        void LeaveRoom();
        void SendPlayerConfig(PlayerConfigBase playerConfig);
        void SetReady(bool ready);

        event Action<KlothoState> StateChanged;
        event Action<SessionPhase> PhaseChanged;
        event Action<int> PlayerCountChanged;
        event Action<bool> AllPlayersReadyChanged;
    }
}
