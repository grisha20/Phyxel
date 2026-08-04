using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Phyxel.Graphics;
using Phyxel.Physics;
using Phyxel.Serialization;
using SharpDX;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Phyxel.Diagnostics;

/// <summary>
/// Readback and CSV writer for the explicitly enabled steam-jet air-coupling
/// observers. The sampled buffers are not world state and never feed physics.
/// </summary>
public sealed class SteamJetAirCouplingTrace
{
    private sealed record Sample(
        SteamJetGasMotionContribution[] Motion,
        SteamJetAirCouplingCell[] Coupling);

    private readonly Dictionary<uint, Sample> samples = [];

    public int Count => samples.Count;

    public void Record(uint frame, GpuSimulationResources resources)
    {
        if (samples.ContainsKey(frame) || resources.SteamJetMotionContributions is null ||
            resources.SteamJetMotionContributionsStaging is null || resources.SteamJetAirCoupling is null ||
            resources.SteamJetAirCouplingStaging is null)
        {
            return;
        }

        DeviceContext context = resources.Context;
        SteamJetGasMotionContribution[] motion = ReadBuffer<SteamJetGasMotionContribution>(
            context,
            resources.SteamJetMotionContributions.Buffer,
            resources.SteamJetMotionContributionsStaging);
        SteamJetAirCouplingCell[] coupling = ReadBuffer<SteamJetAirCouplingCell>(
            context,
            resources.SteamJetAirCoupling.Buffer,
            resources.SteamJetAirCouplingStaging);
        samples.Add(frame, new Sample(motion, coupling));
    }

    public string WriteArtifacts(
        string directory,
        IReadOnlyDictionary<uint, SimulationWorldSnapshot> snapshots,
        uint steamMaterialIndex,
        int sourceX,
        int sourceY)
    {
        Directory.CreateDirectory(directory);
        List<string> paths = [];
        foreach ((uint frame, Sample sample) in samples)
        {
            if (!snapshots.TryGetValue(frame, out SimulationWorldSnapshot? snapshot))
            {
                continue;
            }
            paths.Add(WriteBandProfile(directory, frame, snapshot, sample, steamMaterialIndex, sourceY));
            paths.Add(WriteTrunkCollapseProfile(directory, frame, snapshot, steamMaterialIndex, sourceX, sourceY));
            paths.Add(WriteFineMotion(directory, frame, snapshot.Width, sample.Motion));
            paths.Add(WriteCoarseCoupling(directory, frame, snapshot.Width, sample.Coupling));
        }
        return string.Join(';', paths);
    }

    private static string WriteTrunkCollapseProfile(
        string directory,
        uint frame,
        SimulationWorldSnapshot snapshot,
        uint steamMaterialIndex,
        int sourceX,
        int sourceY)
    {
        if (snapshot.Air is null || snapshot.GasMotion is null)
        {
            throw new InvalidOperationException("steam_jet trunk-collapse trace needs Air and GasMotion readback.");
        }

        const int bandHeight = 20;
        const int bandCount = 4;
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        ReadOnlySpan<AirCell> air = MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        ReadOnlySpan<GasMotionState> motion = MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        int airWidth = (snapshot.Width + 3) / 4;
        int airHeight = (snapshot.Height + 3) / 4;
        TrunkTotals[] bands = [new(), new(), new(), new()];

        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            if (cell.IsActive == 0 || cell.MaterialIndex != steamMaterialIndex) continue;
            int x = index % snapshot.Width;
            int y = index / snapshot.Width;
            int height = sourceY - y;
            if (height < 0 || height >= bandHeight * bandCount) continue;
            TrunkTotals total = bands[height / bandHeight];
            total.SteamCells++;
            total.SumX += x;
            total.SumXSquare += (double)x * x;
            if (x < sourceX) { total.LeftGasCells++; total.LeftGasVelocityX += motion[index].VelocityX; }
            else if (x > sourceX) { total.RightGasCells++; total.RightGasVelocityX += motion[index].VelocityX; }
            total.AirIndices.Add(Math.Min(airHeight - 1, y / 4) * airWidth + Math.Min(airWidth - 1, x / 4));
            total.MinimumX = Math.Min(total.MinimumX, x);
            total.MaximumX = Math.Max(total.MaximumX, x);
        }

        string path = Path.Combine(directory, $"steam-jet-trunk-collapse-{frame}.csv");
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,heightAboveSourceStart,heightAboveSourceEnd,steamCells,sigmaX,meanAirVelocityX,meanAirVelocityY,meanGasVelocityXLeft,meanGasVelocityXRight,leftEdgeAirVelocityX,rightEdgeAirVelocityX,edgeVelocityXRightMinusLeft,airCellsAtLeftEdge,airCellsAtRightEdge");
        for (int band = 0; band < bandCount; band++)
        {
            TrunkTotals total = bands[band];
            if (total.SteamCells == 0) continue;
            double count = total.SteamCells;
            double meanX = total.SumX / count;
            double sigmaX = Math.Sqrt(Math.Max(0, total.SumXSquare / count - meanX * meanX));
            double airX = 0, airY = 0;
            foreach (int airIndex in total.AirIndices) { airX += air[airIndex].VelocityX; airY += air[airIndex].VelocityY; }
            HashSet<int> leftAir = AirIndicesAtFineColumn(grid, steamMaterialIndex, total.MinimumX, sourceY, band, airWidth, airHeight, snapshot.Width);
            HashSet<int> rightAir = AirIndicesAtFineColumn(grid, steamMaterialIndex, total.MaximumX, sourceY, band, airWidth, airHeight, snapshot.Width);
            double leftEdge = MeanAirVelocityX(air, leftAir);
            double rightEdge = MeanAirVelocityX(air, rightAir);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frame},{band * bandHeight},{band * bandHeight + bandHeight - 1},{total.SteamCells},{sigmaX:0.000000},{airX / Math.Max(1, total.AirIndices.Count):0.000000},{airY / Math.Max(1, total.AirIndices.Count):0.000000},{total.LeftGasVelocityX / Math.Max(1, total.LeftGasCells):0.000000},{total.RightGasVelocityX / Math.Max(1, total.RightGasCells):0.000000},{leftEdge:0.000000},{rightEdge:0.000000},{rightEdge - leftEdge:0.000000},{leftAir.Count},{rightAir.Count}"));
        }
        return path;
    }

    private static HashSet<int> AirIndicesAtFineColumn(ReadOnlySpan<GridCell> grid, uint steam, int x, int sourceY, int band, int airWidth, int airHeight, int width)
    {
        HashSet<int> indices = [];
        for (int height = band * 20; height < band * 20 + 20; height++)
        {
            int y = sourceY - height;
            if (x < 0 || y < 0 || x >= width) continue;
            GridCell cell = grid[y * width + x];
            if (cell.IsActive == 0 || cell.MaterialIndex != steam) continue;
            indices.Add(Math.Min(airHeight - 1, y / 4) * airWidth + Math.Min(airWidth - 1, x / 4));
        }
        return indices;
    }

    private static double MeanAirVelocityX(ReadOnlySpan<AirCell> air, IReadOnlyCollection<int> indices)
    {
        if (indices.Count == 0) return double.NaN;
        double sum = 0;
        foreach (int index in indices) sum += air[index].VelocityX;
        return sum / indices.Count;
    }

    private static T[] ReadBuffer<T>(DeviceContext context, Buffer source, Buffer staging) where T : struct
    {
        context.CopyResource(source, staging);
        DataBox mapping = context.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            byte[] bytes = new byte[source.Description.SizeInBytes];
            Marshal.Copy(mapping.DataPointer, bytes, 0, bytes.Length);
            return MemoryMarshal.Cast<byte, T>(bytes.AsSpan()).ToArray();
        }
        finally
        {
            context.UnmapSubresource(staging, 0);
        }
    }

    private static string WriteBandProfile(
        string directory,
        uint frame,
        SimulationWorldSnapshot snapshot,
        Sample sample,
        uint steamMaterialIndex,
        int sourceY)
    {
        if (snapshot.Air is null || snapshot.GasMotion is null)
        {
            throw new InvalidOperationException("steam_jet air-coupling trace needs Air and GasMotion readback.");
        }
        ReadOnlySpan<GridCell> grid = MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
        ReadOnlySpan<AirCell> air = MemoryMarshal.Cast<byte, AirCell>(snapshot.Air);
        ReadOnlySpan<GasMotionState> motion = MemoryMarshal.Cast<byte, GasMotionState>(snapshot.GasMotion);
        int airWidth = (snapshot.Width + 3) / 4;
        int airHeight = (snapshot.Height + 3) / 4;
        int bandCount = Math.Max(1, (sourceY + 20) / 20);
        BandTotals[] bands = new BandTotals[bandCount];

        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            if (cell.IsActive == 0 || cell.MaterialIndex != steamMaterialIndex) continue;
            int x = index % snapshot.Width;
            int y = index / snapshot.Width;
            int band = BandForY(sourceY, y, bandCount);
            if (band < 0) continue;
            BandTotals total = bands[band] ??= new BandTotals();
            total.PostSteamCells++;
            total.PostGasVelocityY += motion[index].VelocityY;
            total.PostAirIndices.Add(Math.Min(airHeight - 1, y / 4) * airWidth + Math.Min(airWidth - 1, x / 4));
        }

        // This array was sampled before phase 89. Its positions are therefore
        // intentionally pre-motion positions for the same fixed tick, rather
        // than reconstructed from the later snapshot by subtraction.
        for (int index = 0; index < sample.Motion.Length; index++)
        {
            SteamJetGasMotionContribution contribution = sample.Motion[index];
            if ((contribution.Flags & 1u) == 0) continue;
            int y = index / snapshot.Width;
            int band = BandForY(sourceY, y, bandCount);
            if (band < 0) continue;
            BandTotals total = bands[band] ??= new BandTotals();
            total.PreMotionSteamCells++;
            total.RetainedVelocityY += contribution.RetainedVelocityY;
            total.AirAdvectionY += contribution.AirAdvectionY;
            total.BuoyancyY += contribution.BuoyancyY;
            total.DiffusionY += contribution.DiffusionY;
            total.UnclampedVelocityY += contribution.UnclampedVelocityY;
            total.IntegratedVelocityY += contribution.IntegratedVelocityY;
            if ((contribution.Flags & 2u) != 0) total.SurfaceCarrierCells++;
        }

        for (int airIndex = 0; airIndex < sample.Coupling.Length; airIndex++)
        {
            SteamJetAirCouplingCell coupling = sample.Coupling[airIndex];
            if (coupling.SteamMask == 0) continue;
            int airX = airIndex % airWidth;
            int airY = airIndex / airWidth;
            uint mask = coupling.SteamMask;
            for (int offset = 0; offset < 16; offset++)
            {
                if ((mask & (1u << offset)) == 0) continue;
                int x = airX * 4 + offset % 4;
                int y = airY * 4 + offset / 4;
                if (x >= snapshot.Width || y >= snapshot.Height) continue;
                int band = BandForY(sourceY, y, bandCount);
                if (band < 0) continue;
                BandTotals total = bands[band] ??= new BandTotals();
                if (!total.CouplingAirIndices.Add(airIndex)) continue;
                total.PreAirSteamCells += coupling.SteamCellCount;
                total.PreAirGasCells += coupling.GasCellCount;
                total.SumImpulseX += coupling.ImpulseX;
                total.SumImpulseY += coupling.ImpulseY;
                total.SumAbsImpulseX += Math.Abs((long)coupling.ImpulseX);
                total.SumAbsImpulseY += Math.Abs((long)coupling.ImpulseY);
                total.AirLossProduct += coupling.AirLossProduct;
                total.MinimumAirLossProduct = Math.Min(total.MinimumAirLossProduct, coupling.AirLossProduct);
                total.MaximumAirLossProduct = Math.Max(total.MaximumAirLossProduct, coupling.AirLossProduct);
            }
        }

        string path = Path.Combine(directory, $"steam-jet-air-field-bands-{frame}.csv");
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,heightAboveSourceStart,heightAboveSourceEnd,worldYTop,worldYBottom,postSteamCells,postAirCoarseCells,meanAirVelocityX,meanAirVelocityY,meanPressure,meanGasVelocityY,preMotionSteamCells,meanRetainedVelocityY,meanAirAdvectionY,meanBuoyancyY,meanDiffusionY,meanUnclampedVelocityY,meanIntegratedVelocityY,surfaceCarrierFraction,couplingAirCoarseCells,preAirSteamCells,preAirGasCells,sumImpulseXFixed,sumImpulseYFixed,sumAbsImpulseXFixed,sumAbsImpulseYFixed,meanAirLossProduct,minAirLossProduct,maxAirLossProduct");
        for (int band = 0; band < bands.Length; band++)
        {
            BandTotals? total = bands[band];
            if (total is null) continue;
            int heightStart = band * 20;
            int worldYBottom = sourceY - heightStart;
            int worldYTop = worldYBottom - 19;
            double airCount = Math.Max(1, total.PostAirIndices.Count);
            double airVx = 0, airVy = 0, pressure = 0;
            foreach (int airIndex in total.PostAirIndices)
            {
                AirCell cell = air[airIndex];
                airVx += cell.VelocityX;
                airVy += cell.VelocityY;
                pressure += cell.Pressure;
            }
            double postSteamCount = Math.Max(1, total.PostSteamCells);
            double preMotionCount = Math.Max(1, total.PreMotionSteamCells);
            double couplingCount = Math.Max(1, total.CouplingAirIndices.Count);
            double minAirLoss = total.CouplingAirIndices.Count == 0 ? double.NaN : total.MinimumAirLossProduct;
            double maxAirLoss = total.CouplingAirIndices.Count == 0 ? double.NaN : total.MaximumAirLossProduct;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frame},{heightStart},{heightStart + 19},{worldYTop},{worldYBottom},{total.PostSteamCells},{total.PostAirIndices.Count},{airVx / airCount:0.000000},{airVy / airCount:0.000000},{pressure / airCount:0.000000},{total.PostGasVelocityY / postSteamCount:0.000000},{total.PreMotionSteamCells},{total.RetainedVelocityY / preMotionCount:0.000000},{total.AirAdvectionY / preMotionCount:0.000000},{total.BuoyancyY / preMotionCount:0.000000},{total.DiffusionY / preMotionCount:0.000000},{total.UnclampedVelocityY / preMotionCount:0.000000},{total.IntegratedVelocityY / preMotionCount:0.000000},{total.SurfaceCarrierCells / preMotionCount:0.000000},{total.CouplingAirIndices.Count},{total.PreAirSteamCells},{total.PreAirGasCells},{total.SumImpulseX},{total.SumImpulseY},{total.SumAbsImpulseX},{total.SumAbsImpulseY},{total.AirLossProduct / couplingCount:0.000000},{minAirLoss:0.000000},{maxAirLoss:0.000000}"));
        }
        return path;
    }

    private static string WriteFineMotion(
        string directory,
        uint frame,
        int width,
        IReadOnlyList<SteamJetGasMotionContribution> contributions)
    {
        string path = Path.Combine(directory, $"steam-jet-motion-contributions-{frame}.csv");
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,x,y,previousVelocityY,retainedVelocityY,airAdvectionY,buoyancyY,diffusionY,unclampedVelocityY,integratedVelocityY,flags");
        for (int index = 0; index < contributions.Count; index++)
        {
            SteamJetGasMotionContribution value = contributions[index];
            if ((value.Flags & 1u) == 0) continue;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frame},{index % width},{index / width},{value.PreviousVelocityY:0.000000},{value.RetainedVelocityY:0.000000},{value.AirAdvectionY:0.000000},{value.BuoyancyY:0.000000},{value.DiffusionY:0.000000},{value.UnclampedVelocityY:0.000000},{value.IntegratedVelocityY:0.000000},{value.Flags}"));
        }
        return path;
    }

    private static string WriteCoarseCoupling(
        string directory,
        uint frame,
        int width,
        IReadOnlyList<SteamJetAirCouplingCell> coupling)
    {
        int airWidth = (width + 3) / 4;
        string path = Path.Combine(directory, $"steam-jet-air-coupling-coarse-{frame}.csv");
        using StreamWriter writer = new(path, false);
        writer.WriteLine("frame,airX,airY,gasCellCount,steamCellCount,steamMask,impulseXFixed,impulseYFixed,impulseX,impulseY,airLossProduct");
        for (int index = 0; index < coupling.Count; index++)
        {
            SteamJetAirCouplingCell value = coupling[index];
            if (value.SteamMask == 0) continue;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frame},{index % airWidth},{index / airWidth},{value.GasCellCount},{value.SteamCellCount},{value.SteamMask},{value.ImpulseX},{value.ImpulseY},{value.ImpulseX / 100000.0:0.000000},{value.ImpulseY / 100000.0:0.000000},{value.AirLossProduct:0.000000}"));
        }
        return path;
    }

    private static int BandForY(int sourceY, int y, int bandCount)
    {
        int heightAboveSource = sourceY - y;
        if (heightAboveSource < 0) return -1;
        int band = heightAboveSource / 20;
        return band < bandCount ? band : -1;
    }

    private sealed class BandTotals
    {
        public int PostSteamCells;
        public readonly HashSet<int> PostAirIndices = [];
        public double PostGasVelocityY;
        public int PreMotionSteamCells;
        public double RetainedVelocityY;
        public double AirAdvectionY;
        public double BuoyancyY;
        public double DiffusionY;
        public double UnclampedVelocityY;
        public double IntegratedVelocityY;
        public int SurfaceCarrierCells;
        public readonly HashSet<int> CouplingAirIndices = [];
        public uint PreAirSteamCells;
        public uint PreAirGasCells;
        public long SumImpulseX;
        public long SumImpulseY;
        public long SumAbsImpulseX;
        public long SumAbsImpulseY;
        public double AirLossProduct;
        public double MinimumAirLossProduct = double.PositiveInfinity;
        public double MaximumAirLossProduct = double.NegativeInfinity;
    }

    private sealed class TrunkTotals
    {
        public int SteamCells;
        public double SumX;
        public double SumXSquare;
        public int MinimumX = int.MaxValue;
        public int MaximumX = -1;
        public int LeftGasCells;
        public int RightGasCells;
        public double LeftGasVelocityX;
        public double RightGasVelocityX;
        public readonly HashSet<int> AirIndices = [];
    }
}
