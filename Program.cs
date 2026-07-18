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
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_THERMAL_MATERIALS") == "1")
        {
            Environment.ExitCode = ThermalMaterialPropertiesRegressionVerifier.Run();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_THERMAL_DIFFUSION") == "1")
        {
            Environment.ExitCode = ThermalDiffusionRegressionVerifier.Run();
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_PHASE_MATERIALS") == "1")
        {
            Environment.ExitCode = PhaseTransitionMaterialRegressionVerifier.Run();
            return;
        }

        using PhyxelGame game = new();
        game.Run();
    }
}
