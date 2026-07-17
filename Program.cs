using System;
using Phyxel.Diagnostics;

namespace Phyxel;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_WORLD_CODEC") == "1")
        {
            Environment.ExitCode = WorldCellCodecRegressionVerifier.Run();
            return;
        }

        using PhyxelGame game = new();
        game.Run();
    }
}
