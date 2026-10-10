using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Phyxel.Core;

internal static class StartupLog
{
    private static readonly object Sync = new();
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static string? path;
    internal static string? Path => path;

    internal static void Begin()
    {
        try
        {
            string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phyxel", "Logs");
            Directory.CreateDirectory(directory);
            // Unique per process/run: another launch doesn't overwrite evidence.
            path = System.IO.Path.Combine(directory, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            Write($"start version={typeof(StartupLog).Assembly.GetName().Version} os={Environment.OSVersion} 64bit={Environment.Is64BitProcess}");
            var assembly=typeof(StartupLog).Assembly;
            Write($"runtime executable={Environment.ProcessPath} assembly={assembly.Location}");
            Write($"build configuration={assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration} revision={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} assemblySha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static void Write(string message)
    {
        if (path is null) return;
        lock (Sync)
        {
            try { File.AppendAllText(path, $"{Watch.Elapsed.TotalSeconds:F3}s {message}{Environment.NewLine}", Encoding.UTF8); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
