using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Phyxel.Materials;
using Phyxel.Physics;
using Phyxel.Serialization;

namespace Phyxel.Diagnostics;

public static class AcceptanceRegressionVerifier
{
    private readonly record struct ComponentMetrics(
        int Count,
        int Largest,
        int MinimumX,
        int MaximumX,
        int MinimumY,
        int MaximumY);

    public static bool Validate(
        AcceptanceScenarioMode mode,
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        string artifactDirectory,
        out string report)
    {
        return mode switch
        {
            AcceptanceScenarioMode.Bowl => ValidateBowl(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.SolidGravity => ValidateSolidGravity(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.Sand => ValidateSand(snapshot, artifactDirectory, out report),
            AcceptanceScenarioMode.Hydro => ValidateHydro(
                snapshot,
                statistics,
                framesPerSecond,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.Slope => MaterialRegressionVerifier.ValidateSlope(
                snapshot,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.Gas => MaterialRegressionVerifier.ValidateGas(
                snapshot,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.WaterStress => ValidateWaterStress(
                snapshot,
                framesPerSecond,
                out report),
            AcceptanceScenarioMode.WaterDrain => ValidateWaterDrain(snapshot, out report),
            AcceptanceScenarioMode.CommunicatingVessels => ValidateCommunicatingVessels(
                snapshot,
                statistics,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.PressureTube => ValidatePressureTube(
                snapshot,
                statistics,
                artifactDirectory,
                out report),
            AcceptanceScenarioMode.SavedPressure => ValidateSavedPressure(snapshot, out report),
            AcceptanceScenarioMode.SavedIsolation => ValidateSavedIsolation(
                snapshot,
                statistics,
                out report),
            AcceptanceScenarioMode.SavedGravity => ValidateSavedGravity(
                snapshot,
                statistics,
                framesPerSecond,
                out report),
            AcceptanceScenarioMode.Buoyancy => ValidateBuoyancy(snapshot, framesPerSecond, out report),
            AcceptanceScenarioMode.SavedSandWater => ValidateSavedSandWater(snapshot, out report),
            _ => Fail(out report)
        };
    }

    private static bool ValidateWaterStress(
        SimulationWorldSnapshot snapshot,
        double framesPerSecond,
        out string report)
    {
        int water = 0;
        int resting = 0;
        int moving = 0;
        foreach (GridCell cell in Cells(snapshot))
        {
            if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Water)
            {
                continue;
            }
            water++;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        report = $"PHYXEL_STRESS_WATER water={water} resting={resting} moving={moving} fps={framesPerSecond:0.0}";
        return water >= 500000;
    }

    private static bool ValidateWaterDrain(
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 20, 170, 20, 250);
        int rightTop = SurfaceTop(grid, snapshot.Width, 310, 460, 20, 250);
        int referenceTop = Math.Max(leftTop, rightTop);
        int water = 0;
        int sand = 0;
        int hangingWater = 0;
        int movingWater = 0;
        int unsettledWater = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                if (cell.MaterialId == (uint)MaterialId.Sand)
                {
                    sand++;
                    continue;
                }
                if (cell.MaterialId != (uint)MaterialId.Water)
                {
                    continue;
                }
                water++;
                movingWater += Speed(cell) > 0.02f ? 1 : 0;
                unsettledWater += cell.RestFrames < 60 ? 1 : 0;
                if (referenceTop > 0 && y + 4 < referenceTop)
                {
                    hangingWater++;
                }
            }
        }
        report = $"PHYXEL_WATER_DRAIN water={water} sand={sand} leftTop={leftTop} rightTop={rightTop} hanging={hangingWater} moving={movingWater} unsettled={unsettledWater}";
        return water > 5000 && sand > 1000 && Math.Abs(leftTop - rightTop) <= 2 &&
            hangingWater <= water / 100 && movingWater == 0 && unsettledWater <= 16;
    }

    private static bool ValidateCommunicatingVessels(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 35, 95, 30, 247);
        int centerTop = SurfaceTop(grid, snapshot.Width, 115, 265, 30, 247);
        int rightTop = SurfaceTop(grid, snapshot.Width, 285, 445, 30, 247);
        int finalRange = Math.Max(leftTop, Math.Max(centerTop, rightTop)) -
            Math.Min(leftTop, Math.Min(centerTop, rightTop));
        string twoSecondImage = Path.Combine(artifactDirectory, "H_vessels_2s.png");
        int imageLeft = ImageSurfaceTop(twoSecondImage, 35, 95, 30, 247);
        int imageCenter = ImageSurfaceTop(twoSecondImage, 115, 265, 30, 247);
        int imageRight = ImageSurfaceTop(twoSecondImage, 285, 445, 30, 247);
        int imageRange = Math.Max(imageLeft, Math.Max(imageCenter, imageRight)) -
            Math.Min(imageLeft, Math.Min(imageCenter, imageRight));
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Water)
            {
                continue;
            }
            water++;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialId == (uint)MaterialId.Water &&
                    (x < 20 || x > 460 || y > 251))
                {
                    leaks++;
                }
            }
        }
        int unsettled = water - resting;
        bool passed = water > 10000 && leftTop > 0 && centerTop > 0 && rightTop > 0 &&
            imageLeft > 0 && imageCenter > 0 && imageRight > 0 &&
            finalRange <= 4 && imageRange >= 16 && unsettled <= 16 && moving == 0 &&
            statistics.PressureMoves == 0 && statistics.FarColumnMoves == 0 && leaks == 0;
        report = $"PHYXEL_H_VESSELS water={water} final={leftTop}/{centerTop}/{rightTop} finalRange={finalRange} image2s={imageLeft}/{imageCenter}/{imageRight} imageRange={imageRange} resting={resting} unsettled={unsettled} moving={moving} pressureMoves={statistics.PressureMoves} farColumnMoves={statistics.FarColumnMoves} leaks={leaks}";
        return passed;
    }

    private static bool ValidatePressureTube(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int tubeTop = SurfaceTop(grid, snapshot.Width, 236, 254, 60, 245);
        int tankTop = SurfaceTop(grid, snapshot.Width, 340, 450, 60, 250);
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Water)
                {
                    continue;
                }
                water++;
                resting += cell.RestFrames >= 60 ? 1 : 0;
                moving += Speed(cell) > 0.02f ? 1 : 0;
                leaks += y > 254 || x > 475 ? 1 : 0;
            }
        }
        string fillImage = Path.Combine(artifactDirectory, "I_pressure_tube_fill.png");
        int imageTubeTop = ImageSurfaceTop(fillImage, 236, 254, 60, 245);
        int imageTankTop = ImageSurfaceTop(fillImage, 340, 450, 60, 250);
        bool passed = water > 10000 && tubeTop > 0 && tankTop > 0 &&
            imageTubeTop > 0 && imageTankTop > 0 &&
            Math.Abs(tubeTop - tankTop) <= 2 &&
            Math.Abs(imageTubeTop - imageTankTop) <= 2 &&
            resting == water && moving == 0 && statistics.PressureMoves == 0 &&
            statistics.FarColumnMoves == 0 && leaks == 0;
        report = $"PHYXEL_I_PRESSURE_TUBE water={water} final={tubeTop}/{tankTop} image300={imageTubeTop}/{imageTankTop} resting={resting} moving={moving} pressureMoves={statistics.PressureMoves} farColumnMoves={statistics.FarColumnMoves} leaks={leaks}";
        return passed;
    }

    private static bool ValidateSavedPressure(
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int tubeTop = CurvedTubeSurfaceTop(
            grid,
            snapshot.Width,
            1120,
            1240,
            620,
            790,
            1190,
            out int tubeWidth);
        int tankTop = SurfaceTop(grid, snapshot.Width, 1080, 1130, 450, 900);
        int water = 0;
        int moving = 0;
        int routed = 0;
        float minimumHead = float.MaxValue;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Water)
            {
                continue;
            }
            water++;
            moving += Speed(cell) > 0.02f ? 1 : 0;
            if (cell.Pressure > 0)
            {
                routed++;
                minimumHead = Math.Min(minimumHead, cell.Pressure - 1);
            }
        }
        bool passed = water > 100000 && tubeTop > 0 && tankTop > 0 &&
            Math.Abs(tubeTop - tankTop) <= 3 && moving == 0;
        report = $"PHYXEL_J_SAVED_PRESSURE water={water} tubeTop={tubeTop} tubeWidth={tubeWidth} tankTop={tankTop} difference={Math.Abs(tubeTop - tankTop)} moving={moving} routed={routed} minHead={(minimumHead == float.MaxValue ? -1 : minimumHead):0}";
        return passed;
    }

    private static bool ValidateSavedIsolation(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int water = CountMaterial(
            grid,
            snapshot.Width,
            0,
            snapshot.Width - 1,
            0,
            snapshot.Height - 1,
            MaterialId.Water);
        int tankTop = SurfaceTop(grid, snapshot.Width, 1100, 1450, 100, 350);
        int leftSpiralTop = SurfaceTop(grid, snapshot.Width, 580, 730, 300, 760);
        int outerRise = CountMaterial(
            grid, snapshot.Width, 620, 700, 390, 508, MaterialId.Water);
        int firstBend = CountMaterial(
            grid, snapshot.Width, 730, 780, 480, 510, MaterialId.Water);
        int innerRise = CountMaterial(
            grid, snapshot.Width, 1040, 1100, 500, 612, MaterialId.Water);
        int bottomWater = CountMaterial(
            grid, snapshot.Width, 0, snapshot.Width - 1, 1000, snapshot.Height - 1, MaterialId.Water);
        int upperWater = water - bottomWater;
        GridCell[] componentCells = grid.ToArray();
        int outerComponent = ConnectedMaterialSize(
            componentCells, snapshot.Width, snapshot.Height, 1200, 300, MaterialId.Water);
        int innerComponent = ConnectedMaterialSize(
            componentCells, snapshot.Width, snapshot.Height, 1050, 600, MaterialId.Water);
        int bottomComponent = ConnectedMaterialSize(
            componentCells, snapshot.Width, snapshot.Height, 100, 1070, MaterialId.Water);
        int moving = 0;
        int leftRouted = 0;
        float leftMinimumHead = float.MaxValue;
        for (int y = 300; y <= 760; y++)
        {
            for (int x = 580; x <= 730; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (IsMaterial(cell, MaterialId.Water) && cell.Pressure > 0)
                {
                    leftRouted++;
                    leftMinimumHead = Math.Min(leftMinimumHead, cell.Pressure - 1);
                }
            }
        }
        foreach (GridCell cell in grid)
        {
            moving += IsMaterial(cell, MaterialId.Water) && Speed(cell) > 0.02f ? 1 : 0;
        }

        // This capture contains three deliberately disconnected pools: the
        // outer vessel, the inner spiral, and the strip along the floor.  A
        // pressure route may rearrange water inside one pool, but it must not
        // transfer mass between those components merely because their columns
        // share the same x coordinate.
        bool passed = water is >= 325000 and <= 325150 &&
            outerComponent is >= 255300 and <= 255450 &&
            innerComponent is >= 40200 and <= 40300 &&
            bottomComponent is >= 29350 and <= 29480 &&
            upperWater is >= 295600 and <= 295750 &&
            tankTop is >= 180 and <= 260 &&
            leftSpiralTop is > 0 and <= 650 && leftRouted >= 1000 &&
            leftMinimumHead <= tankTop + 4 && statistics.PressureMoves > 0 &&
            statistics.FarColumnMoves == 0;
        report = $"PHYXEL_K_SAVED_SPIRAL water={water} components={outerComponent}/{innerComponent}/{bottomComponent} upperWater={upperWater} bottomWater={bottomWater} tankTop={tankTop} leftTop={leftSpiralTop} leftRoute={leftRouted}/{(leftMinimumHead == float.MaxValue ? -1 : leftMinimumHead):0} outerRise={outerRise} firstBend={firstBend} innerRise={innerRise} moving={moving} pressureMoves={statistics.PressureMoves} pressurePlans={statistics.PressurePlans} farColumnMoves={statistics.FarColumnMoves}";
        return passed;
    }

    private static bool ValidateSavedGravity(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int lowerMetal = CountMaterial(
            grid, snapshot.Width, 0, snapshot.Width - 1, 850, snapshot.Height - 1, MaterialId.Metal);
        int lowerConcrete = CountMaterial(
            grid, snapshot.Width, 0, snapshot.Width - 1, 850, snapshot.Height - 1, MaterialId.Concrete);
        int lowerSolids = lowerMetal + lowerConcrete;
        Dictionary<uint, (int Count, int Top, int Bottom)> bodies = [];
        int water = 0;
        int waterTop = snapshot.Height;
        int movingSolids = 0;
        for (int index = 0; index < grid.Length; index++)
        {
            GridCell cell = grid[index];
            int y = index / snapshot.Width;
            if (IsMaterial(cell, MaterialId.Water))
            {
                water++;
                waterTop = Math.Min(waterTop, y);
            }
            if (cell.IsActive != 0 &&
                cell.MaterialId is (uint)MaterialId.Metal or (uint)MaterialId.Concrete &&
                cell.RestFrames < 2)
            {
                movingSolids++;
            }
            if (cell.IsActive != 0 && cell.BodyId != 0 &&
                cell.MaterialId is (uint)MaterialId.Metal or (uint)MaterialId.Concrete)
            {
                bodies.TryGetValue(cell.BodyId, out (int Count, int Top, int Bottom) body);
                body.Count++;
                body.Top = body.Count == 1 ? y : Math.Min(body.Top, y);
                body.Bottom = Math.Max(body.Bottom, y);
                bodies[cell.BodyId] = body;
            }
        }
        (int Count, int Top, int Bottom) largestBody = default;
        foreach ((int Count, int Top, int Bottom) body in bodies.Values)
        {
            if (body.Count > largestBody.Count)
            {
                largestBody = body;
            }
        }
        int draft = waterTop < snapshot.Height ? largestBody.Bottom - waterTop : 0;
        bool floating = water > 1000 && largestBody.Count > 1000 && draft >= 20;
        bool passed = (lowerSolids > 1000 || floating) && movingSolids == 0;
        report = $"PHYXEL_L_SAVED_GRAVITY lowerSolids={lowerSolids} body={largestBody.Count} bounds={largestBody.Top}-{largestBody.Bottom} water={water} waterTop={waterTop} draft={draft} movingSolids={movingSolids} statsFrame={statistics.FrameIndex} statsMoving={statistics.MovingCells} statsMovingSolids={statistics.MovingSolidCells} pressureMoves={statistics.PressureMoves} fps={framesPerSecond:0.0}";
        return passed;
    }

    private static bool ValidateBuoyancy(
        SimulationWorldSnapshot snapshot,
        double framesPerSecond,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int waterTop = SurfaceTop(grid, snapshot.Width, 20, 45, 130, 250);
        int closedBottom = MaterialBottom(grid, snapshot.Width, 45, 195, 60, 250, MaterialId.Metal);
        int openBottom = MaterialBottom(grid, snapshot.Width, 205, 340, 60, 250, MaterialId.Metal);
        int blockBottom = MaterialBottom(grid, snapshot.Width, 365, 435, 60, 250, MaterialId.Metal);
        int closedMetal = CountMaterial(
            grid, snapshot.Width, 45, 195, 60, 250, MaterialId.Metal);
        int openMetal = CountMaterial(
            grid, snapshot.Width, 205, 340, 60, 250, MaterialId.Metal);
        int loadedSand = CountMaterial(
            grid, snapshot.Width, 205, 340, 60, 250, MaterialId.Sand);
        int sunkBlock = CountMaterial(
            grid, snapshot.Width, 365, 435, waterTop + 3, 250, MaterialId.Metal);
        int closedWater = CountMaterial(
            grid, snapshot.Width, 70, 170, Math.Max(0, waterTop - 80), closedBottom - 10, MaterialId.Water);
        int openWater = CountMaterial(
            grid, snapshot.Width, 230, 315, Math.Max(0, waterTop - 80), openBottom - 10, MaterialId.Water);
        int deepHull = CountMaterial(
            grid, snapshot.Width, 45, 340, waterTop + 64, 250, MaterialId.Metal);
        int totalWater = 0;
        int movingSolids = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive != 0 && cell.MaterialId == (uint)MaterialId.Water)
            {
                totalWater++;
            }
            if (cell.IsActive != 0 && cell.MaterialId == (uint)MaterialId.Metal &&
                cell.RestFrames < 2)
            {
                movingSolids++;
            }
        }
        int closedDraft = closedBottom - waterTop;
        int loadedDraft = openBottom - waterTop;
        bool passed = waterTop > 0 && closedMetal > 2400 && openMetal > 1300 &&
            loadedSand > 2500 && sunkBlock > 1000 &&
            closedDraft is >= 15 and <= 35 &&
            loadedDraft >= closedDraft + 10 && loadedDraft <= 64 &&
            blockBottom >= 245 && closedWater == 0 && openWater == 0 && deepHull == 0 &&
            totalWater is >= 32300 and <= 32450 && movingSolids == 0;
        report = $"PHYXEL_M_BUOYANCY waterTop={waterTop} drafts={closedDraft}/{loadedDraft} bottoms={closedBottom}/{openBottom}/{blockBottom} metal={closedMetal}/{openMetal} sandLoad={loadedSand} sunkBlock={sunkBlock} closedWater={closedWater} openWater={openWater} deepHull={deepHull} water={totalWater} movingSolids={movingSolids} fps={framesPerSecond:0.0}";
        return passed;
    }

    private static bool ValidateSavedSandWater(
        SimulationWorldSnapshot snapshot,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int surfaceTop = SurfaceTop(grid, snapshot.Width, 300, 1600, 550, 1000);
        int water = 0;
        int fringeWater = 0;
        int fringeAdjacentSand = 0;
        int movingFringe = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                int index = y * snapshot.Width + x;
                GridCell cell = grid[index];
                if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Water)
                {
                    continue;
                }
                water++;
                if (surfaceTop < 0 || y + 10 >= surfaceTop)
                {
                    continue;
                }
                fringeWater++;
                movingFringe += cell.RestFrames < 60 ? 1 : 0;
                bool adjacentSand =
                    (x > 0 && IsMaterial(grid[index - 1], MaterialId.Sand)) ||
                    (x + 1 < snapshot.Width && IsMaterial(grid[index + 1], MaterialId.Sand)) ||
                    (y > 0 && IsMaterial(grid[index - snapshot.Width], MaterialId.Sand)) ||
                    (y + 1 < snapshot.Height && IsMaterial(grid[index + snapshot.Width], MaterialId.Sand));
                fringeAdjacentSand += adjacentSand ? 1 : 0;
            }
        }
        bool passed = surfaceTop > 0 && water is >= 192300 and <= 192360 &&
            fringeWater <= 100 && fringeAdjacentSand <= 80 && movingFringe <= 40;
        report = $"PHYXEL_N_SAVED_SAND_WATER surface={surfaceTop} water={water} fringe={fringeWater} adjacentSand={fringeAdjacentSand} movingFringe={movingFringe}";
        return passed;
    }

    private static bool ValidateBowl(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int metal = 0;
        int water = 0;
        int sand = 0;
        int leakedWater = 0;
        int movingWater = 0;
        int movingSand = 0;
        double waterY = 0;
        double sandY = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0)
                {
                    continue;
                }
                MaterialId material = (MaterialId)cell.MaterialId;
                if (material == MaterialId.Metal) metal++;
                if (material == MaterialId.Water)
                {
                    water++;
                    waterY += y;
                    leakedWater += x < 108 || x > 331 || y > 231 ? 1 : 0;
                    movingWater += Speed(cell) > 0.02f ? 1 : 0;
                }
                if (material == MaterialId.Sand)
                {
                    sand++;
                    sandY += y;
                    movingSand += Speed(cell) > 0.02f ? 1 : 0;
                }
            }
        }
        int wallGaps = 0;
        for (int y = 115; y <= 229; y++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, 108, 120, y, MaterialId.Metal) ? 0 : 1;
            wallGaps += HasMaterial(grid, snapshot.Width, 319, 331, y, MaterialId.Metal) ? 0 : 1;
        }
        for (int x = 110; x <= 329; x++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, x, x, 220, 230, MaterialId.Metal) ? 0 : 1;
        }
        string waterImage = Path.Combine(artifactDirectory, "A_water_2s.png");
        string finalImage = Path.Combine(artifactDirectory, "A_water_sand.png");
        WaterVisualMetrics waterVisual = AnalyzeWater(waterImage, 121, 318, 120, 218);
        ColorMetrics colors = AnalyzeColors(finalImage);
        bool passed = metal > 3500 && water > 1000 && sand > 5000 && leakedWater == 0 && wallGaps == 0 &&
            movingWater == 0 && movingSand == 0 && sandY / sand > waterY / water &&
            waterVisual.Columns > 190 && waterVisual.Gaps == 0 && waterVisual.SurfaceRange <= 2 &&
            File.Exists(waterImage) && colors.Red == 0 && colors.Blue > 500 && colors.Yellow > 500 && colors.Metal > 500;
        report = $"PHYXEL_A bowlMetal={metal} water={water} sand={sand} leaked={leakedWater} gaps={wallGaps} movingWater={movingWater} movingSand={movingSand} waterY={waterY / Math.Max(1, water):0.0} sandY={sandY / Math.Max(1, sand):0.0} imageColumns={waterVisual.Columns} imageGaps={waterVisual.Gaps} surfaceRange={waterVisual.SurfaceRange} red={colors.Red}";
        return passed;
    }

    private static bool ValidateSolidGravity(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        GridCell[] componentCells = grid.ToArray();
        ComponentMetrics metal = Components(componentCells, snapshot.Width, snapshot.Height, MaterialId.Metal);
        ComponentMetrics concrete = Components(componentCells, snapshot.Width, snapshot.Height, MaterialId.Concrete);
        string offImage = Path.Combine(artifactDirectory, "B_gravity_off.png");
        string fallingImage = Path.Combine(artifactDirectory, "B_falling.png");
        string landedImage = Path.Combine(artifactDirectory, "B_landed.png");
        string splitImage = Path.Combine(artifactDirectory, "B_split_concrete.png");
        int suspendedMetal = CountColor(offImage, 50, 15, 120, 85, IsMetal);
        int suspendedConcrete = CountColor(offImage, 205, 45, 425, 75, IsConcrete);
        ColorMetrics colors = AnalyzeColors(splitImage);
        int squareCells = CountMaterial(grid, snapshot.Width, 50, 120, 180, 245, MaterialId.Metal);
        int supportedFragment = CountMaterial(grid, snapshot.Width, 125, 165, 90, 115, MaterialId.Metal);
        int floorFragment = CountMaterial(grid, snapshot.Width, 170, 210, 225, 245, MaterialId.Metal);
        int supportedConcrete = CountMaterial(grid, snapshot.Width, 210, 310, 155, 185, MaterialId.Concrete);
        int floorConcrete = CountMaterial(grid, snapshot.Width, 320, 410, 225, 245, MaterialId.Concrete);
        int landedWholeConcrete = CountColor(landedImage, 205, 155, 425, 185, IsConcrete);
        int water = CountMaterial(grid, snapshot.Width, 0, snapshot.Width - 1, 0, snapshot.Height - 1, MaterialId.Water);
        int sand = CountMaterial(grid, snapshot.Width, 0, snapshot.Width - 1, 0, snapshot.Height - 1, MaterialId.Sand);
        bool metalWhole = metal.Count == 3 && metal.Largest > 1800 && squareCells > 1800 &&
            supportedFragment > 150 && floorFragment > 150;
        bool concreteSplit = concrete.Count == 2 && concrete.Largest > 700 &&
            supportedConcrete > 650 && floorConcrete > 500 && landedWholeConcrete > 1400 &&
            concrete.MinimumY <= 170 && concrete.MaximumY >= 240;
        bool passed = metalWhole && concreteSplit && water > 500 && sand > 300 &&
            suspendedMetal > 1500 && suspendedConcrete > 1000 &&
            File.Exists(fallingImage) && File.Exists(splitImage) && colors.Red == 0;
        report = $"PHYXEL_B metalComponents={metal.Count} largestMetal={metal.Largest} square={squareCells} splitLevels={supportedFragment}/{floorFragment} concreteComponents={concrete.Count} concreteCells={concrete.Largest} concreteLevels={supportedConcrete}/{floorConcrete} landedWhole={landedWholeConcrete} water={water} sand={sand} suspendedMetal={suspendedMetal} suspendedConcrete={suspendedConcrete} red={colors.Red}";
        return passed;
    }

    private static bool ValidateSand(
        SimulationWorldSnapshot snapshot,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int[] surface = new int[snapshot.Width];
        Array.Fill(surface, -1);
        int sand = 0;
        int resting = 0;
        int moving = 0;
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Sand)
                {
                    continue;
                }
                sand++;
                surface[x] = surface[x] < 0 ? y : surface[x];
                resting += cell.RestFrames >= 30 ? 1 : 0;
                moving += Speed(cell) > 0.02f ? 1 : 0;
            }
        }
        int left = Array.FindIndex(surface, value => value >= 0);
        int right = Array.FindLastIndex(surface, value => value >= 0);
        int peakX = left;
        for (int x = Math.Max(0, left); x <= right; x++)
        {
            if (surface[x] >= 0 && surface[x] < surface[peakX]) peakX = x;
        }
        float leftAngle = Angle(surface, left, peakX);
        float rightAngle = Angle(surface, right, peakX);
        float roughness = 0;
        int samples = 0;
        int gaps = 0;
        for (int x = Math.Max(0, left + 1); x <= right; x++)
        {
            if (surface[x] < 0 || surface[x - 1] < 0)
            {
                gaps++;
                continue;
            }
            roughness += Math.Abs(surface[x] - surface[x - 1]);
            samples++;
        }
        roughness /= Math.Max(1, samples);
        ColorMetrics colors = AnalyzeColors(Path.Combine(artifactDirectory, "C_pile_3s.png"));
        bool passed = sand is >= 900 and <= 1100 && leftAngle is >= 30 and <= 45 &&
            rightAngle is >= 30 and <= 45 && roughness <= 1.5f && gaps == 0 &&
            resting == sand && moving == 0 && colors.Red == 0 && colors.Yellow > 500;
        report = $"PHYXEL_C sand={sand} resting={resting} moving={moving} width={right - left + 1} leftAngle={leftAngle:0.0} rightAngle={rightAngle:0.0} roughness={roughness:0.00} gaps={gaps} red={colors.Red}";
        return passed;
    }

    private static bool ValidateHydro(
        SimulationWorldSnapshot snapshot,
        SimulationStatistics statistics,
        double framesPerSecond,
        string artifactDirectory,
        out string report)
    {
        ReadOnlySpan<GridCell> grid = Cells(snapshot);
        int leftTop = SurfaceTop(grid, snapshot.Width, 25, 128, 100, 245);
        int rightTop = SurfaceTop(grid, snapshot.Width, 148, 250, 100, 245);
        int waterfallTop = SurfaceTop(grid, snapshot.Width, 300, 455, 100, 245);
        int water = 0;
        int resting = 0;
        int moving = 0;
        int leaks = 0;
        double leakedMass = 0;
        int minimumLeakY = snapshot.Height;
        int maximumLeakY = 0;
        int minimumLeakX = snapshot.Width;
        int maximumLeakX = 0;
        int wallGaps = 0;
        foreach (GridCell cell in grid)
        {
            if (cell.IsActive == 0 || cell.MaterialId != (uint)MaterialId.Water)
            {
                continue;
            }
            water++;
            resting += cell.RestFrames >= 60 ? 1 : 0;
            moving += Speed(cell) > 0.02f ? 1 : 0;
        }
        for (int y = 0; y < snapshot.Height; y++)
        {
            for (int x = 0; x < snapshot.Width; x++)
            {
                GridCell cell = grid[y * snapshot.Width + x];
                if (cell.IsActive != 0 && cell.MaterialId == (uint)MaterialId.Water &&
                    ((x < 10 || x > 470) || y > 249))
                {
                    leaks++;
                    leakedMass += cell.Mass;
                    minimumLeakY = Math.Min(minimumLeakY, y);
                    maximumLeakY = Math.Max(maximumLeakY, y);
                    minimumLeakX = Math.Min(minimumLeakX, x);
                    maximumLeakX = Math.Max(maximumLeakX, x);
                }
            }
        }
        for (int y = 95; y <= 254; y++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, 10, 20, y, MaterialId.Metal) ? 0 : 1;
            wallGaps += HasMaterial(grid, snapshot.Width, 255, 265, y, MaterialId.Metal) ? 0 : 1;
        }
        for (int x = 15; x <= 260; x++)
        {
            wallGaps += HasMaterial(grid, snapshot.Width, x, x, 245, 255, MaterialId.Metal) ? 0 : 1;
        }
        string equalImage = Path.Combine(artifactDirectory, "D_equal_2s.png");
        string waterfallImage = Path.Combine(artifactDirectory, "D_waterfall.png");
        int imageLeft = ImageSurfaceTop(equalImage, 25, 128, 100, 245);
        int imageRight = ImageSurfaceTop(equalImage, 148, 250, 100, 245);
        WaterVisualMetrics leftVisual = AnalyzeWater(equalImage, 25, 128, 100, 245);
        WaterVisualMetrics rightVisual = AnalyzeWater(equalImage, 148, 250, 100, 245);
        int fallingWater = CountColor(waterfallImage, 330, 80, 415, 220, IsBlue);
        ColorMetrics colors = AnalyzeColors(Path.Combine(artifactDirectory, "D_rest.png"));
        bool passed = water > 5000 && Math.Abs(leftTop - rightTop) <= 3 &&
            imageLeft > 0 && imageRight > 0 && Math.Abs(imageLeft - imageRight) >= 4 &&
            waterfallTop > 0 &&
            leftVisual.Gaps == 0 && rightVisual.Gaps == 0 &&
            resting >= water * 0.99 && moving == 0 && leaks == 0 &&
            framesPerSecond >= 55 && colors.Red == 0 && colors.Blue > 500 &&
            fallingWater > 100;
        report = $"PHYXEL_D water={water} leftTop={leftTop} rightTop={rightTop} image2s={imageLeft}/{imageRight} waterfallTop={waterfallTop} fallingWater={fallingWater} resting={resting} moving={moving} leaks={leaks} leakMass={leakedMass:0.000} leakBounds={minimumLeakX},{minimumLeakY}-{maximumLeakX},{maximumLeakY} wallGaps={wallGaps} fps={framesPerSecond:0.0} statsMoving={statistics.MovingCells} red={colors.Red}";
        return passed;
    }

    private static ComponentMetrics Components(
        GridCell[] grid,
        int width,
        int height,
        MaterialId material)
    {
        bool[] visited = new bool[grid.Length];
        int[] queue = new int[grid.Length];
        int components = 0;
        int largest = 0;
        int minX = width;
        int maxX = 0;
        int minY = height;
        int maxY = 0;
        for (int start = 0; start < grid.Length; start++)
        {
            if (visited[start] || grid[start].IsActive == 0 || grid[start].MaterialId != (uint)material)
            {
                continue;
            }
            components++;
            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            int size = 0;
            while (head < tail)
            {
                int index = queue[head++];
                int x = index % width;
                int y = index / width;
                size++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                Enqueue(grid, visited, queue, ref tail, index - 1, x > 0, material);
                Enqueue(grid, visited, queue, ref tail, index + 1, x + 1 < width, material);
                Enqueue(grid, visited, queue, ref tail, index - width, y > 0, material);
                Enqueue(grid, visited, queue, ref tail, index + width, y + 1 < height, material);
            }
            largest = Math.Max(largest, size);
        }
        return new ComponentMetrics(components, largest, minX, maxX, minY, maxY);
    }

    private static int ConnectedMaterialSize(
        GridCell[] grid,
        int width,
        int height,
        int seedX,
        int seedY,
        MaterialId material)
    {
        if (seedX < 0 || seedY < 0 || seedX >= width || seedY >= height)
        {
            return 0;
        }
        int seed = seedY * width + seedX;
        if (!IsMaterial(grid[seed], material))
        {
            return 0;
        }
        bool[] visited = new bool[grid.Length];
        int[] queue = new int[grid.Length];
        int head = 0;
        int tail = 0;
        int size = 0;
        visited[seed] = true;
        queue[tail++] = seed;
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            size++;
            Enqueue(grid, visited, queue, ref tail, index - 1, x > 0, material);
            Enqueue(grid, visited, queue, ref tail, index + 1, x + 1 < width, material);
            Enqueue(grid, visited, queue, ref tail, index - width, y > 0, material);
            Enqueue(grid, visited, queue, ref tail, index + width, y + 1 < height, material);
        }
        return size;
    }

    private static void Enqueue(
        GridCell[] grid,
        bool[] visited,
        int[] queue,
        ref int tail,
        int index,
        bool valid,
        MaterialId material)
    {
        if (!valid || visited[index] || grid[index].IsActive == 0 || grid[index].MaterialId != (uint)material)
        {
            return;
        }
        visited[index] = true;
        queue[tail++] = index;
    }

    private static float Angle(int[] surface, int edge, int peak)
    {
        int horizontal = Math.Max(1, Math.Abs(peak - edge));
        int vertical = Math.Max(0, surface[edge] - surface[peak]);
        return MathF.Atan2(vertical, horizontal) * 180 / MathF.PI;
    }

    private static int SurfaceTop(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom)
    {
        List<int> tops = [];
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                GridCell cell = grid[y * width + x];
                if (cell.IsActive != 0 && cell.MaterialId == (uint)MaterialId.Water)
                {
                    tops.Add(y);
                    break;
                }
            }
        }
        if (tops.Count == 0) return -1;
        tops.Sort();
        return tops[tops.Count / 2];
    }

    private static int MaterialBottom(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        MaterialId material)
    {
        for (int y = bottom; y >= top; y--)
        {
            if (HasMaterial(grid, width, left, right, y, material))
            {
                return y;
            }
        }
        return -1;
    }

    private static int CurvedTubeSurfaceTop(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        int expectedCenter,
        out int filledWidth)
    {
        filledWidth = 0;
        for (int y = top; y <= bottom; y++)
        {
            List<(int Start, int End)> walls = [];
            int runStart = -1;
            for (int x = left; x <= right + 1; x++)
            {
                bool solid = x <= right && grid[y * width + x].IsActive != 0 &&
                    grid[y * width + x].MaterialId != (uint)MaterialId.Water;
                if (solid && runStart < 0)
                {
                    runStart = x;
                }
                else if (!solid && runStart >= 0)
                {
                    if (x - runStart >= 4)
                    {
                        walls.Add((runStart, x - 1));
                    }
                    runStart = -1;
                }
            }
            int interiorLeft = -1;
            int interiorRight = -1;
            int bestDistance = int.MaxValue;
            for (int wall = 0; wall + 1 < walls.Count; wall++)
            {
                int gapLeft = walls[wall].End + 1;
                int gapRight = walls[wall + 1].Start - 1;
                int gapWidth = gapRight - gapLeft + 1;
                if (gapWidth is < 3 or > 45)
                {
                    continue;
                }
                int distance = Math.Abs((gapLeft + gapRight) / 2 - expectedCenter);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    interiorLeft = gapLeft;
                    interiorRight = gapRight;
                }
            }
            if (interiorLeft < 0)
            {
                continue;
            }
            int water = CountMaterial(
                grid,
                width,
                interiorLeft,
                interiorRight,
                y,
                y,
                MaterialId.Water);
            int interiorWidth = interiorRight - interiorLeft + 1;
            if (water * 2 >= interiorWidth)
            {
                filledWidth = water;
                return y;
            }
        }
        return -1;
    }

    private static int ImageSurfaceTop(string path, int left, int right, int top, int bottom)
    {
        if (!File.Exists(path)) return -1000;
        using Bitmap bitmap = new(path);
        List<int> tops = [];
        for (int x = left; x <= right; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (IsBlue(bitmap.GetPixel(x, y)))
                {
                    tops.Add(y);
                    break;
                }
            }
        }
        if (tops.Count == 0) return -1000;
        tops.Sort();
        return tops[tops.Count / 2];
    }

    private static ColorMetrics AnalyzeColors(string path)
    {
        if (!File.Exists(path)) return default;
        using Bitmap bitmap = new(path);
        int red = 0;
        int blue = 0;
        int yellow = 0;
        int metal = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                red += color.R > 120 && color.R > color.G * 1.35 && color.R > color.B * 1.35 ? 1 : 0;
                blue += IsBlue(color) ? 1 : 0;
                yellow += IsYellow(color) ? 1 : 0;
                metal += IsMetal(color) ? 1 : 0;
            }
        }
        return new ColorMetrics(red, blue, yellow, metal);
    }

    private static WaterVisualMetrics AnalyzeWater(
        string path,
        int left,
        int right,
        int top,
        int bottom)
    {
        if (!File.Exists(path)) return default;
        using Bitmap bitmap = new(path);
        int columns = 0;
        int gaps = 0;
        int minimumTop = bottom;
        int maximumTop = top;
        for (int x = left; x <= right; x++)
        {
            int first = -1;
            int last = -1;
            for (int y = top; y <= bottom; y++)
            {
                if (!IsBlue(bitmap.GetPixel(x, y))) continue;
                first = first < 0 ? y : first;
                last = y;
            }
            if (first < 0) continue;
            columns++;
            minimumTop = Math.Min(minimumTop, first);
            maximumTop = Math.Max(maximumTop, first);
            for (int y = first; y <= last; y++)
            {
                gaps += IsBlue(bitmap.GetPixel(x, y)) ? 0 : 1;
            }
        }
        return new WaterVisualMetrics(columns, gaps, maximumTop - minimumTop);
    }

    private static int CountColor(
        string path,
        int left,
        int top,
        int right,
        int bottom,
        Func<Color, bool> predicate)
    {
        if (!File.Exists(path)) return 0;
        using Bitmap bitmap = new(path);
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                count += predicate(bitmap.GetPixel(x, y)) ? 1 : 0;
            }
        }
        return count;
    }

    private static bool HasMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int y,
        MaterialId material)
    {
        for (int x = left; x <= right; x++)
        {
            GridCell cell = grid[y * width + x];
            if (cell.IsActive != 0 && cell.MaterialId == (uint)material) return true;
        }
        return false;
    }

    private static bool IsMaterial(GridCell cell, MaterialId material) =>
        cell.IsActive != 0 && cell.MaterialId == (uint)material;

    private static int CountMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        MaterialId material)
    {
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                GridCell cell = grid[y * width + x];
                count += cell.IsActive != 0 && cell.MaterialId == (uint)material ? 1 : 0;
            }
        }
        return count;
    }

    private static bool HasMaterial(
        ReadOnlySpan<GridCell> grid,
        int width,
        int x,
        int ignored,
        int top,
        int bottom,
        MaterialId material)
    {
        for (int y = top; y <= bottom; y++)
        {
            GridCell cell = grid[y * width + x];
            if (cell.IsActive != 0 && cell.MaterialId == (uint)material) return true;
        }
        return false;
    }

    private static ReadOnlySpan<GridCell> Cells(SimulationWorldSnapshot snapshot)
    {
        return MemoryMarshal.Cast<byte, GridCell>(snapshot.Grid);
    }

    private static float Speed(GridCell cell)
    {
        return Math.Abs(cell.VelocityX) + Math.Abs(cell.VelocityY);
    }

    private static bool IsBlue(Color color) => color.B > color.G + 35 && color.G > color.R + 35;
    private static bool IsYellow(Color color) => color.R > 160 && color.G > 130 && color.B < 130;
    private static bool IsMetal(Color color) => color.R is >= 125 and <= 160 && color.G is >= 140 and <= 175 && color.B is >= 145 and <= 185;
    private static bool IsConcrete(Color color) => color.R is >= 75 and <= 110 && color.G is >= 80 and <= 115 && color.B is >= 85 and <= 125;
    private static bool Fail(out string report)
    {
        report = "PHYXEL_ACCEPTANCE_MODE_MISSING";
        return false;
    }

    private readonly record struct ColorMetrics(int Red, int Blue, int Yellow, int Metal);
    private readonly record struct WaterVisualMetrics(int Columns, int Gaps, int SurfaceRange);
}
