using System.Text.Json.Serialization;

namespace Phyxel.Core;

[JsonConverter(typeof(JsonStringEnumConverter<SimulationMode>))]
public enum SimulationMode
{
    Sandbox,
    Simulation
}
