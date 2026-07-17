using Phyxel.Physics;

namespace Phyxel.Diagnostics;

internal sealed class TemperatureProbeAcceptanceTrace
{
    public TemperatureProbeResult? Sand { get; private set; }
    public TemperatureProbeResult? AcceptanceMaterial { get; private set; }
    public TemperatureProbeResult? Hot { get; private set; }
    public TemperatureProbeResult? Cold { get; private set; }
    public TemperatureProbeResult? Empty { get; private set; }
    public TemperatureProbeResult? TemperatureTool { get; private set; }
    public bool ResetAfterClear { get; private set; }
    public bool ResetAfterScale { get; private set; }

    public void Observe(uint frame, TemperatureProbeResult? result)
    {
        switch (frame)
        {
            case 29:
                Sand = result;
                break;
            case 59:
                AcceptanceMaterial = result;
                break;
            case 119:
                Hot = result;
                break;
            case 179:
                Cold = result;
                break;
            case 209:
                Empty = result;
                break;
            case 210:
                ResetAfterClear = result is null;
                break;
            case 220:
                ResetAfterScale = result is null;
                break;
        }
    }

    public void ObserveTemperatureTool(uint frame, TemperatureProbeResult? result)
    {
        if (frame == 15)
        {
            TemperatureTool = result;
        }
    }
}
