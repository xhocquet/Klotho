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
        Network.SessionPhase Phase { get; }
        bool AllPlayersReady { get; }
        bool IsStopped { get; }

        void Update(float deltaTime);
        void InputCommand(ICommand command);
        void Stop(bool keepReconnectCredentials = false, bool saveReplay = true);

        void HostGame(string roomName, int maxPlayers);
        void JoinGame(string roomName);
        void LeaveRoom();
        void SendPlayerConfig(PlayerConfigBase playerConfig);
        void SetReady(bool ready);
    }
}
