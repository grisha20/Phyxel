using System;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace Phyxel.Graphics;

public sealed class GpuRenderTexturePair : IDisposable
{
    private int writeIndex;

    public GpuRenderTexturePair(Device device, int width, int height)
    {
        Texture2DDescription description = new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };
        Textures = [new Texture2D(device, description), new Texture2D(device, description)];
        Views =
        [
            new UnorderedAccessView(device, Textures[0]),
            new UnorderedAccessView(device, Textures[1])
        ];
    }

    public Texture2D[] Textures { get; }
    public UnorderedAccessView[] Views { get; }
    public Texture2D WriteTexture => Textures[writeIndex];
    public UnorderedAccessView WriteView => Views[writeIndex];

    public void Swap()
    {
        writeIndex = 1 - writeIndex;
    }

    public void Dispose()
    {
        foreach (UnorderedAccessView view in Views)
        {
            view.Dispose();
        }

        foreach (Texture2D texture in Textures)
        {
            texture.Dispose();
        }
    }
}
