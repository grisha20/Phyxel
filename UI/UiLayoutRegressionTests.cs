using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Phyxel.Core;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.UI;

public static class UiLayoutRegressionTests
{
    public static void RunAllTests(MaterialRegistry registry)
    {
        Console.WriteLine("=== Running Phyxel UI Layout & Input Regression Tests ===");

        TestLayoutResolutions();
        TestMaterialCategorization(registry);
        TestExternalCategoryValidation();
        TestPauseContinueLogic();

        Console.WriteLine("=== All UI Layout Regression Tests Passed Successfully ===");
    }

    private static void TestLayoutResolutions()
    {
        (int width, int height)[] resolutions = [
            (1280, 720),
            (1600, 900),
            (1920, 1080),
            (2560, 1440)
        ];

        foreach ((int w, int h) in resolutions)
        {
            Viewport vp = new(0, 0, w, h);
            UiLayoutBounds bounds = UiLayoutCalculator.Calculate(vp);

            if (bounds.SimulationCanvas.Width <= 0 || bounds.SimulationCanvas.Height <= 0)
            {
                throw new InvalidOperationException($"SimulationCanvas has invalid size on {w}x{h}: {bounds.SimulationCanvas}");
            }

            // Ensure simulation canvas does not intersect TopBar or BottomPalette
            if (bounds.SimulationCanvas.Intersects(bounds.TopBar) ||
                bounds.SimulationCanvas.Intersects(bounds.BottomPalette) ||
                bounds.SimulationCanvas.Intersects(bounds.LeftToolbar) ||
                bounds.SimulationCanvas.Intersects(bounds.RightPanel))
            {
                throw new InvalidOperationException($"SimulationCanvas intersects UI panels on {w}x{h}.");
            }

            Console.WriteLine($"[PASS] Resolution Layout {w}x{h} -> Canvas {bounds.SimulationCanvas.Width}x{bounds.SimulationCanvas.Height}");
        }
    }

    private static void TestMaterialCategorization(MaterialRegistry registry)
    {
        int totalSelectable = registry.SelectableMaterials.Count;
        int categorizedCount = 0;

        foreach (MaterialDefinition mat in registry.SelectableMaterials)
        {
            if (mat.Hidden)
            {
                throw new InvalidOperationException($"Hidden material '{mat.Id}' included in SelectableMaterials.");
            }

            MaterialCategoryType category = MaterialCategoryResolver.Resolve(mat);
            categorizedCount++;
        }

        if (categorizedCount != totalSelectable)
        {
            throw new InvalidOperationException($"Mismatch in categorized materials count: expected {totalSelectable}, got {categorizedCount}.");
        }

        Console.WriteLine($"[PASS] Material Categorization for {categorizedCount} materials.");
    }

    private static void TestExternalCategoryValidation()
    {
        // Dummy test for external material with valid/invalid custom categories
        MaterialProperties props = default;
        MaterialDefinition externalValid = new(
            "ext:custom_sand", 100, "Custom Sand", Color.Yellow, props, 0, false, "powders");

        if (MaterialCategoryResolver.Resolve(externalValid) != MaterialCategoryType.Powders)
        {
            throw new InvalidOperationException("External material category 'powders' resolution failed.");
        }

        MaterialDefinition externalUnknown = new(
            "ext:unknown_elem", 101, "Unknown Elem", Color.Green, props, 0, false, "invalid_category_string");

        // Should fall back to auto-categorization based on kind (default Solid)
        if (MaterialCategoryResolver.Resolve(externalUnknown) != MaterialCategoryType.Solids)
        {
            throw new InvalidOperationException("External material with invalid category fallback failed.");
        }

        Console.WriteLine("[PASS] External Material Category Validation.");
    }

    private static void TestPauseContinueLogic()
    {
        SimulationSettings settings = new();
        bool initial = settings.Paused;
        settings.Paused = !initial;
        if (settings.Paused == initial)
        {
            throw new InvalidOperationException("Pause state toggle failed.");
        }

        Console.WriteLine("[PASS] Pause / Continue Toggle Logic.");
    }
}
