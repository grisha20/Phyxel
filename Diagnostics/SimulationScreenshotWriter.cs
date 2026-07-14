using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Phyxel.Graphics;
using SharpDX.Direct3D11;

namespace Phyxel.Diagnostics;

public static class SimulationScreenshotWriter
{
    public static void Save(GpuSimulationResources resources, string path)
    {
        Texture2DDescription sourceDescription = resources.NativeReadTexture.Description;
        Texture2DDescription stagingDescription = sourceDescription;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CpuAccessFlags = CpuAccessFlags.Read;
        stagingDescription.OptionFlags = ResourceOptionFlags.None;
        using Texture2D staging = new(resources.Device, stagingDescription);
        resources.Context.CopyResource(resources.NativeReadTexture, staging);
        SharpDX.DataBox data = resources.Context.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
        int rowBytes = resources.Width * 4;
        byte[] pixels = new byte[rowBytes * resources.Height];
        for (int y = 0; y < resources.Height; y++)
        {
            Marshal.Copy(IntPtr.Add(data.DataPointer, y * data.RowPitch), pixels, y * rowBytes, rowBytes);
        }
        resources.Context.UnmapSubresource(staging, 0);

        for (int index = 0; index < pixels.Length; index += 4)
        {
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        }

        using Bitmap bitmap = new(resources.Width, resources.Height, PixelFormat.Format32bppArgb);
        BitmapData bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, resources.Width, resources.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        for (int y = 0; y < resources.Height; y++)
        {
            Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), rowBytes);
        }
        bitmap.UnlockBits(bitmapData);
        bitmap.Save(path, ImageFormat.Png);
    }
}
