using System;
using Phyxel.Diagnostics;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.UI;

namespace Phyxel;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--prepare-shaders")
        {
            try
            {
                if (args.Length != 3) throw new ArgumentException("--prepare-shaders <runtime-source> <output-directory>");
                ShaderCatalog.VerifyRuntimeCalls(args[1]);
                ShaderBytecodeStore.Default.WritePackage(args[2]);
            }
            catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
            return;
        }
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_STARTUP") == "1")
        {
            Environment.ExitCode = StartupRegressionVerifier.Run();
            return;
        }
        if(Environment.GetEnvironmentVariable("PHYXEL_VERIFY_FILTER_MODEL")=="1"){Environment.ExitCode=FilterModelRegressionVerifier.Run();return;}
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_GAS_BRUSH") == "1")
        {
            Environment.ExitCode = GasBrushQueueRegressionVerifier.Run();
            return;
        }
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
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_COMBUSTION_MATERIALS") == "1")
        {
            Environment.ExitCode = CombustionMaterialRegressionVerifier.Run();
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
        if (Environment.GetEnvironmentVariable("PHYXEL_VERIFY_PHASE_RUNTIME") == "1")
        {
            Environment.ExitCode = PhaseTransitionRuntimeRegressionVerifier.Run();
            return;
        }

        if(Environment.GetEnvironmentVariable("PHYXEL_VERIFY_ABSORPTION_MODEL")=="1")
        {Environment.ExitCode=AbsorptionModelRegressionVerifier.Run();return;}
        bool diagnostic = false;
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (variable.Key is string name && (name.StartsWith("PHYXEL_VERIFY_", StringComparison.Ordinal) ||
                name.StartsWith("PHYXEL_ACCEPTANCE_", StringComparison.Ordinal)) && variable.Value is string value && value != "0" && value.Length > 0)
                diagnostic = true;
        if (!diagnostic)
        {
            StartupLog.Begin();
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            if (!StartupScreen.Run(ShaderBytecodeStore.Default.Prepare)) return;
        }
        try
        {
            StartupLog.Write("creating game window and D3D11 device");
            using PhyxelGame game = new();
            game.Run();
        }
        catch (Exception e)
        {
            Environment.ExitCode = 1;
            if (diagnostic) Console.Error.WriteLine(e);
            else StartupScreen.ShowFailure(e);
        }
    }
}
