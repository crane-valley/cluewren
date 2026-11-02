using PlaytestAgent.Kit;

namespace PlaytestAgent.Tests;

public class PoseSmokeTests
{
    sealed class PoseHarness : Harness
    {
        public override string Goal => "Watch the player";

        public override double TickHz => 60;

        public override async Task<Obs> Observe(Game game) =>
            new() { ["_pose"] = await game.Pose() };

        public override List<Option> Options(Obs obs) =>
            [new Option("watch", "Watch the player", (g, o) => Wait(g, 0.05))];

        public override Status Status(Obs obs) =>
            Runtime.PoseAt(obs) is { } at && at.Z == 90 ? new Status(Kit.Status.Done, "seen") : new Status();

        static Task<Result> Wait(Game game, double seconds) => Control.Wait(game, seconds);
    }

    [Fact]
    public async Task The_tick_loop_reads_a_pose_without_jev()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pose-{Guid.NewGuid():N}.jsonl");
        try
        {
            var result = await new Runtime(new Game(new StillGame()), new PoseHarness(), path, maxSeconds: 5).Run();
            Assert.Equal(Status.Done, result.Status!.State);
            Assert.Equal(0, result.Decisions);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
