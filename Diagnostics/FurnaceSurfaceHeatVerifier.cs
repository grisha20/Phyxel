using System;
using System.Linq;
using System.Runtime.InteropServices;
using Phyxel.Core;
using Phyxel.Graphics;
using Phyxel.Materials;
using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal static class FurnaceSurfaceHeatVerifier
{
    internal static void Run(SimulationDispatchCoordinator coordinator, MaterialRegistry registry)
    {
        var settings = new SimulationSettings { Paused = true, AirSimulation = false };
        var r = coordinator.DispatchFrame(settings, [new() { X = 40, Y = 40, Radius = 1,
            Density = 1, MaterialIndex = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal) }], 0);
        var table = registry.CreateGpuTable();
        foreach (ref var material in table.AsSpan()) material.AmbientCoolingRate = 0;
        uint fire = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fire),
            smoke = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Smoke),
            metal = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Metal),
            water = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Water),
            wood = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Wood),
            fixture = registry.GetRequiredRuntimeIndex(CoreMaterialIds.Fixture);
        table[fixture].ThermalConductivity = 0;
        r.Materials.Upload(r.Context, table);
        int w = r.Width, centre = 80 * w + 80, failures = 0, checks = 0;
        GridCell Cell(uint id, float t, float mass = 1) => new()
        { IsActive = 1, MaterialIndex = id, Mass = mass, Temperature = t };
        GridCell[] Read() => MemoryMarshal.Cast<byte, GridCell>(AirInventoryRegressionVerifier.Read(r, r.Grid.ReadBuffer)).ToArray();
        double Energy(GridCell[] grid) => grid.Where(c => c.IsActive != 0).Sum(c =>
            (double)c.Mass * PhaseEnthalpy.SpecificEnergy(c, table));
        void Check(bool ok, string message)
        { checks++; if (!ok) { failures++; Console.WriteLine("PHYXEL_SURFACE_FAIL " + message); } }
        GridCell[] Advance(GridCell[] grid, int ticks, string name)
        {
            r.Context.UpdateSubresource(grid, r.Grid.ReadBuffer);
            double before = Energy(grid);
            for (uint i = 0; i < ticks; i++) coordinator.DispatchThermalDiffusion(r, false, i, false);
            var after = Read(); double error = Math.Abs(Energy(after) - before);
            Check(error <= Math.Max(.01, Math.Abs(before) * .00005), name + " energy");
            Check(after.Where(c => c.IsActive != 0).All(c => float.IsFinite(c.Temperature) &&
                c.Temperature >= grid.Where(c => c.IsActive != 0).Min(c => c.Temperature) - .001 &&
                c.Temperature <= grid.Where(c => c.IsActive != 0).Max(c => c.Temperature) + .001), name + " bounded");
            Check(after.Sum(c => (double)c.Mass + c.MoistureMass + c.FuelMass) ==
                grid.Sum(c => (double)c.Mass + c.MoistureMass + c.FuelMass), name + " mass/stocks");
            Console.WriteLine(FormattableString.Invariant($"PHYXEL_SURFACE name={name} error={error:F8} receiverC={after[centre].Temperature:F6} progress={after[centre].Lifetime:F6}"));
            return after;
        }
        foreach (uint donor in new[] { fire, smoke })
        {
            var grid = new GridCell[w * r.Height];
            grid[centre] = Cell(metal, 20, 7.8f); grid[centre + 12 * w] = Cell(donor, 1000);
            var after = Advance(grid, 120, "gap/" + donor);
            Check(after[centre].Temperature > 21 && after[centre + 12 * w].Temperature < 999, "gap must heat and cool both endpoints");
        }
        uint iron=registry.GetRequiredRuntimeIndex("core:cast_iron");
        foreach(uint donor in new[]{fire,smoke})
        foreach(bool diagonal in new[]{false,true})
        {
            var grid=new GridCell[w*r.Height];grid[centre]=Cell(iron,30,7.2f);
            int hot=centre-w+(diagonal?1:0);grid[hot]=Cell(donor,1000,.1f);
            var heated=Advance(grid,1,$"metal/contact/{donor}/{diagonal}");
            Check(heated[centre].Temperature>30 && heated[hot].Temperature<1000,
                "Hot gas must heat metal through a visible face or diagonal");
            if(diagonal)
            {
                grid[centre+1]=Cell(fixture,30);grid[centre-w]=Cell(fixture,30);
                Check(Advance(grid,1,$"metal/closed-corner/{donor}")[centre].Temperature==30,
                    "Hot gas must not heat metal through a closed corner");
            }
        }
        foreach(var (dx,dy) in new[]{(12,0),(0,12),(12,12)})
        {
            var grid=new GridCell[w*r.Height];
            int donor=centre+dx+dy*w;
            grid[centre]=Cell(iron,20,7.2f);grid[donor]=Cell(iron,1000,3.6f);
            var after=Advance(grid,120,$"surface/surface/{dx},{dy}");
            Check(after[centre].Temperature>21 && after[donor].Temperature<999,
                "Visible hot wall must heat the opposite wall without FIRE or hot gas");
        }
        foreach(string control in new[]{"equal","opaque","corner","range"})
        {
            var grid=new GridCell[w*r.Height];
            int donor=centre+(control=="range"?97:12)*w+(control=="corner"?12:0);
            grid[centre]=Cell(iron,20,7.2f);grid[donor]=Cell(iron,control=="equal"?20:1000,3.6f);
            if(control=="opaque")grid[centre+6*w]=Cell(fixture,20);
            if(control=="corner")grid[centre+1]=Cell(fixture,20);
            var after=Advance(grid,1,"surface/control/"+control);
            Check(after[centre].Temperature==20,"Surface radiation must respect "+control);
        }
        foreach (string control in new[] { "cold", "range", "wall", "water", "insulator" })
        {
            var grid = new GridCell[w * r.Height]; grid[centre] = Cell(metal, 20, 7.8f);
            grid[centre + (control == "range" ? 97 : 12) * w] = Cell(fire, control == "cold" ? 20 : 1000);
            if (control == "wall" || control == "water") grid[centre + 6 * w] = Cell(control == "wall" ? fixture : water, 20);
            float savedK = table[metal].ThermalConductivity;
            if (control == "insulator") { table[metal].ThermalConductivity = 0; r.Materials.Upload(r.Context, table); }
            var after = Advance(grid, 1, control);
            Check(after[centre].Temperature == 20, control + " recipient must stay cold");
            table[metal].ThermalConductivity = savedK; r.Materials.Upload(r.Context, table);
        }
        foreach (var (dx, dy) in new[] { (48, 0), (0, 96), (40, 40), (-40, 40), (40, -40), (-40, -40) })
        {
            var grid = new GridCell[w * r.Height]; grid[centre] = Cell(metal, 20, 7.8f);
            int donorIndex = centre + dx + dy * w;
            grid[donorIndex] = Cell(fire, 1000);
            var after = Advance(grid, 1, $"extended/{dx},{dy}");
            Check(after[centre].Temperature > 20 && after[donorIndex].Temperature < 1000, "extended reciprocal visible pair");
            if (dx != 0 && dy != 0)
            {
                grid[centre + Math.Sign(dx)] = Cell(fixture, 20);
                after = Advance(grid, 1, $"corner/{dx},{dy}");
                Check(after[centre].Temperature == 20 && after[donorIndex].Temperature == 1000, "corner occludes both endpoints");
            }
        }
        var diagonalRange = new GridCell[w * r.Height];
        diagonalRange[centre] = Cell(metal, 20, 7.8f);
        diagonalRange[centre + 68 + 68 * w] = Cell(fire, 1000);
        Check(Advance(diagonalRange, 1, "euclidean/range")[centre].Temperature == 20, "diagonal exceeds Euclidean 96 range");
        // A surface sees many emitters; the pair sum must remain bounded and
        // conserve heat even when the recipient starts on its melting plateau.
        var many = new GridCell[w * r.Height]; many[centre] = Cell(metal, 1000, 7.8f);
        for (int distance = 2; distance <= 24; distance++)
            foreach (int direction in new[] { -1, 1, -w, w, -w-1, -w+1, w-1, w+1 }) many[centre + direction * distance] = Cell(fire, 1500, .2f);
        // Independent light emitters must not dilute one another just
        // because their heavy recipient sees many gas packets. Their total
        // stock is well below the wall capacity, so no recipient cap binds.
        var light=new GridCell[w*r.Height];light[centre]=Cell(iron,30,7.2f);
        double expected=0;
        for(int distance=2;distance<=24;distance++)
        foreach(int direction in new[]{-1,1,-w,w,-w-1,-w+1,w-1,w+1})
        {
            light[centre+direction*distance]=Cell(fire,1000,.001f);
            double d=distance*(Math.Abs(direction)==1 || Math.Abs(direction)==w ? 1 : Math.Sqrt(2));
            expected+=.001*.8*.18/(Math.Pow(1+d/24,2))*970/(7.2*.13);
        }
        var lightAfter=Advance(light,1,"metal/many-light-emitters");
        Check(lightAfter[centre].Temperature-30 >= .9*expected,
            "Independent light flames must retain their additive finite heat flux");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_SURFACE_LIGHT rise={lightAfter[centre].Temperature-30:F6} expected={expected:F6}"));
        // Observe the partial plateau before the now-additive emitters can
        // pay its entire latent heat. A 120-step observation can fully melt.
        var melted = Advance(many, 1, "many/melting");
        Check(melted[centre].Temperature == 1000 && melted[centre].Lifetime > 1, "metal melting plateau receives Q");
        var fullyPaid = Advance(many,120,"many/paid-melting");
        Check(fullyPaid[centre].Lifetime >= table[metal].TransitionAboveLatentHeat,
            "Heating beyond the melting plateau must first pay its latent heat");
        Advance(many, 600, "many/long");
        var shells = new GridCell[w * r.Height]; shells[centre] = Cell(fire, 1500, .2f);
        foreach (int direction in new[] { -1, 1, -w, w, -w-1, -w+1, w-1, w+1 })
            shells[centre + direction * 48] = Cell(metal, 20, 7.8f);
        Advance(shells, 120, "eight-surfaces/budget");
        // A granular surface must receive the same reciprocal radiant pair
        // as a fixed wall. Chemistry is deliberately absent in this probe.
        foreach(string id in new[]{CoreMaterialIds.Coal,CoreMaterialIds.StoneCoal,CoreMaterialIds.Sand,CoreMaterialIds.Gunpowder})
        foreach(uint donor in new[]{fire,iron})
        {
            uint receiver=registry.GetRequiredRuntimeIndex(id);
            var grid=new GridCell[w*r.Height];grid[centre]=Cell(receiver,20);
            grid[centre-12*w]=Cell(donor,1000,2);
            var heated=Advance(grid,120,$"granular/gap/{id}/{donor}");
            Check(heated[centre].Temperature>21 && heated[centre-12*w].Temperature<999,
                "Visible granular surface must exchange with fire and hot metal");
        }
        uint charcoal=registry.GetRequiredRuntimeIndex(CoreMaterialIds.Coal);
        // Compare direct finite flame heating with the same low-k grain's
        // dry interior conduction; external coupling must not raise bulk k.
        var contact=new GridCell[w*r.Height];contact[centre]=Cell(charcoal,30);
        contact[centre-w]=Cell(fire,1000,.1f);
        var direct=Advance(contact,1,"granular/direct-flame");
        float directRise=direct[centre].Temperature-30;
        Check(directRise>5,"Direct flame must heat the exposed grain without a temperature jump");
        contact[centre-w]=Cell(charcoal,1000,.1f);
        var internalHeat=Advance(contact,1,"granular/internal-conduction");
        Check(directRise>3*(internalHeat[centre].Temperature-30),"External heating must not share the slow interior rate");
        contact[centre-w]=default;contact[centre-w+1]=Cell(fire,1000,.1f);
        Check(Advance(contact,1,"granular/diagonal-flame")[centre].Temperature>30,
            "Exposed diagonal grain must receive flame heat");
        contact[centre+1]=Cell(fixture,30);contact[centre-w]=Cell(fixture,30);
        Check(Advance(contact,1,"granular/closed-contact-corner")[centre].Temperature==30,
            "Diagonal flame must not heat through a closed corner");
        foreach(string control in new[]{"opaque","corner","range","insulator"})
        {
            var grid=new GridCell[w*r.Height];grid[centre]=Cell(charcoal,20);
            int donor=centre+(control=="range"?97:-12)*w+(control=="corner"?12:0);
            grid[donor]=Cell(fire,1000,2);
            if(control=="opaque")grid[centre-6*w]=Cell(fixture,20);
            if(control=="corner")grid[centre+1]=Cell(fixture,20);
            float savedK=table[charcoal].ThermalConductivity;
            if(control=="insulator"){table[charcoal].ThermalConductivity=0;r.Materials.Upload(r.Context,table);}
            Check(Advance(grid,1,"granular/control/"+control)[centre].Temperature==20,
                "Granular radiation must respect "+control);
            table[charcoal].ThermalConductivity=savedK;r.Materials.Upload(r.Context,table);
        }
        var mound=new GridCell[w*r.Height];
        for(int y=0;y<5;y++)for(int x=-2;x<=2;x++)mound[centre+y*w+x]=Cell(charcoal,20);
        mound[centre-12*w]=Cell(fire,1000,2);
        var front=Advance(mound,1,"granular/front");
        Check(front[centre].Temperature>20 && front[centre+2*w].Temperature==20,
            "Radiation heats exposed charcoal, not its buried core in the same step");
        var serializer = new Phyxel.Serialization.SimulationStateSerializer();
        var snapshot = new Phyxel.Serialization.SimulationWorldSnapshot(w, r.Height,
            MemoryMarshal.AsBytes(melted.AsSpan()).ToArray());
        serializer.ApplyWorldSnapshot(r, snapshot);
        coordinator.RestoreWorldActivity(r, true, true, false);
        coordinator.DispatchFrame(settings, [], .05f);
        Check(Read().AsSpan().SequenceEqual(melted), "paused radiant phase exact");
        string path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("PHYXEL_ARTIFACT_DIR")!, "radiant-phase.json");
        System.Threading.Tasks.Task.Run(() => serializer.SaveAsync(path, settings, (ushort)metal, snapshot, registry)).GetAwaiter().GetResult();
        var loaded = System.Threading.Tasks.Task.Run(() => serializer.LoadAsync(path, registry)).GetAwaiter().GetResult()!.World!;
        Check(loaded.Grid.AsSpan().SequenceEqual(snapshot.Grid), "radiant partial phase save exact");
        serializer.ApplyWorldSnapshot(r, loaded); coordinator.DispatchThermalDiffusion(r, false, 0, false); var next = Read();
        serializer.ApplyWorldSnapshot(r, loaded); coordinator.DispatchThermalDiffusion(r, false, 0, false);
        Check(Read().AsSpan().SequenceEqual(next), "radiant degree scratch regenerated after reload");
        // Water receives heat from the visible metal by its existing contact;
        // the new distant transfer must not discard boiling enthalpy.
        var wet = new GridCell[w * r.Height]; wet[centre] = Cell(water, 100);
        wet[centre + w] = Cell(metal, 100, 7.8f); wet[centre + 13 * w] = Cell(fire, 1000, 5);
        var boiled = Advance(wet, 2, "water/contact/plateau");
        Check(boiled[centre].Temperature == 100 && boiled[centre].Lifetime > .01f, "water boiling plateau receives Q");
        var spent = Advance(wet, 600, "water/contact/after-latent");
        Check(spent[centre].Lifetime >= boiled[centre].Lifetime, "long transfer preserves spent boiling enthalpy");
        var porous = new GridCell[w * r.Height]; porous[centre] = Cell(wood, 100);
        porous[centre].MoistureMass = .15f; porous[centre].MoistureEnergy = 20;
        porous[centre + 12 * w] = Cell(fire, 1000, 5);
        var drying = Advance(porous, 120, "wood/drying");
        Check(drying[centre].Temperature == 100 && drying[centre].MoistureEnergy > 20.01f,
            "radiant heat enters retained water drying plateau");
        // One finite hot carrier exchange: the exposed grain warms, a
        // completely covered grain does not, and air pays the same energy.
        var surfaceGrid=new GridCell[w*r.Height];int buried=centre+20;
        surfaceGrid[centre]=Cell(charcoal,30);surfaceGrid[buried]=Cell(charcoal,30);
        foreach(int offset in new[]{-1,1,-w,w})surfaceGrid[buried+offset]=Cell(fixture,30);
        int airCount=r.AirWidth*r.AirHeight;
        var carrierHeat=new System.Numerics.Vector2[airCount];
        Array.Fill(carrierHeat,new((800+273.15f)*.016f,.016f));
        r.Context.UpdateSubresource(surfaceGrid,r.Grid.ReadBuffer);
        r.Context.UpdateSubresource(new AirCell[airCount],r.Air.Buffer);
        r.Context.UpdateSubresource(new uint[airCount],r.AirFlowLinks.Buffer);
        r.Context.UpdateSubresource(new System.Numerics.Vector2[airCount],r.AirProjectionB.Buffer);
        r.Context.UpdateSubresource(carrierHeat,r.AirThermal.Buffer);
        SimulationDispatchCoordinator.DispatchAirHeat(r,false);
        var surfaceAfter=Read();
        var airAfter=MemoryMarshal.Cast<byte,System.Numerics.Vector2>(AirInventoryRegressionVerifier.Read(r,r.AirThermal.Buffer)).ToArray();
        double carrierBalance=Energy(surfaceAfter)-Energy(surfaceGrid);
        for(int i=0;i<airCount;i++)carrierBalance+=airAfter[i].X-carrierHeat[i].X;
        Check(Math.Abs(carrierBalance)<.002,"Granular/carrier exchange must conserve finite energy");
        Check(surfaceAfter[centre].Temperature>31 && surfaceAfter[buried].Temperature==30,
            "Only the exposed grain receives carrier heat");
        Check(surfaceAfter.Sum(c=>(double)c.Mass)==surfaceGrid.Sum(c=>(double)c.Mass),"Carrier exchange must preserve grain mass");
        Console.WriteLine(FormattableString.Invariant($"PHYXEL_SURFACE_CARRIER error={carrierBalance:F8} exposedC={surfaceAfter[centre].Temperature:F6} buriedC={surfaceAfter[buried].Temperature:F6}"));
        Console.WriteLine($"PHYXEL_SURFACE_COMPLETE checks={checks} failures={failures}");
        if (failures > 0 && Environment.GetEnvironmentVariable("PHYXEL_DRAFT_BASELINE") != "1")
            throw new InvalidOperationException("Surface heating checks failed.");
    }
}
