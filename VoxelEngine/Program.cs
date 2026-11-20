namespace VoxelEngine;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--test"))  return EngineTests.Run();
        if (args.Contains("--bench")) return Bench.Run();

        using var game = new VoxelGame();
        game.Run();
        return 0;
    }
}
