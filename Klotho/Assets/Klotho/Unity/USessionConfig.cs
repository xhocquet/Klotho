using UnityEngine;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho
{
    /// <summary>
    /// SessionConfig editable from the Unity Inspector.
    /// Used as a ScriptableObject or MonoBehaviour field on the host side.
    /// </summary>
    [CreateAssetMenu(menuName = "Klotho/SessionConfig", fileName = "SessionConfig")]
    public class USessionConfig : ScriptableObject, ISessionConfig
    {
        [Header("Determinism")]
        [field: SerializeField] public int RandomSeed { get; set; } = 0;

        [Header("Membership")]
        [field: SerializeField] public int MaxPlayers { get; set; } = 4;
        [field: SerializeField] public int MinPlayers { get; set; } = 2;
        [field: SerializeField] public int MaxSpectators { get; set; } = 0;

        [Header("LateJoin / Reconnect Policy")]
        [field: SerializeField] public bool AllowLateJoin { get; set; } = true;
        [field: SerializeField] public int LateJoinDelayTicks { get; set; } = 10;
        [field: SerializeField] public int ReconnectTimeoutMs { get; set; } = 60000;
        [field: SerializeField] public int ReconnectMaxRetries { get; set; } = 3;

        [Header("LateJoin / Reconnect Tuning")]
        [field: SerializeField] public int LateJoinDelaySafety { get; set; } = 2;
        [field: SerializeField] public int RttSanityMaxMs { get; set; } = 240;

        [Header("Chain-Stall Watchdog")]
        [field: SerializeField] public int MinStallAbortTicks { get; set; } = 600;

        [Header("Match Start Countdown")]
        [field: SerializeField] public int CountdownDurationMs { get; set; } = 3000;

        [Header("Match End Grace")]
        [field: SerializeField] public int AbortGraceMs { get; set; } = 1500;
        [field: SerializeField] public EndGracePolicy EndGracePolicy { get; set; } = EndGracePolicy.Continue;
        [field: SerializeField] public int EndGraceMs { get; set; } = 5000;
        [field: SerializeField] public int ClientShutdownGraceMs { get; set; } = 4500;
    }
}
