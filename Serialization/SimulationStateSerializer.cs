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
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Serialization;

public sealed record SimulationSceneState(
    int Version,
    float Scale,
    float Gravity,
    int BrushRadius,
    float SpawnDensity,
    bool SolidGravity,
    ushort SelectedMaterial,
    DateTimeOffset SavedAt,
    bool HydraulicPressure = false,
    bool OpenBoundaries = true,
    SimulationMode Mode = SimulationMode.Simulation,
    FilterSelection FilterSelection = FilterSelection.Steam);

// World v11 extends v10 auxiliary validation for negative fusion progress.
// v10 persists pending reaction packets, pressure waves and their finite
// volume stock, plus Air/GasMotion. v9 carrier heat and v8 oxidizer stay intact.
// v16 adds an optional fine filter map, remapped through the scene palette.
// v15 appends retained-liquid identity to the v14 52-byte prefix (56-byte cells).
// Wet boiling storage may retain
// excess heat while vapour awaits an outlet. v12 remains readable.
// v3..v11 migrate their historic strides; missing moisture starts at zero.
public sealed record SimulationWorldSnapshot(
    int Width,
    int Height,
    byte[] Grid,
    byte[]? Air = null,
    byte[]? GasMotion = null,
    byte[]? Oxidizer = null,
    byte[]? AirThermal = null,
    byte[]? ReactionPending = null,
    byte[]? ReactionPulse = null,byte[]? Filters = null);

public sealed record LoadedSimulationScene(
    SimulationSceneState State,
    SimulationWorldSnapshot? World,
    IReadOnlyList<string> Warnings);

public sealed class SimulationStateSerializer
{
    private sealed class SceneFileV4
    {
        public int Version { get; set; }
        public float Scale { get; set; }
        public float Gravity { get; set; }
        public int BrushRadius { get; set; }
        public float SpawnDensity { get; set; }
        public bool SolidGravity { get; set; }
        public string SelectedMaterialId { get; set; } = CoreMaterialIds.Sand;
        public DateTimeOffset SavedAt { get; set; }
        public bool HydraulicPressure { get; set; }
        public FilterSelection FilterSelection { get; set; }
        public bool OpenBoundaries { get; set; } = true;
        // Existing scenes used finite oxidizer before modes were introduced.
        public SimulationMode Mode { get; set; } = SimulationMode.Simulation;
        public string[] MaterialPalette { get; set; } = [];
    }

    private const uint WorldFileMagic = 0x5058594C;
    private const int LegacyWorldHeaderSize = 20;
    private const int CurrentWorldHeaderSize = 28;
    private const int CurrentVersion = 16;
    private const string RemovedGoldSandId = "core:gold_sand";
    private const string RenamedConcreteId = "core:concrete";
    private const string RenamedGasId = "core:gas";
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private bool capturePending;
    private SimulationWorldSnapshot? emptySnapshot;
    private byte[]? capturedFilters;

    public SimulationStateSerializer()
    {
        WorldCellCodec.ValidateLayoutContracts();
    }

    public void BeginWorldCapture(GpuSimulationResources resources)
    {
        if (capturePending)
        {
            return;
        }
        if (!resources.IsSimulationAllocated)
        {
            emptySnapshot = new SimulationWorldSnapshot(resources.Width, resources.Height, []);
            capturePending = true;
            return;
        }
        capturedFilters=resources.FilterCount>0?MemoryMarshal.AsBytes(resources.FilterMap.AsSpan()).ToArray():null;
        resources.Context.CopyResource(resources.Grid.ReadBuffer, resources.GridStaging);
        resources.Context.CopyResource(resources.Air.Buffer, resources.AirStaging);
        resources.Context.CopyResource(resources.GasMotion.Buffer, resources.GasMotionStaging);
        resources.Context.CopyResource(resources.Oxidizer.ReadBuffer, resources.OxidizerStaging);
        resources.Context.CopyResource(resources.AirThermal.Buffer, resources.AirThermalStaging);
        resources.Context.CopyResource(resources.ReactionPending.Buffer,resources.ReactionPendingStaging);
        resources.Context.CopyResource(resources.ReactionPulse.ReadBuffer,resources.ReactionPulseStaging);
        resources.Context.End(resources.SceneTransferQuery);
        resources.Context.Flush();
        capturePending = true;
    }

    public bool TryCompleteWorldCapture(
        GpuSimulationResources resources,
        out SimulationWorldSnapshot? snapshot)
    {
        snapshot = null;
        if (!capturePending)
        {
            return false;
        }
        if (emptySnapshot is not null)
        {
            snapshot = emptySnapshot;
            emptySnapshot = null;
            capturePending = false;
            return true;
        }
        bool ready = resources.Context.GetData(
            resources.SceneTransferQuery,
            AsynchronousFlags.DoNotFlush,
            out RawBool completed);
        if (!ready || !completed)
        {
            return false;
        }
        snapshot = new SimulationWorldSnapshot(
            resources.Width,
            resources.Height,
            ReadBuffer(resources.Context, resources.GridStaging),
            ReadBuffer(resources.Context, resources.AirStaging),
            ReadBuffer(resources.Context, resources.GasMotionStaging),
            ReadBuffer(resources.Context, resources.OxidizerStaging),
            ReadBuffer(resources.Context, resources.AirThermalStaging),
            ReadBuffer(resources.Context,resources.ReactionPendingStaging),
            ReadBuffer(resources.Context,resources.ReactionPulseStaging),capturedFilters);
        capturePending = false;
        return true;
    }

    public async Task SaveAsync(
        string path,
        SimulationSettings settings,
        ushort selectedMaterial,
        SimulationWorldSnapshot world,
        MaterialRegistry materialRegistry,
        CancellationToken cancellationToken = default)
    {
        if (!materialRegistry.TryGet(selectedMaterial, out MaterialDefinition selectedDefinition))
        {
            throw new InvalidDataException($"Выбранный runtime-индекс материала {selectedMaterial} отсутствует в реестре.");
        }
        if (!Enum.IsDefined(settings.Mode)) throw new InvalidDataException("Неизвестный режим симуляции.");

        (SimulationWorldSnapshot encodedWorld, string[] palette) = EncodeSceneSnapshot(world, materialRegistry);
        SceneFileV4 state = new()
        {
            Version = CurrentVersion,
            Scale = settings.Scale,
            Gravity = settings.Gravity,
            BrushRadius = settings.BrushRadius,
            SpawnDensity = settings.SpawnDensity,
            SolidGravity = settings.SolidGravity,
            SelectedMaterialId = selectedDefinition.Id,
            SavedAt = DateTimeOffset.UtcNow,
            HydraulicPressure = settings.HydraulicPressure,
            FilterSelection = settings.FilterSelection,
            OpenBoundaries = settings.OpenBoundaries,
            Mode = settings.Mode,
            MaterialPalette = palette
        };
        string directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        await using (FileStream stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, state, options, cancellationToken);
        }
        await WriteWorldAsync(
            Path.ChangeExtension(path, ".world"),
            CurrentVersion,
            encodedWorld,
            cancellationToken);
    }

    public async Task<LoadedSimulationScene?> LoadAsync(
        string path,
        MaterialRegistry materialRegistry,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] sceneJson = await File.ReadAllBytesAsync(path, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(sceneJson);
        if (!document.RootElement.TryGetProperty("Version", out JsonElement versionElement) ||
            !versionElement.TryGetInt32(out int version))
        {
            return null;
        }

        RawWorldFile? rawWorld = await ReadWorldAsync(
            Path.ChangeExtension(path, ".world"),
            cancellationToken);
        if (rawWorld is not null && rawWorld.Version != version)
        {
            throw new InvalidDataException(
                $"Версии scene.json ({version}) и .world ({rawWorld.Version}) не совпадают.");
        }
        SimulationWorldSnapshot? world = rawWorld is null ? null : WorldCellCodec.Decode(rawWorld);

        List<string> warnings = [];
        return version switch
        {
            3 => LegacySceneV3Loader.Load(
                sceneJson,
                world,
                materialRegistry,
                warnings,
                options),
            4 => LoadPaletteScene(sceneJson, world, materialRegistry, warnings, true),
            5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or CurrentVersion => LoadPaletteScene(sceneJson, world, materialRegistry, warnings, false),
            _ => null
        };
    }

    public void ApplyWorldSnapshot(GpuSimulationResources resources, SimulationWorldSnapshot world)
    {
        if (resources.Width != world.Width || resources.Height != world.Height)
        {
            throw new InvalidDataException("Размер снимка мира не совпадает с размером GPU-ресурсов.");
        }
        ValidateSnapshotSize(world);
        Array.Clear(resources.FilterMap);resources.FilterCount=0;
        if(world.Filters is {Length:>0} filterBytes){
            MemoryMarshal.Cast<byte,uint>(filterBytes).CopyTo(resources.FilterMap);
            foreach(uint rule in resources.FilterMap)if(rule!=0)resources.FilterCount++;
        }
        resources.UploadFilters();
        if(world.ReactionPending is { Length: > 0 } pending)
            UploadBuffer(resources.Context,resources.ReactionPendingStaging,pending,[resources.ReactionPending.Buffer]);
        else resources.Context.ClearUnorderedAccessView(resources.ReactionPending.UnorderedView,new RawInt4());
        if(world.ReactionPulse is { Length: > 0 } pulse)
            UploadBuffer(resources.Context,resources.ReactionPulseStaging,pulse,resources.ReactionPulse.Buffers);
        else foreach(var view in resources.ReactionPulse.UnorderedAccessViews) resources.Context.ClearUnorderedAccessView(view,new RawInt4());
        if(world.Air is { Length: > 0 } air)
            UploadBuffer(resources.Context,resources.AirStaging,air,[resources.Air.Buffer]);
        else resources.Context.ClearUnorderedAccessView(resources.Air.UnorderedView,new RawInt4());
        if(world.GasMotion is { Length: > 0 } motion)
            UploadBuffer(resources.Context,resources.GasMotionStaging,motion,[resources.GasMotion.Buffer]);
        else resources.Context.ClearUnorderedAccessView(resources.GasMotion.UnorderedView,new RawInt4());
        if (world.Grid.Length == 0)
            foreach (var view in resources.Grid.UnorderedAccessViews)
                resources.Context.ClearUnorderedAccessView(view, new RawInt4());
        else
            UploadBuffer(resources.Context, resources.GridStaging, world.Grid, resources.Grid.Buffers);
        if (world.Oxidizer is { Length: > 0 } oxygen)
            UploadBuffer(resources.Context, resources.OxidizerStaging, oxygen, resources.Oxidizer.Buffers);
        else
        {
            float[] freshAir = new float[resources.Oxidizer.Count];
            Array.Fill(freshAir, 1f);
            foreach (Buffer buffer in resources.Oxidizer.Buffers) resources.Context.UpdateSubresource(freshAir, buffer);
        }
        if (world.AirThermal is { Length: > 0 } thermal)
            UploadBuffer(resources.Context, resources.AirThermalStaging, thermal, [resources.AirThermal.Buffer]);
        else
        {
            var ambient = new System.Numerics.Vector2[resources.AirWidth * resources.AirHeight];
            Array.Fill(ambient, new System.Numerics.Vector2(293.15f*.016f,.016f));
            resources.Context.UpdateSubresource(ambient,resources.AirThermal.Buffer);
        }
    }

    public static void Apply(SimulationSceneState state, SimulationSettings settings)
    {
        settings.ApplyScale(state.Scale);
        settings.Gravity = Math.Clamp(state.Gravity, 0, 4000);
        settings.BrushRadius = Math.Clamp(state.BrushRadius, 1, 96);
        settings.SpawnDensity = Math.Clamp(state.SpawnDensity, 0.05f, 1);
        settings.SolidGravity = state.SolidGravity;
        settings.HydraulicPressure = state.HydraulicPressure;
        settings.FilterSelection = Enum.IsDefined(state.FilterSelection) ? state.FilterSelection : FilterSelection.Steam;
        settings.OpenBoundaries = state.OpenBoundaries;
        if (!Enum.IsDefined(state.Mode)) throw new InvalidDataException("Неизвестный режим симуляции.");
        settings.Mode = state.Mode;
    }

    public static bool ContainsMatter(SimulationWorldSnapshot world)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive != 0)
            {
                return true;
            }
        }
        return false;
    }

    public static bool ContainsContactTransitionSource(
        SimulationWorldSnapshot world,
        MaterialRegistry materialRegistry)
    {
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive != 0 && cell.MaterialIndex < materialRegistry.Count &&
                (materialRegistry[cell.MaterialIndex].LiquidContactTransition is not null || materialRegistry[cell.MaterialIndex].Moisture is not null))
            {
                return true;
            }
        }
        return false;
    }

    private LoadedSimulationScene LoadPaletteScene(
        byte[] sceneJson,
        SimulationWorldSnapshot? world,
        MaterialRegistry materialRegistry,
        List<string> warnings,
        bool initializeLegacyTemperature)
    {
        SceneFileV4 state = JsonSerializer.Deserialize<SceneFileV4>(sceneJson, options) ??
            throw new InvalidDataException("Сцена v4 не содержит состояния.");
        if (!Enum.IsDefined(state.Mode)) throw new InvalidDataException("Неизвестный режим симуляции.");
        if (state.MaterialPalette.Length is 0 or > MaterialRegistry.MaximumMaterials)
        {
            throw new InvalidDataException("Палитра сцены v4 пуста или превышает допустимый размер.");
        }
        if (MaterialRegistry.NormalizeId(state.MaterialPalette[0]) != CoreMaterialIds.Empty)
        {
            throw new InvalidDataException("Индекс 0 палитры сцены v4 должен быть core:empty.");
        }

        string selectedMaterialId = MigrateV4MaterialId(
            MaterialRegistry.NormalizeId(state.SelectedMaterialId),
            warnings);
        ushort selectedMaterial;
        if (!materialRegistry.TryGet(selectedMaterialId, out MaterialDefinition selectedDefinition) ||
            selectedDefinition.Hidden)
        {
            selectedMaterial = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Sand);
            AddWarning(warnings, $"Выбранный материал '{state.SelectedMaterialId}' отсутствует; выбран core:sand.");
        }
        else
        {
            selectedMaterial = selectedDefinition.RuntimeIndex;
        }
        if (world is not null)
        {
            RemapSnapshotToRuntime(
                world,
                state.MaterialPalette,
                materialRegistry,
                warnings,
                initializeLegacyTemperature);
        }

        return new LoadedSimulationScene(
            new SimulationSceneState(
                state.Version,
                state.Scale,
                state.Gravity,
                state.BrushRadius,
                state.SpawnDensity,
                state.SolidGravity,
                selectedMaterial,
                state.SavedAt,
                state.HydraulicPressure,
                state.OpenBoundaries,
                state.Mode, state.FilterSelection),
            world,
            warnings);
    }

    private static (SimulationWorldSnapshot Snapshot, string[] Palette) EncodeSceneSnapshot(
        SimulationWorldSnapshot world,
        MaterialRegistry materialRegistry)
    {
        ValidateSnapshotSize(world);
        bool[] usedRuntimeIndices = new bool[materialRegistry.Count];
        ushort emptyRuntimeIndex = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Empty);
        usedRuntimeIndices[emptyRuntimeIndex] = true;
        ReadOnlySpan<GridCell> sourceCells = MemoryMarshal.Cast<byte, GridCell>(world.Grid);
        foreach (GridCell cell in sourceCells)
        {
            if (cell.IsActive == 0)
            {
                continue;
            }
            if (cell.MaterialIndex >= materialRegistry.Count)
            {
                throw new InvalidDataException(
                    $"Снимок содержит неизвестный runtime-индекс материала {cell.MaterialIndex}.");
            }
            usedRuntimeIndices[cell.MaterialIndex] = true;
            if(cell.FuelMass>0)
            {
                uint retained=PhaseEnthalpy.RetainedLiquidIndex(cell,materialRegistry[cell.MaterialIndex].Properties);
                ValidateRetainedLiquid(retained,materialRegistry,materialRegistry[cell.MaterialIndex].Properties);
                usedRuntimeIndices[retained]=true;
            }
            ValidatePhaseAuxiliary(cell, materialRegistry[cell.MaterialIndex].Properties);
        }

        if(world.Filters is {Length:>0} filterBytes)
            foreach(uint rule in MemoryMarshal.Cast<byte,uint>(filterBytes)){
                FilterRules.Validate(rule,materialRegistry.Count);
                if((rule&FilterRules.IdMask)!=0)usedRuntimeIndices[(rule&FilterRules.IdMask)-1]=true;
            }
        ushort[] runtimeToScene = new ushort[materialRegistry.Count];
        Array.Fill(runtimeToScene, ushort.MaxValue);
        List<string> palette = [];
        for (ushort runtimeIndex = 0; runtimeIndex < materialRegistry.Count; runtimeIndex++)
        {
            if (!usedRuntimeIndices[runtimeIndex])
            {
                continue;
            }
            runtimeToScene[runtimeIndex] = checked((ushort)palette.Count);
            palette.Add(materialRegistry[runtimeIndex].Id);
        }

        byte[] encodedGrid = (byte[])world.Grid.Clone();
        Span<GridCell> encodedCells = MemoryMarshal.Cast<byte, GridCell>(encodedGrid.AsSpan());
        for (int index = 0; index < encodedCells.Length; index++)
        {
            if (encodedCells[index].IsActive == 0)
            {
                encodedCells[index] = default;
                continue;
            }
            uint runtimeIndex = encodedCells[index].MaterialIndex;
            if(encodedCells[index].FuelMass>0)
                encodedCells[index].RetainedLiquidMaterialIndex=runtimeToScene[PhaseEnthalpy.RetainedLiquidIndex(encodedCells[index],materialRegistry[runtimeIndex].Properties)];
            else encodedCells[index].RetainedLiquidMaterialIndex=0;
            encodedCells[index].MaterialIndex = runtimeToScene[runtimeIndex];
        }
        return (new SimulationWorldSnapshot(world.Width, world.Height, encodedGrid, world.Air,world.GasMotion,
            world.Oxidizer,world.AirThermal,world.ReactionPending,world.ReactionPulse,EncodeFilters(world.Filters,runtimeToScene)), palette.ToArray());
    }

    private static void RemapSnapshotToRuntime(
        SimulationWorldSnapshot world,
        IReadOnlyList<string> scenePalette,
        MaterialRegistry materialRegistry,
        List<string> warnings,
        bool initializeLegacyTemperature)
    {
        ValidateSnapshotSize(world);
        if (scenePalette.Count is 0 or > MaterialRegistry.MaximumMaterials)
        {
            throw new InvalidDataException("Палитра сцены пуста или превышает допустимый размер.");
        }

        uint[] sceneToRuntime = new uint[scenePalette.Count];
        bool[] missing = new bool[scenePalette.Count];
        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int sceneIndex = 0; sceneIndex < scenePalette.Count; sceneIndex++)
        {
            string storedId = MaterialRegistry.NormalizeId(scenePalette[sceneIndex]);
            if (!ids.Add(storedId))
            {
                throw new InvalidDataException($"Палитра сцены содержит дублирующий ID '{storedId}'.");
            }
            string id = MigrateV4MaterialId(storedId, warnings);
            if (materialRegistry.TryGet(id, out MaterialDefinition definition))
            {
                sceneToRuntime[sceneIndex] = definition.RuntimeIndex;
            }
            else
            {
                missing[sceneIndex] = true;
                sceneToRuntime[sceneIndex] = materialRegistry.GetRequiredRuntimeIndex(CoreMaterialIds.Empty);
                AddWarning(warnings, $"Материал '{id}' отсутствует и заменён на core:empty.");
            }
        }

        if(world.Filters is {Length:>0} savedFilters){
            var rules=MemoryMarshal.Cast<byte,uint>(savedFilters.AsSpan());
            for(int i=0;i<rules.Length;i++){
                uint id=rules[i]&FilterRules.IdMask;if(id==0)continue;
                if(id>sceneToRuntime.Length)throw new InvalidDataException("Filter refers outside the scene palette.");
                uint runtime=sceneToRuntime[id-1];
                var kind=(MaterialSimulationKind)materialRegistry[runtime].Properties.SimulationKind;
                bool moving=kind is MaterialSimulationKind.Gas or MaterialSimulationKind.Liquid or MaterialSimulationKind.Granular;
                rules[i]=missing[id-1]||!moving?FilterRules.Closed:(rules[i]&~FilterRules.IdMask)|(runtime+1);
            }
        }
        var moistureTable=materialRegistry.CreateGpuTable();
        Span<GridCell> cells = MemoryMarshal.Cast<byte, GridCell>(world.Grid.AsSpan());
        for (int index = 0; index < cells.Length; index++)
        {
            if (cells[index].IsActive == 0)
            {
                cells[index] = default;
                continue;
            }
            uint sceneIndex = cells[index].MaterialIndex;
            if (sceneIndex >= sceneToRuntime.Length || missing[sceneIndex])
            {
                cells[index] = default;
                continue;
            }
            uint runtimeIndex = sceneToRuntime[sceneIndex];
            cells[index].MaterialIndex = runtimeIndex;
            MaterialProperties moistureMaterial=materialRegistry[runtimeIndex].Properties;
            if(cells[index].FuelMass>0)
            {
                uint retainedScene=cells[index].RetainedLiquidMaterialIndex;
                // v14 and old programmatic snapshots omit the species: use that host's configured carrier.
                if(retainedScene==0) cells[index].RetainedLiquidMaterialIndex=moistureMaterial.FuelLiquidMaterialIndex;
                else {
                    if(retainedScene>=sceneToRuntime.Length || missing[retainedScene])
                        throw new InvalidDataException("Retained liquid is missing from the scene palette; refusing to replace or discard it.");
                    cells[index].RetainedLiquidMaterialIndex=sceneToRuntime[retainedScene];
                }
                ValidateRetainedLiquid(cells[index].RetainedLiquidMaterialIndex,materialRegistry,moistureMaterial);
            }
            else cells[index].RetainedLiquidMaterialIndex=0;
            ValidateAbsorbedFuel(cells[index], moistureMaterial);
            if (cells[index].MoistureMass > 0 &&
                (moistureMaterial.MoistureCapacity <= 0 || !float.IsFinite(cells[index].Mass) || cells[index].Mass <= 0 ||
                 cells[index].MoistureMass > cells[index].Mass * moistureMaterial.MoistureCapacity + .00001f))
                throw new InvalidDataException($"Invalid moisture capacity/energy in cell {index}.");
            if (cells[index].MoistureMass > 0 && cells[index].Temperature >
                materialRegistry[moistureMaterial.MoistureLiquidMaterialIndex].Properties.TransitionAboveTemperature)
            {
                // v12 previously allowed hot wet cells after filling latent
                // storage. Canonicalize the same total energy without loss.
                float moistureEnergy=PhaseEnthalpy.SpecificEnergy(cells[index],moistureTable);
                PhaseEnthalpy.SetSpecificEnergy(ref cells[index],moistureEnergy,moistureTable);
            }
            if (moistureMaterial.MoistureCapacity > 0 && cells[index].MoistureMass == 0 &&
                runtimeIndex == moistureMaterial.MoistureWetMaterialIndex && runtimeIndex != moistureMaterial.MoistureDryMaterialIndex)
            {
                cells[index].MaterialIndex = moistureMaterial.MoistureDryMaterialIndex;
                AddWarning(warnings, "Мокрый материал без сохранённой воды восстановлен как сухой; вода не создавалась.");
            }
            ValidatePhaseAuxiliary(cells[index], materialRegistry[runtimeIndex].Properties);
            if (ThermalRegulator.Enabled(materialRegistry[runtimeIndex].Properties) &&
                !ThermalRegulator.ValidCellSettings(cells[index]))
                throw new InvalidDataException($"Invalid thermal device settings in world cell {index}.");
            if (initializeLegacyTemperature)
            {
                cells[index].Temperature = LegacySceneV3Loader.InitialTemperature(materialRegistry[runtimeIndex]);
            }
        }
    }

    private static string MigrateV4MaterialId(string id, List<string> warnings)
    {
        string replacementId;
        string warning;
        if (id == RemovedGoldSandId)
        {
            replacementId = CoreMaterialIds.Sand;
            warning = "Материал 'core:gold_sand' удалён и мигрирован в 'core:sand'.";
        }
        else if (id == RenamedConcreteId)
        {
            replacementId = CoreMaterialIds.Stone;
            warning = "Материал 'core:concrete' переименован и мигрирован в 'core:stone'.";
        }
        else if (id == RenamedGasId)
        {
            replacementId = CoreMaterialIds.Co2;
            warning = "Материал 'core:gas' переименован и мигрирован в 'core:co2'.";
        }
        else
        {
            return id;
        }

        if (!warnings.Contains(warning))
        {
            AddWarning(warnings, warning);
        }
        return replacementId;
    }

    private static void AddWarning(List<string> warnings, string message)
    {
        warnings.Add(message);
        Console.Error.WriteLine($"PHYXEL_SCENE_WARNING {message}");
    }

    private static byte[]? EncodeFilters(byte[]? source,ushort[] mapping)
    {
        if(source is not {Length:>0})return null;
        var bytes=(byte[])source.Clone();var rules=MemoryMarshal.Cast<byte,uint>(bytes.AsSpan());
        for(int i=0;i<rules.Length;i++){
            uint id=rules[i]&FilterRules.IdMask;if(id!=0)rules[i]=(rules[i]&~FilterRules.IdMask)|(uint)(mapping[id-1]+1);
        }
        return bytes;
    }

    private static void ValidateRetainedLiquid(uint index,MaterialRegistry registry,MaterialProperties host)
    {
        if(index==0 || index>=registry.Count || registry[index].Properties.SimulationKind!=(uint)MaterialSimulationKind.Liquid)
            throw new InvalidDataException("Retained species must be a registered liquid.");
        if(index==host.MoistureLiquidMaterialIndex ||
           ((host.Flags & (uint)MaterialFlags.UniversalPores)==0 && index!=host.FuelLiquidMaterialIndex))
            throw new InvalidDataException("Host does not accept this species in its second retained liquid stock.");
    }

    private static void ValidateAbsorbedFuel(GridCell cell, MaterialProperties material)
    {
        if (!float.IsFinite(cell.FuelMass) || cell.FuelMass < 0)
            throw new InvalidDataException("Invalid absorbed fuel mass.");
        if (cell.FuelMass == 0) return;
        if (material.FuelCapacity <= 0 || !float.IsFinite(cell.Mass) || cell.Mass <= 0 ||
            cell.FuelMass / (cell.Mass * material.FuelCapacity) +
            (material.MoistureCapacity > 0 ? cell.MoistureMass / (cell.Mass * material.MoistureCapacity) : 0) > 1.0001f)
            throw new InvalidDataException("Absorbed fuel is unsupported or exceeds shared pore capacity.");
    }

    private static void ValidatePhaseAuxiliary(GridCell cell, MaterialProperties material)
    {
        ValidateAbsorbedFuel(cell, material);
        if (cell.Lifetime < 0 &&
            (!PhaseEnthalpy.FusionEnabled(material) || material.SimulationKind != (uint)MaterialSimulationKind.Liquid))
            throw new InvalidDataException("Negative phase progress requires a fusion-enthalpy liquid.");
    }

    internal static void ValidateSnapshotSize(SimulationWorldSnapshot world)
    {
        WorldCellCodec.ValidateFilters(world.Width,world.Height,world.Filters);
        WorldCellCodec.ValidateOxidizer(world.Width, world.Height, world.Oxidizer);
        WorldCellCodec.ValidateAirThermal(world.Width,world.Height,world.AirThermal);
        WorldCellCodec.ValidateReactionState(world.Width,world.Height,world.ReactionPending,world.ReactionPulse,world.Air,world.GasMotion);
        int expected = checked(world.Width * world.Height * Marshal.SizeOf<GridCell>());
        if (world.Grid.Length != 0 && world.Grid.Length != expected)
        {
            throw new InvalidDataException("Размер секции снимка мира некорректен.");
        }
    }

    private static byte[] ReadBuffer(DeviceContext context, Buffer buffer)
    {
        int length = buffer.Description.SizeInBytes;
        byte[] bytes = new byte[length];
        DataBox mapping = context.MapSubresource(buffer, 0, MapMode.Read, MapFlags.None);
        Marshal.Copy(mapping.DataPointer, bytes, 0, length);
        context.UnmapSubresource(buffer, 0);
        return bytes;
    }

    private static void UploadBuffer(DeviceContext context, Buffer staging, byte[] bytes, Buffer[] destinations)
    {
        if (bytes.Length != staging.Description.SizeInBytes)
        {
            throw new InvalidDataException("Размер секции снимка мира некорректен.");
        }
        DataBox mapping = context.MapSubresource(staging, 0, MapMode.Write, MapFlags.None);
        Marshal.Copy(bytes, 0, mapping.DataPointer, bytes.Length);
        context.UnmapSubresource(staging, 0);
        foreach (Buffer destination in destinations)
        {
            context.CopyResource(staging, destination);
        }
    }

    private static async Task WriteWorldAsync(
        string path,
        int version,
        SimulationWorldSnapshot world,
        CancellationToken cancellationToken)
    {
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Запись world v{version} не поддерживается.");
        }
        ValidateSnapshotSize(world);
        byte[] header = new byte[CurrentWorldHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), WorldFileMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), world.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), world.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), WorldCellCodec.CurrentCellStride);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), world.Grid.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), world.Oxidizer?.Length ?? 0);
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(world.Grid, cancellationToken);
        if (world.Oxidizer is { Length: > 0 }) await stream.WriteAsync(world.Oxidizer, cancellationToken);
        byte[] heatLength=new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(heatLength,world.AirThermal?.Length ?? 0);
        await stream.WriteAsync(heatLength,cancellationToken);
        if (world.AirThermal is { Length: > 0 }) await stream.WriteAsync(world.AirThermal,cancellationToken);
        foreach(byte[]? section in new[]{world.ReactionPending,world.ReactionPulse,world.Air,world.GasMotion})
        {
            byte[] size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,section?.Length??0);
            await stream.WriteAsync(size,cancellationToken);
            if(section is { Length: > 0 }) await stream.WriteAsync(section,cancellationToken);
        }
        if(world.Filters is {Length:>0} filters){
            byte[] size=BitConverter.GetBytes(filters.Length);await stream.WriteAsync(size,cancellationToken);
            await stream.WriteAsync(filters,cancellationToken);
        }
    }

    internal static async Task<RawWorldFile?> ReadWorldAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        byte[] prefix = new byte[8];
        try
        {
            await stream.ReadExactlyAsync(prefix, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Заголовок файла мира обрезан.", exception);
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(0, 4));
        int version = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4, 4));
        if (magic != WorldFileMagic || version is not (3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or CurrentVersion))
        {
            throw new InvalidDataException("Формат снимка мира не поддерживается.");
        }

        bool extendedHeader = version >= 5;
        int headerSize = version >= 7 ? CurrentWorldHeaderSize : extendedHeader ? 24 : LegacyWorldHeaderSize;
        byte[] remainder = new byte[headerSize - prefix.Length];
        try
        {
            await stream.ReadExactlyAsync(remainder, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Заголовок файла мира обрезан.", exception);
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(remainder.AsSpan(0, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(remainder.AsSpan(4, 4));
        int storedCellStride = extendedHeader
            ? BinaryPrimitives.ReadInt32LittleEndian(remainder.AsSpan(8, 4))
            : WorldCellCodec.LegacyCellStride;
        int length = BinaryPrimitives.ReadInt32LittleEndian(
            remainder.AsSpan(extendedHeader ? 12 : 8, 4));
        WorldCellCodec.ValidateStoredWorld(version, width, height, storedCellStride, length);

        int oxygenLength = version >= 7 ? BinaryPrimitives.ReadInt32LittleEndian(remainder.AsSpan(16, 4)) : 0;
        long expectedOxygenLength = (long)width * height * sizeof(float);
        if (oxygenLength < 0 || (oxygenLength != 0 && oxygenLength != expectedOxygenLength))
            throw new InvalidDataException("Invalid oxidizer section length.");
        long expectedFileLength = checked((long)headerSize + length + oxygenLength);
        if (stream.Length < expectedFileLength)
        {
            throw new InvalidDataException("Секция клеток файла мира обрезана.");
        }
        if (version < 9 && stream.Length > expectedFileLength)
        {
            throw new InvalidDataException("После секции клеток файла мира обнаружены лишние байты.");
        }

        byte[] grid = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(grid, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Секция клеток файла мира обрезана.", exception);
        }
        byte[]? oxygen = oxygenLength == 0 ? null : new byte[oxygenLength];
        if (oxygen is not null) await stream.ReadExactlyAsync(oxygen, cancellationToken);
        WorldCellCodec.ValidateOxidizer(width, height, oxygen, version >= 8);
        byte[]? thermal=null;
        if (version >= 9)
        {
            byte[] size=new byte[4];
            try { await stream.ReadExactlyAsync(size,cancellationToken); }
            catch (EndOfStreamException e) { throw new InvalidDataException("Air heat section header is truncated.",e); }
            int heatLength=BinaryPrimitives.ReadInt32LittleEndian(size);
            long expectedHeatLength=(long)((width+3)/4)*((height+3)/4)*8;
            if (heatLength < 0 || (heatLength != 0 && heatLength != expectedHeatLength) ||
                (version==9 && stream.Length != expectedFileLength+4+heatLength) || stream.Length<expectedFileLength+4+heatLength)
                throw new InvalidDataException("Invalid air heat section length.");
            thermal=heatLength==0?null:new byte[heatLength];
            if (thermal is not null) await stream.ReadExactlyAsync(thermal,cancellationToken);
            WorldCellCodec.ValidateAirThermal(width,height,thermal);
        }
        byte[]? pending=null,pulse=null,air=null,motion=null,filters=null;
        if(version>=10)
        {
            async Task<byte[]?> Section(long expected)
            {
                byte[] size=new byte[4];
                try { await stream.ReadExactlyAsync(size,cancellationToken); }
                catch(EndOfStreamException e) { throw new InvalidDataException("Reaction section header is truncated.",e); }
                int n=BinaryPrimitives.ReadInt32LittleEndian(size);
                if(n<0 || (n!=0 && n!=expected) || stream.Length-stream.Position<n)
                    throw new InvalidDataException("Invalid reaction section length.");
                if(n==0) return null;
                byte[] bytes=new byte[n];await stream.ReadExactlyAsync(bytes,cancellationToken);return bytes;
            }
            long fine=(long)width*height*16,coarse=(long)((width+3)/4)*((height+3)/4)*16;
            pending=await Section(fine);pulse=await Section(coarse);air=await Section(coarse);motion=await Section(fine);
            if(version>=16 && stream.Position<stream.Length)filters=await Section((long)width*height*4);
            if(stream.Position!=stream.Length) throw new InvalidDataException("Trailing world data.");
            WorldCellCodec.ValidateReactionState(width,height,pending,pulse,air,motion);
        }
        return new RawWorldFile(version, width, height, storedCellStride, grid, oxygen, thermal,pending,pulse,air,motion,filters);
    }
}
