using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.UI;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

internal static class StartupRegressionVerifier
{
    private static int checks;
    internal static int Run()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            StartupLog.Begin();
            string root = Path.GetFullPath(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR") ?? "artifacts/startup");
            Directory.CreateDirectory(root);
            if (Environment.GetEnvironmentVariable("PHYXEL_STARTUP_COLD") == "1")
            {
                string cache = Path.Combine(root, "cold-cache-" + Guid.NewGuid().ToString("N"));
                var cold = new ShaderBytecodeStore(Path.Combine(AppContext.BaseDirectory, "Content", "Shaders"), Path.Combine(root, "absent-package"), cache);
                using var form = new StartupScreen(cold.Prepare);
                Stopwatch watch = Stopwatch.StartNew();
                double timeout = double.TryParse(Environment.GetEnvironmentVariable("PHYXEL_STARTUP_COLD_SECONDS"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double requested)
                    && requested > 0 ? requested : 600;
                bool captured = false;
                form.DiagnosticTick += screen =>
                {
                    if (!captured && screen.MessageLoopTicks >= 10)
                    {
                        Capture(screen, Path.Combine(root, "cold-live.png"));
                        captured = true;
                    }
                    if (watch.Elapsed.TotalSeconds >= timeout)
                    {
                        Capture(screen, Path.Combine(root, "cold-stopped.png"));
                        screen.Close();
                    }
                };
                Application.Run(form);
                if (!form.Ready)
                {
                    Console.WriteLine($"PHYXEL_STARTUP_COLD_INCOMPLETE seconds={watch.Elapsed.TotalSeconds:F3} ticks={form.MessageLoopTicks} compiled={cold.CompiledCount} failed={form.Failed}");
                    return 2;
                }
                Check(form.Ready && !form.Failed, "real cold preparation completed");
                Check(cold.CompiledCount == ShaderCatalog.Programs.Length, "every cold program actually compiled");
                Check(captured && form.MessageLoopTicks > 10, "message loop continued during real compilation");
                Console.WriteLine($"PHYXEL_STARTUP_COLD seconds={watch.Elapsed.TotalSeconds:F3} ticks={form.MessageLoopTicks} compiled={cold.CompiledCount}");
                return 0;
            }

            string fixture = Path.Combine(root, "fixture-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(fixture, "source");
            string package = Path.Combine(fixture, "package");
            string cacheDir = Path.Combine(fixture, "cache");
            Directory.CreateDirectory(source);
            foreach (string include in new[] { "PhysicsShared.hlsli", "PhaseEnthalpy.hlsli", "OxidizerShared.hlsli", "FineAirGeometry.hlsli", "BulkThermalGeometry.hlsli" })
                File.WriteAllText(Path.Combine(source, include), "// " + include);
            var program = new ShaderProgram("Tiny.hlsl");
            string path = Path.Combine(source, program.FileName);
            const string tiny = "#include \"PhysicsShared.hlsli\"\nRWStructuredBuffer<uint> Result : register(u0); [numthreads(1,1,1)] void CSMain(uint3 id:SV_DispatchThreadID){ Result[id.x]=17; }";
            File.WriteAllText(path, tiny);
            var compile = new ShaderBytecodeStore(source, package, cacheDir);
            byte[] original = compile.Get(program);
            string key = ShaderBytecodeStore.Key(program, compile.ExpandSource(program.FileName));
            Check(compile.CompiledCount == 1 && original.Length > 0, "cold tiny shader compiles");
            Check(compile.Get(program).SequenceEqual(original) && compile.CompiledCount == 1, "in-memory reuse");
            var cached = new ShaderBytecodeStore(source, package, cacheDir);
            Check(cached.Get(program).SequenceEqual(original) && cached.CacheCount == 1 && cached.CompiledCount == 0, "warm disk reuse");
            Directory.CreateDirectory(package);
            string packed = Path.Combine(package, key + ".cso");
            ShaderBytecodeStore.AtomicWrite(packed, original);
            ShaderBytecodeStore.AtomicWrite(packed + ".sha256", System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original))));
            string unavailableCache = Path.Combine(fixture, "not-a-directory");
            File.WriteAllText(unavailableCache, "cache cannot be written here");
            var packaged = new ShaderBytecodeStore(source, package, unavailableCache);
            Check(packaged.Get(program).SequenceEqual(original) && packaged.PackageCount == 1 && packaged.CompiledCount == 0, "package works without writable or populated cache");
            File.WriteAllText(packed + ".sha256", "wrong digest");
            var corrupt = new ShaderBytecodeStore(source, package, cacheDir);
            Check(corrupt.Get(program).SequenceEqual(original) && corrupt.CacheCount == 1, "corrupt package falls back to valid cache");
            File.WriteAllBytes(Path.Combine(cacheDir, key + ".cso"), [1, 2, 3]);
            var truncated = new ShaderBytecodeStore(source, package, cacheDir);
            Check(truncated.Get(program).SequenceEqual(original) && truncated.CompiledCount == 1, "truncated cache and bad package are repaired");
            var noWrite = new ShaderBytecodeStore(source, package, unavailableCache);
            Check(noWrite.Get(program).Length > 0 && noWrite.CompiledCount == 1, "unwritable cache doesn't prevent compilation");
            File.WriteAllText(path, tiny.Replace("=17", "=23", StringComparison.Ordinal));
            var changed = new ShaderBytecodeStore(source, package, cacheDir);
            Check(key != ShaderBytecodeStore.Key(program, changed.ExpandSource(program.FileName)) &&
                !changed.Get(program).SequenceEqual(original) && changed.CompiledCount == 1, "source change invalidates package and cache");
            string previousKey = ShaderBytecodeStore.Key(program, changed.ExpandSource(program.FileName));
            File.AppendAllText(Path.Combine(source, "PhysicsShared.hlsli"), "\n// changed shared source\n");
            Check(previousKey != ShaderBytecodeStore.Key(program, changed.ExpandSource(program.FileName)), "included source invalidates key");

            using (var form = new StartupScreen((report, cancellation) =>
            {
                report(2, 85, "Компиляция шейдера: CellularAutomataSolver.hlsl / CSMain");
                cancellation.WaitHandle.WaitOne(1250);
                cancellation.ThrowIfCancellationRequested();
                report(85, 85, "Подготовка графики");
            }))
            {
                bool captured = false;
                form.DiagnosticTick += screen =>
                {
                    if (!captured && screen.MessageLoopTicks >= 4) { Capture(screen, Path.Combine(root, "loading.png")); captured = true; }
                };
                Application.Run(form);
                Check(form.Ready && captured && form.MessageLoopTicks >= 8, "window paints and pumps messages during worker preparation");
            }
            Stopwatch cancelWatch = Stopwatch.StartNew();
            using (var form = new StartupScreen((report, _) =>
            {
                report(0, 1, "Компиляция…");
                Thread.Sleep(2000); // Models an uninterruptible native compiler.
            }))
            {
                form.DiagnosticTick += screen => { if (screen.MessageLoopTicks == 2) screen.Close(); };
                Application.Run(form);
                Check(!form.Ready && cancelWatch.Elapsed.TotalSeconds < 1, "close never waits for native compiler");
            }
            using (var form = new StartupScreen((_, _) => throw new InvalidDataException("Проверочная ошибка шейдера")))
            using (var monitor = new System.Windows.Forms.Timer { Interval = 100 })
            {
                monitor.Tick += (_, _) =>
                {
                    if (form.Failed) { Capture(form, Path.Combine(root, "error.png")); form.Close(); }
                };
                monitor.Start();
                Application.Run(form);
                Check(form.Failed && !form.Ready, "preparation error is visible and cannot enter game");
                Environment.ExitCode = 0; // Expected error fixture.
            }

            // These are the shipped production bytecodes, loaded by a real D3D11
            // device. No physics or GPU dispatch is needed to validate packaging.
            using (var device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None,
                SharpDX.Direct3D.FeatureLevel.Level_11_1))
            {
                Console.WriteLine("PHYXEL_STARTUP_D3D feature=" + device.FeatureLevel);
                foreach (ShaderProgram shipped in ShaderCatalog.Programs)
                {
                    Console.WriteLine("PHYXEL_STARTUP_D3D_LOAD " + shipped.FileName + "/" + shipped.EntryPoint);
                    byte[] bytes = ShaderBytecodeStore.Default.Get(shipped);
                    using var code = new SharpDX.D3DCompiler.ShaderBytecode(bytes);
                    using var shader = new ComputeShader(device, code);
                    Check(bytes.Length > 0, "D3D11 " + shipped.FileName + "/" + shipped.EntryPoint);
                }
                Check(ShaderBytecodeStore.Default.PackageCount == ShaderCatalog.Programs.Length && ShaderBytecodeStore.Default.CompiledCount == 0,
                    "all production shaders loaded from package without compiling");
            }
            Console.WriteLine($"PHYXEL_STARTUP_PASS checks={checks} programs={ShaderCatalog.Programs.Length}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("PHYXEL_STARTUP_FAIL " + e); return 1; }
    }

    private static void Capture(Form form, string path)
    {
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException(name);
        checks++;
    }
}
