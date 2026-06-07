using System;
using System.Threading.Tasks;
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.DeterminismVerification;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Godot;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

// Headless 2-peer P2P determinism check inside a Godot (.NET) process.
// host = KlothoSessionFlow.StartHostAndListen; guest = GodotConnectionAsync.ConnectAsync ->
// flow.CreateForConnection (official flow, no reflection backdoor). Deterministic inputs are
// produced in ISimulationCallbacks.OnPollInput; per-tick state hashes of both peers must match.
public partial class Main : Node
{
    private const int Port = 7777;
    private const int TargetTick = 300;
    private const string ConnectionKey = "xpTURN.GodotNetCheck";

    public override void _Ready()
    {
        int code;
        try { code = Run(); }
        catch (Exception ex) { GD.PushError($"[NetCheck] EXCEPTION: {ex}"); code = 2; }
        GetTree().Quit(code);
    }

    private static int Run()
    {
        WarmupRegistry.RunAll();

        var logger = new GodotDebugSink(KLogLevel.Warning);
        var simCallbacks = new NetCheckSimCallbacks();
        var viewCallbacks = new NoopViewCallbacks();
        SessionCallbacks Factory(ISimulationConfig s, ISessionConfig ss) =>
            new SessionCallbacks(simCallbacks, viewCallbacks);

        var simCfg = new SimulationConfig();
        var sesCfg = new SessionConfig { MaxPlayers = 2, MinPlayers = 2, CountdownDurationMs = 0 };

        GD.Print($"=== Godot 2-peer P2P determinism check (Godot {Engine.GetVersionInfo()["string"]}) ===");

        // ── host ──
        var hostTransport = new LiteNetLibTransport(logger, connectionKey: ConnectionKey);
        var hostFlow = new KlothoSessionFlow(new KlothoFlowSetup
        {
            Logger = logger,
            Transport = hostTransport,
            CallbacksFactory = Factory,
        });
        var hostSession = hostFlow.StartHostAndListen(simCfg, sesCfg, "room", "0.0.0.0", Port);

        // ── guest connect (official KlothoConnection path, pumped by this loop) ──
        var guestTransport = new LiteNetLibTransport(logger, connectionKey: ConnectionKey);
        var guestFlow = new KlothoSessionFlow(new KlothoFlowSetup
        {
            Logger = logger,
            Transport = guestTransport,
            CallbacksFactory = Factory,
        });

        Task<ConnectionResult> connectTask =
            GodotConnectionAsync.ConnectAsync(guestTransport, "127.0.0.1", Port, logger);

        long lastMs = NowMs();
        int guard = 0;
        while (!connectTask.IsCompleted)
        {
            hostTransport.PollEvents();
            guestTransport.PollEvents();
            float dt = StepDt(ref lastMs);
            hostSession.Update(dt);
            System.Threading.Thread.Sleep(1);
            if (++guard > 60000) { GD.PushError("[NetCheck] connect timeout"); return 1; }
        }
        if (connectTask.IsFaulted) { GD.PushError($"[NetCheck] connect failed: {connectTask.Exception}"); return 1; }

        var guestSession = guestFlow.CreateForConnection(connectTask.Result, 0, sesCfg);
        GD.Print("[NetCheck] guest connected, session created.");

        // ── ready -> Playing (CountdownDurationMs=0 => immediate) ──
        hostSession.NetworkService.SetReady(true);
        guestSession.NetworkService.SetReady(true);

        guard = 0;
        while (hostSession.Phase != SessionPhase.Playing || guestSession.Phase != SessionPhase.Playing)
        {
            hostTransport.PollEvents();
            guestTransport.PollEvents();
            float dt = StepDt(ref lastMs);
            hostSession.Update(dt);
            guestSession.Update(dt);
            System.Threading.Thread.Sleep(1);
            if (++guard > 60000) { GD.PushError($"[NetCheck] not Playing (host={hostSession.Phase}, guest={guestSession.Phase})"); return 1; }
        }
        GD.Print("[NetCheck] both peers Playing — running ticks.");

        // ── tick loop until TargetTick + per-tick hash compare ──
        int mismatches = 0;
        int compared = 0;
        guard = 0;
        while (hostSession.Engine.CurrentTick < TargetTick)
        {
            hostTransport.PollEvents();
            guestTransport.PollEvents();
            float dt = StepDt(ref lastMs);
            hostSession.Update(dt);
            guestSession.Update(dt);
            System.Threading.Thread.Sleep(1);

            int t = Math.Min(hostSession.Engine.CurrentTick, guestSession.Engine.CurrentTick);
            if (t > compared)
            {
                long hh = hostSession.Simulation.GetStateHash();
                long gh = guestSession.Simulation.GetStateHash();
                if (hh != gh)
                {
                    mismatches++;
                    if (mismatches <= 5)
                        GD.PushError($"[NetCheck] DESYNC at tick {t}: host={hh} guest={gh}");
                }
                compared = t;
            }
            if (++guard > 200000) { GD.PushError($"[NetCheck] tick loop stalled at {t}"); break; }
        }

        GD.Print($"[NetCheck] ticks host={hostSession.Engine.CurrentTick} guest={guestSession.Engine.CurrentTick}, compared={compared}, mismatches={mismatches}");
        if (mismatches == 0 && compared > 0)
        {
            GD.Print("=== NETCHECK PASSED (desync 0) ===");
            return 0;
        }
        GD.PushError("=== NETCHECK FAILED ===");
        return 1;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static float StepDt(ref long lastMs)
    {
        long now = NowMs();
        float dt = (lastMs > 0) ? (now - lastMs) * 0.001f : 0f;
        lastMs = now;
        return dt;
    }
}

// Deterministic simulation callbacks — same systems as the console determinism harness.
internal sealed class NetCheckSimCallbacks : ISimulationCallbacks
{
    public void RegisterSystems(EcsSimulation simulation)
    {
        simulation.AddSystem(new EntityLifecycleSystem(), SystemPhase.PreUpdate);
        simulation.AddSystem(new ArithmeticStressSystem(), SystemPhase.Update);
        simulation.AddSystem(new TrigStressSystem(), SystemPhase.PostUpdate);
        simulation.AddSystem(new RandomStressSystem(), SystemPhase.LateUpdate);
    }

    public void OnInitializeWorld(IKlothoEngine engine) { }

    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        // Deterministic per (playerId, tick) — both peers compute identical local inputs,
        // so after lockstep exchange every peer's state stays byte-identical.
        var rng = new DeterministicRandom(playerId * 1000003 + tick);
        var moveDir = new FPVector3(
            rng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)),
            FP64.Zero,
            rng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)));
        int actionId = rng.NextInt(0, 10);
        sender.Send(new DeterminismTestCommand(playerId, tick, moveDir, actionId));
    }
}

internal sealed class NoopViewCallbacks : IViewCallbacks
{
    public void OnGameStart(IKlothoEngine engine) { }
    public void OnTickExecuted(int tick) { }
    public void OnLateJoinActivated(IKlothoEngine engine) { }
}
