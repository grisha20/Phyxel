using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SharpDX.D3DCompiler;
using Phyxel.Core;

namespace Phyxel.Graphics;

internal sealed class ShaderBytecodeStore
{
    internal static readonly ShaderBytecodeStore Default = new(
        Path.Combine(AppContext.BaseDirectory, "Content", "Shaders"),
        Environment.GetEnvironmentVariable("PHYXEL_SHADER_PACKAGE_DIR") ?? Path.Combine(AppContext.BaseDirectory, "Content", "CompiledShaders"),
        Environment.GetEnvironmentVariable("PHYXEL_SHADER_CACHE_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phyxel", "ShaderCache"));

    private readonly string sourceDirectory;
    private readonly string packageDirectory;
    private readonly string cacheDirectory;
    private readonly ConcurrentDictionary<string, byte[]> prepared = new();
    internal int CompiledCount { get; private set; }
    internal int PackageCount { get; private set; }
    internal int CacheCount { get; private set; }

    internal ShaderBytecodeStore(string sourceDirectory, string packageDirectory, string cacheDirectory)
    {
        this.sourceDirectory = sourceDirectory;
        this.packageDirectory = packageDirectory;
        this.cacheDirectory = cacheDirectory;
    }

    internal string ExpandSource(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(sourceDirectory, fileName));
        // Preserve the old expansion order, optimizer and source-based cache key.
        foreach (string include in new[] { "PhysicsShared.hlsli", "PhaseEnthalpy.hlsli", "OxidizerShared.hlsli", "FineAirGeometry.hlsli", "BulkThermalGeometry.hlsli" })
            source = source.Replace("#include \"" + include + "\"", File.ReadAllText(Path.Combine(sourceDirectory, include)), StringComparison.Ordinal);
        return source;
    }

    internal static string Key(ShaderProgram program, string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"phyxel-compute-shader-v1\0{program.EntryPoint}\0{source}")));

    internal byte[] Get(ShaderProgram program, Action<string>? progress = null)
    {
        string source = ExpandSource(program.FileName);
        string key = Key(program, source);
        if (prepared.TryGetValue(key, out byte[]? memory)) return memory;
        string name = program.FileName + " / " + program.EntryPoint;
        byte[]? bytes = ReadValid(Path.Combine(packageDirectory, key + ".cso"), requireDigest: true);
        if (bytes is not null)
        {
            PackageCount++;
            progress?.Invoke("Загрузка готовых шейдеров: " + name);
            StartupLog.Write("package " + name);
        }
        else if ((bytes = ReadValid(Path.Combine(cacheDirectory, key + ".cso"), requireDigest: false)) is not null)
        {
            CacheCount++;
            progress?.Invoke("Загрузка кеша: " + name);
            StartupLog.Write("cache " + name);
        }
        else
        {
            progress?.Invoke("Компиляция шейдера: " + name);
            StartupLog.Write("compile-start " + name);
            if (Environment.GetEnvironmentVariable("PHYXEL_SHADER_TRACE") == "1")
                Console.WriteLine("PHYXEL_SHADER_COMPILE " + program.FileName + " " + program.EntryPoint);
            Stopwatch watch = Stopwatch.StartNew();
            using CompilationResult compilation = ShaderBytecode.Compile(source, program.EntryPoint, "cs_5_0",
                ShaderFlags.OptimizationLevel3, EffectFlags.None, null, null, Path.Combine(sourceDirectory, program.FileName));
            bytes = compilation.Bytecode.Data;
            CompiledCount++;
            StartupLog.Write($"compile-end {name} seconds={watch.Elapsed.TotalSeconds:F3}");
            TryWriteCache(Path.Combine(cacheDirectory, key + ".cso"), bytes);
        }
        prepared[key] = bytes;
        return bytes;
    }

    internal void Prepare(Action<int, int, string> progress, CancellationToken cancellation)
    {
        for (int i = 0; i < ShaderCatalog.Programs.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            int completed = i;
            Get(ShaderCatalog.Programs[i], message => progress(completed, ShaderCatalog.Programs.Length, message));
            cancellation.ThrowIfCancellationRequested();
            progress(i + 1, ShaderCatalog.Programs.Length, "Подготовка графики");
        }
        StartupLog.Write($"prepared package={PackageCount} cache={CacheCount} compiled={CompiledCount}");
    }

    internal void WritePackage(string outputDirectory, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var files = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ShaderProgram program in ShaderCatalog.Programs)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] bytes = Get(program);
            string fileName = Key(program, ExpandSource(program.FileName)) + ".cso";
            string path = Path.Combine(outputDirectory, fileName);
            files.Add(fileName); files.Add(fileName + ".sha256");
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) AtomicWrite(path, bytes);
            byte[] digest = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)));
            if (!File.Exists(path + ".sha256") || !File.ReadAllBytes(path + ".sha256").AsSpan().SequenceEqual(digest))
                AtomicWrite(path + ".sha256", digest);
        }
        // Only our generated files in the explicitly supplied output folder.
        foreach (string path in Directory.EnumerateFiles(outputDirectory, "*.cso*"))
            if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^[0-9A-F]{64}\.cso(?:\.sha256)?$") &&
                !files.Contains(Path.GetFileName(path))) File.Delete(path);
        Console.WriteLine($"PHYXEL_SHADER_PACKAGE programs={ShaderCatalog.Programs.Length} package={PackageCount} cache={CacheCount} compiled={CompiledCount}");
    }

    private static byte[]? ReadValid(string path, bool requireDigest)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            // GetVersion is intentionally preceded by size checks: SharpDX's
            // decoder assumes a well-formed DXBC container.
            if (bytes.Length < 32 || Encoding.ASCII.GetString(bytes, 0, 4) != "DXBC" ||
                BitConverter.ToUInt32(bytes, 24) != bytes.Length) return null;
            uint chunks = BitConverter.ToUInt32(bytes, 28);
            if (chunks > (bytes.Length - 32) / 4) return null;
            for (int i = 0; i < chunks; i++)
            {
                uint offset = BitConverter.ToUInt32(bytes, 32 + 4 * i);
                if (offset > bytes.Length - 8) return null;
                uint size = BitConverter.ToUInt32(bytes, (int)offset + 4);
                if (size > bytes.Length - offset - 8) return null;
            }
            if (requireDigest || File.Exists(path + ".sha256"))
                if (!string.Equals(File.ReadAllText(path + ".sha256").Trim(), Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal)) return null;
            using ShaderBytecode code = new(bytes);
            return code.GetVersion().ToString() == "cs_5_0" ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException or SharpDX.SharpDXException)
        {
            return null;
        }
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void TryWriteCache(string path, byte[] bytes)
    {
        try
        {
            AtomicWrite(path, bytes);
            AtomicWrite(path + ".sha256", Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(bytes))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StartupLog.Write("cache-write-unavailable " + e.Message);
        }
    }
}
