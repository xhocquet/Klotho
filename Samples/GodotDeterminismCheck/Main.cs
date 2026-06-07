using System.Collections.Generic;
using System.IO;
using Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.DeterminismVerification;
using xpTURN.Klotho.ECS;

// Headless determinism check inside a Godot (.NET) process.
// Mirrors Tools/DeterminismVerification/Program.cs exactly (same harness, same input
// sequence), so the per-tick hash dump must match the net8 console baseline byte-for-byte.
public partial class Main : Node
{
    private const int TotalTicks = 10_000;
    private const int MaxEntities = 512;
    private const int DeltaTimeMs = 25; // 40Hz
    private static readonly int[] Seeds = { 42, 12345, 987654321 };

    public override void _Ready()
    {
        int code = Run();
        GetTree().Quit(code);
    }

    private static int Run()
    {
        // Trigger [ModuleInitializer] registrations (ECS component storage) before building sims.
        WarmupRegistry.RunAll();

        // Output dir overridable via a trailing user cmdline arg; defaults to a temp path.
        string outputDir = "/tmp/klotho_det_godot";
        string[] userArgs = OS.GetCmdlineUserArgs();
        if (userArgs.Length > 0 && !string.IsNullOrWhiteSpace(userArgs[0]))
            outputDir = userArgs[0];
        Directory.CreateDirectory(outputDir);

        GD.Print($"=== Determinism Verification (Godot {Engine.GetVersionInfo()["string"]}, .NET {System.Environment.Version}) ===");
        GD.Print($"Ticks: {TotalTicks}, Seeds: {Seeds.Length}, MaxEntities: {MaxEntities}");

        foreach (int seed in Seeds)
        {
            string csvPath = Path.Combine(outputDir, $"hashes_seed{seed}.csv");
            using var writer = new HashDumpWriter(csvPath);

            var sim = NewSim();
            SeedInitialEntities(sim, seed);

            var inputRng = new DeterministicRandom(seed);
            var commands = new List<ICommand>();

            for (int tick = 0; tick < TotalTicks; tick++)
            {
                commands.Clear();
                var moveDir = new FPVector3(
                    inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)),
                    FP64.Zero,
                    inputRng.NextFixed(FP64.FromInt(-1), FP64.FromInt(1)));
                int actionId = inputRng.NextInt(0, 10);
                commands.Add(new DeterminismTestCommand(1, tick, moveDir, actionId));

                sim.Tick(commands);
                writer.WriteHash(tick, sim.GetStateHash());
            }

            GD.Print($"Seed {seed}: {TotalTicks} ticks -> {csvPath}");
        }

        GD.Print("=== GODOT RUN DONE ===");
        return 0;
    }

    private static EcsSimulation NewSim()
    {
        var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 2, deltaTimeMs: DeltaTimeMs);
        sim.AddSystem(new EntityLifecycleSystem(), SystemPhase.PreUpdate);
        sim.AddSystem(new ArithmeticStressSystem(), SystemPhase.Update);
        sim.AddSystem(new TrigStressSystem(), SystemPhase.PostUpdate);
        sim.AddSystem(new RandomStressSystem(), SystemPhase.LateUpdate);
        sim.Initialize();
        return sim;
    }

    private static void SeedInitialEntities(EcsSimulation sim, int seed)
    {
        // Matches the console tool: 5 empty ticks let EntityLifecycleSystem create initial entities.
        var emptyCommands = new List<ICommand>();
        for (int i = 0; i < 5; i++)
            sim.Tick(emptyCommands);
    }
}
