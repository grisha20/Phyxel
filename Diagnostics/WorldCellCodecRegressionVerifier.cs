using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

internal static class WorldCellCodecRegressionVerifier
{
    private const uint WorldFileMagic = 0x5058594C;
    private const int LegacyHeaderSize = 20;
    private const int CurrentHeaderSize = 24;

    public static int Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("PHYXEL_WORLD_CODEC_SUCCESS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYXEL_WORLD_CODEC_FAILED {exception}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"phyxel-world-codec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string externalMaterials = Path.Combine(directory, "external-materials");
            Directory.CreateDirectory(externalMaterials);
            MaterialRegistry materials = new(externalMaterials);
            SimulationStateSerializer serializer = new();

            VerifyLayoutContracts();
            VerifyGasSchedulerContract();
            await VerifyV3Async(directory, serializer, materials);
            await VerifyV4MigrationsAsync(directory, serializer, materials);
            await VerifyV5RoundTripAsync(directory, serializer, materials);
            VerifyV5Migration();
            await VerifyV5RuntimeRemapAsync(directory);
            await VerifyOxidizerAsync(directory, serializer, materials);
            await VerifyFusionVersionAsync(directory, serializer, materials);
            var heat = new System.Numerics.Vector2[] { new(17.3f,.025f) };
            var thermal = MemoryMarshal.AsBytes(heat.AsSpan()).ToArray();
            var heatWorld = new SimulationWorldSnapshot(3,1,new byte[3*WorldCellCodec.CurrentCellStride],AirThermal:thermal);
            string heatPath = Path.Combine(directory,"carrier-heat.json");
            await serializer.SaveAsync(heatPath,new SimulationSettings(),materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand),heatWorld,materials);
            var heatLoaded = await serializer.LoadAsync(heatPath,materials);
            Require(heatLoaded?.World?.AirThermal is not null && heatLoaded.World.AirThermal.AsSpan().SequenceEqual(thermal),"Carrier heat did not round-trip.");
            // Explicit old-v9 fixture: strip only v10's four empty extensions.
            byte[] v9=await File.ReadAllBytesAsync(Path.ChangeExtension(heatPath,".world"));
            // Pack historical stride before stripping the later extensions.
            byte[] oldV9=RepackWorldPrefix(v9,3,40);
            v9=oldV9;
            BinaryPrimitives.WriteInt32LittleEndian(v9.AsSpan(16,4),40);
            BinaryPrimitives.WriteInt32LittleEndian(v9.AsSpan(20,4),120);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(v9.AsSpan(4,4),9);
            string v9Path=Path.Combine(directory,"legacy-v9.json");
            var v9Json=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(heatPath))!.AsObject();v9Json["Version"]=9;
            await File.WriteAllTextAsync(v9Path,v9Json.ToJsonString());
            await File.WriteAllBytesAsync(Path.ChangeExtension(v9Path,".world"),v9[..^16]);
            var v9Loaded=await serializer.LoadAsync(v9Path,materials);
            Require(v9Loaded?.World?.AirThermal is { } oldHeat && oldHeat.AsSpan().SequenceEqual(thermal) && v9Loaded.World.ReactionPulse is null,
                "Legacy v9 thermal save did not migrate without a pulse.");
            foreach(var invalid in new System.Numerics.Vector4[] { new(-1,0,0,0),new(1,float.NaN,1,0),new(1,1,0,0),new(1,1,1,1) })
            {
                var bad=new System.Numerics.Vector4[3];bad[0]=invalid;
                ExpectInvalid(()=>WorldCellCodec.ValidateReactionState(3,1,MemoryMarshal.AsBytes(bad.AsSpan()).ToArray(),null,null,null));
            }
            foreach(var invalid in new System.Numerics.Vector4[] { new(float.NaN,0,0,0),new(257,0,0,0),new(1,65,0,0),new(1,0,0,-1) })
                ExpectInvalid(()=>WorldCellCodec.ValidateReactionState(3,1,null,MemoryMarshal.AsBytes(new[]{invalid}.AsSpan()).ToArray(),null,null));
            foreach(var invalid in new System.Numerics.Vector2[] { new(float.NaN,1),new(1,-1),new(1,0),new(6000,1),new(float.MaxValue,1e-35f) })
            {
                var invalidBytes=MemoryMarshal.AsBytes(new[] { invalid }.AsSpan()).ToArray();
                ExpectInvalid(()=>WorldCellCodec.ValidateAirThermal(3,1,invalidBytes));
            }
            await VerifySimulationModesAsync(directory, serializer, materials);
            await VerifyCorruptWorldsAsync(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    internal static byte[] RepackWorldPrefix(byte[] bytes, int count, int stride)
    {
        int current=WorldCellCodec.CurrentCellStride;
        byte[] legacy=new byte[bytes.Length-count*(current-stride)];
        bytes.AsSpan(0,28).CopyTo(legacy);
        for(int i=0;i<count;i++) bytes.AsSpan(28+i*current,stride).CopyTo(legacy.AsSpan(28+i*stride));
        bytes.AsSpan(28+count*current).CopyTo(legacy.AsSpan(28+count*stride));
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(16,4),stride);
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(20,4),count*stride);
        return legacy;
    }

    private static async Task VerifyFusionVersionAsync(string directory, SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        var cells = new GridCell[3];
        cells[0] = new GridCell { IsActive=1, MaterialIndex=materials.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            Mass=1, Temperature=0, Lifetime=-100 };
        string path=Path.Combine(directory,"fusion-version.json");
        await serializer.SaveAsync(path,new SimulationSettings(),(ushort)cells[0].MaterialIndex,
            new SimulationWorldSnapshot(3,1,MemoryMarshal.AsBytes(cells.AsSpan()).ToArray()),materials);
        var loaded=await serializer.LoadAsync(path,materials);
        Require(MemoryMarshal.Cast<byte,GridCell>(loaded!.World!.Grid)[0].Lifetime==-100,"v11 freezing progress lost");
        byte[] bytes=await File.ReadAllBytesAsync(Path.ChangeExtension(path,".world"));
        var json=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        // Repack the current 48-byte cells into the actual historical 40-byte layout.
        byte[] legacy=RepackWorldPrefix(bytes,3,40);
        bytes=legacy;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16,4),40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20,4),120);
        json["Version"]=10;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4,4),10);
        await File.WriteAllTextAsync(path,json.ToJsonString());
        await File.WriteAllBytesAsync(Path.ChangeExtension(path,".world"),bytes);
        bool rejected=false;
        try { await serializer.LoadAsync(path,materials); } catch(InvalidDataException) { rejected=true; }
        Require(rejected,"Legacy v10 negative lifetime accepted");
        // v10 has the same payload layout; legitimate positive latent progress remains readable.
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28+36,4),100);
        await File.WriteAllBytesAsync(Path.ChangeExtension(path,".world"),bytes);
        loaded=await serializer.LoadAsync(path,materials);
        Require(MemoryMarshal.Cast<byte,GridCell>(loaded!.World!.Grid)[0].Lifetime==100,"Legacy v10 phase progress lost");
        // A forged v11 negative lifetime on smoke is rejected after palette remapping.
        json["Version"]=11;
        var palette=json["MaterialPalette"]!.AsArray();
        for(int i=0;i<palette.Count;i++) if(palette[i]!.GetValue<string>()==CoreMaterialIds.Water) palette[i]=CoreMaterialIds.Smoke;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4,4),11);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28+36,4),-100);
        await File.WriteAllTextAsync(path,json.ToJsonString());
        await File.WriteAllBytesAsync(Path.ChangeExtension(path,".world"),bytes);
        rejected=false;
        try { await serializer.LoadAsync(path,materials); } catch(InvalidDataException) { rejected=true; }
        Require(rejected,"Negative lifetime on non-fusion material accepted after remap");
    }

    private static async Task VerifySimulationModesAsync(string directory, SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        Require(new SimulationSettings().Mode == SimulationMode.Sandbox, "New scene must default to Sandbox.");
        var grid = new byte[320 * 180 * System.Runtime.InteropServices.Marshal.SizeOf<GridCell>()];
        var oxygen = new float[320 * 180];
        Array.Fill(oxygen, .37f);
        var world = new SimulationWorldSnapshot(320, 180, grid,
            Oxidizer: MemoryMarshal.AsBytes(oxygen.AsSpan()).ToArray());
        foreach (SimulationMode mode in Enum.GetValues<SimulationMode>())
        {
            string path = Path.Combine(directory, $"mode-{mode}.json");
            await serializer.SaveAsync(path, new SimulationSettings { Mode = mode },
                materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand), world, materials);
            var loaded = await serializer.LoadAsync(path, materials) ?? throw new InvalidDataException("Missing mode scene.");
            var settings = new SimulationSettings();
            SimulationStateSerializer.Apply(loaded.State, settings);
            Require(settings.Mode == mode && loaded.World is not null &&
                loaded.World.Oxidizer!.AsSpan().SequenceEqual(world.Oxidizer), "Mode/oxidizer roundtrip changed.");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json.Remove("Mode");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var legacy = await serializer.LoadAsync(path, materials);
            Require(legacy?.State.Mode == SimulationMode.Simulation, "Existing scene changed combustion rules.");
            json["Mode"] = 999;
            await File.WriteAllTextAsync(path, json.ToJsonString());
            bool invalidRejected = false;
            try { await serializer.LoadAsync(path, materials); }
            catch (InvalidDataException) { invalidRejected = true; }
            Require(invalidRejected, "Unknown scene mode was accepted.");
        }
        Console.WriteLine("[PASS] Scene modes roundtrip; legacy scenes retain Simulation; oxygen is preserved.");
    }

    private static void VerifyLayoutContracts()
    {
        WorldCellCodec.ValidateLayoutContracts();
        Require(
            Marshal.SizeOf<LegacyGridCellV3V4>() == WorldCellCodec.LegacyCellStride,
            "LegacyGridCellV3V4 must remain 32 bytes.");
        Require(
            Marshal.SizeOf<LegacyGridCellV5>() == WorldCellCodec.V5CellStride,
            "LegacyGridCellV5 must remain 36 bytes.");
        Require(
            Marshal.SizeOf<GridCell>() == WorldCellCodec.CurrentCellStride,
            "GridCell must be 52 bytes.");
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders", "PhysicsShared.hlsli");
        string shader = File.ReadAllText(shaderPath);
        int layoutStart = shader.IndexOf("struct GridCell", StringComparison.Ordinal);
        int layoutEnd = shader.IndexOf("};", layoutStart, StringComparison.Ordinal);
        Require(layoutStart >= 0 && layoutEnd > layoutStart, "HLSL GridCell declaration is missing.");
        string layout = shader[layoutStart..layoutEnd];
        string[] fields =
        [
            "uint MaterialIndex;", "float Mass;", "float VelocityX;", "float VelocityY;",
            "float Pressure;", "uint IsActive;", "uint BodyId;", "uint RestFrames;",
            "float Temperature;", "float Lifetime;", "float MoistureMass;", "float MoistureEnergy;", "float FuelMass;"
        ];
        int previous = -1;
        foreach (string field in fields)
        {
            int position = layout.IndexOf(field, StringComparison.Ordinal);
            Require(position > previous, $"HLSL GridCell field '{field}' is missing or out of order.");
            previous = position;
        }
    }

    private static void VerifyGasSchedulerContract()
    {
        foreach ((int framesPerSecond, int frames) in new[]
        {
            (30, 30), (60, 60), (100, 100), (144, 144)
        })
        {
            FixedStepGasScheduler scheduler = new();
            int dispatches = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                dispatches += scheduler.Advance(1d / framesPerSecond, false, true);
            }
            Require(scheduler.TotalTicks == 120 && dispatches == 120,
                $"Gas fixed-step schedule differs at {framesPerSecond} FPS: " +
                $"ticks/dispatches={scheduler.TotalTicks}/{dispatches}.");
        }

        FixedStepGasScheduler paused = new();
        Require(paused.Advance(0.005, false, true) == 0,
            "Gas scheduler unexpectedly ticked before one fixed step.");
        for (int frame = 0; frame < 120; frame++)
        {
            Require(paused.Advance(1d / 30d, true, true) == 0,
                "Gas scheduler advanced while paused.");
        }
        Require(paused.Advance(0.004, false, true) == 1 && paused.TotalTicks == 1,
            "Gas scheduler accumulated paused time or lost its pre-pause fraction.");
        paused.Reset();
        Require(paused.TotalTicks == 0,
            "Gas scheduler Reset did not clear total ticks.");
    }

    private static async Task VerifyV3Async(
        string directory,
        SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        string scenePath = Path.Combine(directory, "legacy-v3.json");
        await WriteJsonAsync(scenePath, new
        {
            Version = 3,
            Scale = 0.25f,
            Gravity = 980f,
            BrushRadius = 18,
            SpawnDensity = 0.82f,
            SolidGravity = true,
            SelectedMaterial = 4u,
            SavedAt = DateTimeOffset.UnixEpoch,
            HydraulicPressure = false
        });
        LegacyGridCellV3V4 stone = CreateLegacyCell(4, 9.2f, -3.5f, 7.25f, 2.75f, 1, 41, 12);
        LegacyGridCellV3V4 dirtyInactive = CreateLegacyCell(2, 4, 1, 2, 3, 0, 99, 8);
        await WriteLegacyWorldAsync(
            Path.ChangeExtension(scenePath, ".world"),
            3,
            2,
            1,
            EncodeLegacyCells(stone, dirtyInactive));

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, materials) ??
            throw new InvalidOperationException("Synthetic v3 scene did not load.");
        uint stoneIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Stone);
        Require(loaded.State.SelectedMaterial == stoneIndex, "v3 selected material index 4 did not map to core:stone.");
        AssertCells(
            loaded.World,
            CreateCurrentCell(stone, stoneIndex, 20f),
            default);
    }

    private static async Task VerifyV4MigrationsAsync(
        string directory,
        SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        string scenePath = Path.Combine(directory, "migrations-v4.json");
        await WriteJsonAsync(scenePath, new
        {
            Version = 4,
            Scale = 0.25f,
            Gravity = 980f,
            BrushRadius = 18,
            SpawnDensity = 0.82f,
            SolidGravity = false,
            SelectedMaterialId = "core:concrete",
            SavedAt = DateTimeOffset.UnixEpoch,
            HydraulicPressure = false,
            MaterialPalette = new[] { "core:empty", "core:gold_sand", "core:concrete", "core:gas" }
        });
        LegacyGridCellV3V4 goldSand = CreateLegacyCell(1, 1.5f, 4.25f, -8.5f, 0, 1, 0, 3);
        LegacyGridCellV3V4 concrete = CreateLegacyCell(2, 9.2f, 0.5f, 1.5f, 6.5f, 1, 52, 14);
        LegacyGridCellV3V4 gas = CreateLegacyCell(3, 0.75f, 1.25f, -2.5f, 0, 1, 0, 4);
        LegacyGridCellV3V4 dirtyInactive = CreateLegacyCell(1, 2, 3, 4, 5, 0, 6, 7);
        await WriteLegacyWorldAsync(
            Path.ChangeExtension(scenePath, ".world"),
            4,
            4,
            1,
            EncodeLegacyCells(goldSand, concrete, gas, dirtyInactive));

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, materials) ??
            throw new InvalidOperationException("Synthetic v4 scene did not load.");
        uint sandIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint stoneIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Stone);
        uint co2Index = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Co2);
        Require(loaded.State.SelectedMaterial == stoneIndex, "core:concrete selected material did not migrate to core:stone.");
        Require(ContainsWarning(loaded.Warnings, "core:gold_sand"), "core:gold_sand migration warning is missing.");
        Require(ContainsWarning(loaded.Warnings, "core:concrete"), "core:concrete migration warning is missing.");
        Require(ContainsWarning(loaded.Warnings, "core:gas"), "core:gas migration warning is missing.");
        AssertCells(
            loaded.World,
            CreateCurrentCell(goldSand, sandIndex, 20f),
            CreateCurrentCell(concrete, stoneIndex, 20f),
            CreateCurrentCell(gas, co2Index, 20f),
            default);
    }

    private static async Task VerifyV5RoundTripAsync(
        string directory,
        SimulationStateSerializer serializer,
        MaterialRegistry materials)
    {
        uint sandIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
        uint stoneIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Stone);
        GridCell sand = new()
        {
            MaterialIndex = sandIndex,
            Mass = 1.5f,
            VelocityX = -12.25f,
            VelocityY = 28.5f,
            Pressure = 0.75f,
            IsActive = 1,
            BodyId = 0,
            RestFrames = 11,
            Temperature = -120.5f,
            Lifetime = 0.75f
        };
        GridCell stone = new()
        {
            MaterialIndex = stoneIndex,
            Mass = 9.2f,
            VelocityX = 2.5f,
            VelocityY = 3.75f,
            Pressure = 1.25f,
            IsActive = 1,
            BodyId = 701,
            RestFrames = 19,
            Temperature = 1450.25f,
            Lifetime = 2.25f
        };
        GridCell dirtyInactive = new()
        {
            MaterialIndex = uint.MaxValue,
            Mass = 9,
            VelocityX = 8,
            VelocityY = 7,
            Pressure = 6,
            IsActive = 0,
            BodyId = 5,
            RestFrames = 4,
            Temperature = float.NaN
        };
        SimulationWorldSnapshot source = CreateSnapshot(3, 1, sand, stone, dirtyInactive);
        string scenePath = Path.Combine(directory, "roundtrip-v5.json");
        SimulationSettings settings = new();
        await serializer.SaveAsync(
            scenePath,
            settings,
            checked((ushort)sandIndex),
            source,
            materials);

        string worldPath = Path.ChangeExtension(scenePath, ".world");
        RawWorldFile raw = await SimulationStateSerializer.ReadWorldAsync(worldPath, CancellationToken.None) ??
            throw new InvalidOperationException("Saved v5 world file is missing.");
        Require(raw.Version == 14, "CurrentVersion is not 14.");
        Require(raw.StoredCellStride == 52, "Current writer did not store the explicit 52-byte stride.");
        Require(new FileInfo(worldPath).Length == CurrentHeaderSize + 24 + raw.CellBytes.Length,
            "v10 world did not preserve its header and empty extensions.");

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, materials) ??
            throw new InvalidOperationException("Saved v5 scene did not reload.");
        AssertCells(loaded.World, sand, stone, default);
    }

    private static void VerifyV5Migration()
    {
        LegacyGridCellV5 legacy = new()
        {
            MaterialIndex = 4,
            Mass = 0.04f,
            VelocityY = -8,
            IsActive = 1,
            RestFrames = 2,
            Temperature = 650
        };
        SimulationWorldSnapshot migrated = WorldCellCodec.Decode(
            new RawWorldFile(5, 1, 1, WorldCellCodec.V5CellStride, EncodeV5Cells(legacy)));
        ReadOnlySpan<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(migrated.Grid);
        Require(cells.Length == 1 && cells[0].MaterialIndex == legacy.MaterialIndex &&
            SameFloat(cells[0].Temperature, legacy.Temperature) && cells[0].Lifetime == 0,
            "v5 world did not migrate to v6 GridCell with zero transient lifetime.");
    }

    private static async Task VerifyV5RuntimeRemapAsync(string directory)
    {
        string firstMaterials = Path.Combine(directory, "runtime-order-a");
        string secondMaterials = Path.Combine(directory, "runtime-order-b");
        Directory.CreateDirectory(firstMaterials);
        Directory.CreateDirectory(secondMaterials);
        string zMaterial = CreateExternalMaterialJson("test:z_material");
        await File.WriteAllTextAsync(Path.Combine(firstMaterials, "z.json"), zMaterial);
        await File.WriteAllTextAsync(Path.Combine(secondMaterials, "a.json"), CreateExternalMaterialJson("test:a_material"));
        await File.WriteAllTextAsync(Path.Combine(secondMaterials, "z.json"), zMaterial);

        MaterialRegistry firstRegistry = new(firstMaterials);
        MaterialRegistry secondRegistry = new(secondMaterials);
        ushort firstIndex = firstRegistry["test:z_material"].RuntimeIndex;
        ushort secondIndex = secondRegistry["test:z_material"].RuntimeIndex;
        Require(firstIndex != secondIndex, "Runtime-order test did not move the material index.");
        GridCell sourceCell = new()
        {
            MaterialIndex = firstIndex,
            Mass = 2.5f,
            VelocityX = 3,
            VelocityY = 4,
            IsActive = 1,
            RestFrames = 5,
            Temperature = 777.25f
        };
        string scenePath = Path.Combine(directory, "runtime-remap-v5.json");
        SimulationStateSerializer serializer = new();
        await serializer.SaveAsync(
            scenePath,
            new SimulationSettings(),
            firstIndex,
            CreateSnapshot(1, 1, sourceCell),
            firstRegistry);

        LoadedSimulationScene loaded = await serializer.LoadAsync(scenePath, secondRegistry) ??
            throw new InvalidOperationException("Runtime-remap v5 scene did not load.");
        sourceCell.MaterialIndex = secondIndex;
        AssertCells(loaded.World, sourceCell);
    }

    private static async Task VerifyOxidizerAsync(string directory, SimulationStateSerializer serializer, MaterialRegistry materials)
    {
        float[] concentrations = [0, .1234567f, 2.75f];
        byte[] oxygen = MemoryMarshal.AsBytes(concentrations.AsSpan()).ToArray();
        GridCell cell = new() { MaterialIndex = materials.GetRequiredRuntimeIndex(CoreMaterialIds.Sand),
            IsActive = 1, Mass = 1, Temperature = 20 };
        SimulationWorldSnapshot world = CreateSnapshot(3, 1, cell, default, default) with { Oxidizer = oxygen };
        string path = Path.Combine(directory, "oxidizer-v8.json");
        await serializer.SaveAsync(path, new SimulationSettings { OpenBoundaries = false }, (ushort)cell.MaterialIndex, world, materials);
        var scene = await serializer.LoadAsync(path, materials);
        Require(scene?.State.OpenBoundaries == false, "v8 did not preserve the oxygen boundary condition.");
        var loaded = scene?.World;
        Require(loaded?.Oxidizer is not null && loaded.Oxidizer.AsSpan().SequenceEqual(oxygen),
            "World v8 did not preserve exhausted, fractional and compressed oxidizer byte-for-byte.");
        var raw = await SimulationStateSerializer.ReadWorldAsync(Path.ChangeExtension(path, ".world"), CancellationToken.None);
        Require(raw?.Version == 14 && raw.StoredCellStride == 52 && raw.Oxidizer is not null,
            "v8 changed GridCell layout or omitted the oxidizer section.");
        byte[] oldOxygen = MemoryMarshal.AsBytes(new float[] { 0, .37f, 1 }.AsSpan()).ToArray();
        Require(WorldCellCodec.Decode(new RawWorldFile(7, 3, 1, 40, EncodeV6Cells(MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray()), oldOxygen)).Oxidizer!.AsSpan().SequenceEqual(oldOxygen),
            "v7 inventory was not preserved during migration.");
        ExpectInvalid(() => WorldCellCodec.Decode(new RawWorldFile(7, 3, 1, 40, EncodeV6Cells(MemoryMarshal.Cast<byte,GridCell>(world.Grid).ToArray()), oxygen)));
        var emptyInventory=world with { Grid=[] };
        string emptyPath=Path.Combine(directory,"empty-inventory-v8.json");
        await serializer.SaveAsync(emptyPath,new SimulationSettings(),0,emptyInventory,materials);
        var emptyLoaded=await serializer.LoadAsync(emptyPath,materials);
        Require(emptyLoaded?.World?.Oxidizer is not null && emptyLoaded.World.Oxidizer.AsSpan().SequenceEqual(oxygen),
            "An empty cell section discarded its saved inventory.");
        Require(WorldCellCodec.Decode(new RawWorldFile(6, 1, 1, 40, EncodeV6Cells(cell))).Oxidizer is null,
            "v6 must remain loadable without an oxidizer section.");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -.01f })
        {
            float[] values = [invalid, 0, 1];
            ExpectInvalid(() => WorldCellCodec.ValidateOxidizer(3, 1, MemoryMarshal.AsBytes(values.AsSpan()).ToArray()));
        }
        ExpectInvalid(() => WorldCellCodec.ValidateOxidizer(3, 1, new byte[4]));
        byte[] original = await File.ReadAllBytesAsync(Path.ChangeExtension(path, ".world"));
        foreach (int length in new[] { -1, 4, int.MaxValue })
        {
            byte[] corrupt = (byte[])original.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(24, 4), length);
            string bad = Path.Combine(directory, $"oxidizer-length-{length}.world");
            await File.WriteAllBytesAsync(bad, corrupt);
            await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(bad, CancellationToken.None));
        }
        string truncated = Path.Combine(directory, "oxidizer-truncated.world");
        await File.WriteAllBytesAsync(truncated, original[..^1]);
        await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(truncated, CancellationToken.None));
        Console.WriteLine("PHYXEL_OXIDIZER_CODEC v8CompressedRoundTrip=True v7Compatible=True v6Compatible=True invalidValuesAndLengthsRejected=True");
    }

    private static async Task VerifyCorruptWorldsAsync(string directory)
    {
        byte[] oneCell = EncodeLegacyCells(default(LegacyGridCellV3V4));
        await ExpectInvalidWorldAsync(directory, "zero-width", 4, 0, 1, 0, []);
        await ExpectInvalidWorldAsync(directory, "zero-height", 4, 1, 0, 0, []);
        await ExpectInvalidWorldAsync(directory, "overflow", 4, int.MaxValue, int.MaxValue, 0, []);
        await ExpectInvalidWorldAsync(directory, "negative-length", 4, 1, 1, -1, []);
        await ExpectInvalidWorldAsync(directory, "wrong-length", 4, 1, 1, 31, new byte[31]);
        await ExpectInvalidWorldAsync(directory, "truncated-cells", 4, 1, 1, 32, new byte[31]);
        await ExpectInvalidWorldAsync(directory, "trailing-bytes", 4, 1, 1, 32, [.. oneCell, 0x7f]);

        string truncatedHeader = Path.Combine(directory, "truncated-header.world");
        await File.WriteAllBytesAsync(truncatedHeader, new byte[LegacyHeaderSize - 1]);
        await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(truncatedHeader, CancellationToken.None));

        string truncatedV5Header = Path.Combine(directory, "truncated-v5-header.world");
        byte[] v5Header = new byte[CurrentHeaderSize - 1];
        BinaryPrimitives.WriteUInt32LittleEndian(v5Header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(v5Header.AsSpan(4, 4), 5);
        await File.WriteAllBytesAsync(truncatedV5Header, v5Header);
        await ExpectInvalidAsync(() =>
            SimulationStateSerializer.ReadWorldAsync(truncatedV5Header, CancellationToken.None));

        ExpectInvalid(() => WorldCellCodec.Decode(new RawWorldFile(4, 1, 1, 31, oneCell)));
        ExpectInvalid(() => WorldCellCodec.Decode(new RawWorldFile(4, 1, 1, 36, oneCell)));

        GridCell valid = new() { MaterialIndex = 0, Mass = 1, IsActive = 1, Temperature = 20 };
        byte[] currentCell = EncodeCurrentCells(valid);
        await ExpectInvalidCurrentWorldAsync(directory, "v14-wrong-stride", 51, 52, currentCell);
        await ExpectInvalidCurrentWorldAsync(directory, "v14-truncated", 52, 52, currentCell[..^1]);
        await ExpectInvalidCurrentWorldAsync(directory, "v14-trailing", 52, 52, [.. currentCell, 0x7f]);
        await ExpectInvalidTemperatureAsync(directory, "v6-nan", float.NaN);
        await ExpectInvalidTemperatureAsync(directory, "v6-infinity", float.PositiveInfinity);
        await ExpectInvalidTemperatureAsync(directory, "v6-too-cold", -273.16f);
        await ExpectInvalidTemperatureAsync(directory, "v6-too-hot", 5000.01f);
        WorldCellCodec.Decode(new RawWorldFile(
            14,
            2,
            1,
            WorldCellCodec.CurrentCellStride,
            EncodeCurrentCells(
                new GridCell { IsActive = 1, Temperature = -273.15f },
                new GridCell { IsActive = 1, Temperature = 5000f })));
        Require(
            WorldCellCodec.Decode(new RawWorldFile(6, 1, 1, 40, [])).Grid.Length == 0,
            "v6 zero-length unallocated world was rejected.");

        GridCell dirtyInactive = new()
        {
            MaterialIndex = uint.MaxValue,
            Mass = 9,
            VelocityX = 8,
            VelocityY = 7,
            Pressure = 6,
            IsActive = 0,
            BodyId = 5,
            RestFrames = 4,
            Temperature = float.NaN
        };
        SimulationWorldSnapshot normalized = WorldCellCodec.Decode(
            new RawWorldFile(6, 1, 1, 40, EncodeV6Cells(dirtyInactive)));
        AssertCells(normalized, default(GridCell));
    }

    private static async Task ExpectInvalidWorldAsync(
        string directory,
        string name,
        int version,
        int width,
        int height,
        int declaredLength,
        byte[] actualBytes)
    {
        string path = Path.Combine(directory, $"{name}.world");
        await WriteRawWorldAsync(path, version, width, height, declaredLength, actualBytes);
        await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(path, CancellationToken.None));
    }

    private static async Task ExpectInvalidCurrentWorldAsync(
        string directory,
        string name,
        int stride,
        int declaredLength,
        byte[] actualBytes)
    {
        string path = Path.Combine(directory, $"{name}.world");
        await WriteCurrentWorldAsync(path, 1, 1, stride, declaredLength, actualBytes);
        await ExpectInvalidAsync(() => SimulationStateSerializer.ReadWorldAsync(path, CancellationToken.None));
    }

    private static async Task ExpectInvalidTemperatureAsync(
        string directory,
        string name,
        float temperature)
    {
        GridCell cell = new() { MaterialIndex = 0, Mass = 1, IsActive = 1, Temperature = temperature };
        byte[] cells = EncodeCurrentCells(cell);
        string path = Path.Combine(directory, $"{name}.world");
        await WriteCurrentWorldAsync(path, 1, 1, WorldCellCodec.CurrentCellStride, cells.Length, cells);
        await ExpectInvalidAsync(async () =>
        {
            RawWorldFile raw = await SimulationStateSerializer.ReadWorldAsync(path, CancellationToken.None) ??
                throw new InvalidOperationException("Invalid temperature world is missing.");
            WorldCellCodec.Decode(raw);
            return raw;
        });
    }

    private static async Task ExpectInvalidAsync(Func<Task<RawWorldFile?>> action)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Corrupt world file was accepted.");
    }

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Unsupported world cell stride was accepted.");
    }

    private static async Task WriteLegacyWorldAsync(
        string path,
        int version,
        int width,
        int height,
        byte[] cells)
    {
        await WriteRawWorldAsync(path, version, width, height, cells.Length, cells);
    }

    private static async Task WriteRawWorldAsync(
        string path,
        int version,
        int width,
        int height,
        int declaredLength,
        byte[] actualBytes)
    {
        byte[] header = new byte[LegacyHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), declaredLength);
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(header);
        await stream.WriteAsync(actualBytes);
    }

    private static async Task WriteCurrentWorldAsync(
        string path,
        int width,
        int height,
        int stride,
        int declaredLength,
        byte[] actualBytes)
    {
        byte[] header = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 14);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), stride);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), declaredLength);
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(header);
        await stream.WriteAsync(actualBytes);
        await stream.WriteAsync(new byte[20]);
    }

    private static byte[] EncodeLegacyCells(params LegacyGridCellV3V4[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.LegacyCellStride)];
        for (int index = 0; index < cells.Length; index++)
        {
            LegacyGridCellV3V4 cell = cells[index];
            Span<byte> destination = bytes.AsSpan(index * WorldCellCodec.LegacyCellStride, WorldCellCodec.LegacyCellStride);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], cell.MaterialIndex);
            WriteSingle(destination[4..8], cell.Mass);
            WriteSingle(destination[8..12], cell.VelocityX);
            WriteSingle(destination[12..16], cell.VelocityY);
            WriteSingle(destination[16..20], cell.Pressure);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], cell.IsActive);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[24..28], cell.BodyId);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[28..32], cell.RestFrames);
        }
        return bytes;
    }

    private static byte[] EncodeV6Cells(params GridCell[] cells)
    {
        byte[] bytes=new byte[cells.Length*40];
        for(int i=0;i<cells.Length;i++) MemoryMarshal.AsBytes(cells.AsSpan(i,1))[..40].CopyTo(bytes.AsSpan(i*40));
        return bytes;
    }

    private static byte[] EncodeCurrentCells(params GridCell[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.CurrentCellStride)];
        cells.AsSpan().CopyTo(MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan()));
        return bytes;
    }

    private static byte[] EncodeV5Cells(params LegacyGridCellV5[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.V5CellStride)];
        cells.AsSpan().CopyTo(MemoryMarshal.Cast<byte, LegacyGridCellV5>(bytes.AsSpan()));
        return bytes;
    }

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static LegacyGridCellV3V4 CreateLegacyCell(
        uint materialIndex,
        float mass,
        float velocityX,
        float velocityY,
        float pressure,
        uint isActive,
        uint bodyId,
        uint restFrames) =>
        new()
        {
            MaterialIndex = materialIndex,
            Mass = mass,
            VelocityX = velocityX,
            VelocityY = velocityY,
            Pressure = pressure,
            IsActive = isActive,
            BodyId = bodyId,
            RestFrames = restFrames
        };

    private static GridCell CreateCurrentCell(
        LegacyGridCellV3V4 source,
        uint materialIndex,
        float temperature) =>
        new()
        {
            MaterialIndex = materialIndex,
            Mass = source.Mass,
            VelocityX = source.VelocityX,
            VelocityY = source.VelocityY,
            Pressure = source.Pressure,
            IsActive = source.IsActive,
            BodyId = source.BodyId,
            RestFrames = source.RestFrames,
            Temperature = temperature
        };

    private static SimulationWorldSnapshot CreateSnapshot(int width, int height, params GridCell[] cells)
    {
        byte[] bytes = new byte[checked(cells.Length * WorldCellCodec.CurrentCellStride)];
        cells.AsSpan().CopyTo(MemoryMarshal.Cast<byte, GridCell>(bytes.AsSpan()));
        return new SimulationWorldSnapshot(width, height, bytes);
    }

    private static void AssertCells(SimulationWorldSnapshot? snapshot, params GridCell[] expected)
    {
        Require(snapshot is not null, "Loaded world snapshot is missing.");
        ReadOnlySpan<GridCell> actual = MemoryMarshal.Cast<byte, GridCell>(snapshot!.Grid);
        Require(actual.Length == expected.Length, "Loaded cell count changed.");
        for (int index = 0; index < expected.Length; index++)
        {
            GridCell left = actual[index];
            GridCell right = expected[index];
            Require(left.MaterialIndex == right.MaterialIndex, $"Cell {index} MaterialIndex changed.");
            Require(SameFloat(left.Mass, right.Mass), $"Cell {index} Mass changed.");
            Require(SameFloat(left.VelocityX, right.VelocityX), $"Cell {index} VelocityX changed.");
            Require(SameFloat(left.VelocityY, right.VelocityY), $"Cell {index} VelocityY changed.");
            Require(SameFloat(left.Pressure, right.Pressure), $"Cell {index} Pressure changed.");
            Require(left.IsActive == right.IsActive, $"Cell {index} IsActive changed.");
            Require(left.BodyId == right.BodyId, $"Cell {index} BodyId changed.");
            Require(left.RestFrames == right.RestFrames, $"Cell {index} RestFrames changed.");
            Require(SameFloat(left.Temperature, right.Temperature), $"Cell {index} Temperature changed.");
            Require(SameFloat(left.Lifetime, right.Lifetime), $"Cell {index} Lifetime changed.");
        }
    }

    private static bool SameFloat(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static bool ContainsWarning(IReadOnlyList<string> warnings, string value)
    {
        foreach (string warning in warnings)
        {
            if (warning.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static Task WriteJsonAsync(string path, object value) =>
        File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value));

    private static string CreateExternalMaterialJson(string id) => $$"""
        {
          "schema": 1,
          "id": "{{id}}",
          "name": "Runtime order probe",
          "kind": "granular",
          "color": "#ABCDEF",
          "physics": { "density": 2.5, "friction": 0.4, "flowRate": 0.2 },
          "thermal": { "initialTemperature": 123.0, "conductivity": 0.2, "heatCapacity": 1.5 },
          "ui": { "hidden": false }
        }
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
