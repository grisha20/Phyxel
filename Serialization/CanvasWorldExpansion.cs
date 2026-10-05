using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Phyxel.Core;
using Phyxel.Physics;

namespace Phyxel.Serialization;

// Add empty cells above/right; never crop a saved scene or move its floor.
internal static class CanvasWorldExpansion
{
    internal static Rectangle CoverBounds(Rectangle canvas,int width,int height)
    {
        float scale=Math.Max(canvas.Width/(float)width,canvas.Height/(float)height);
        int renderedHeight=(int)MathF.Ceiling(height*scale);
        return new(canvas.X,canvas.Bottom-renderedHeight,(int)MathF.Ceiling(width*scale),renderedHeight);
    }
    internal static Point RequiredSize(int width, int height, Rectangle canvas)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0) return new(width,height);
        for(int attempt=0;attempt<16;attempt++)
        {
            double scale = Math.Min(canvas.Width/(double)width,canvas.Height/(double)height);
            int extraX = Math.Max(0,(int)Math.Ceiling(canvas.Width/scale-1e-6)-width);
            int extraY = Math.Max(0,(int)Math.Ceiling(canvas.Height/scale-1e-6)-height);
            // Stop within one coarse cell; otherwise alignment rounding would
            // alternately add columns/rows forever to chase an irrational ratio.
            if(extraX<=4)extraX=0;if(extraY<=4)extraY=0;
            extraX=(extraX+3)/4*4;extraY=(extraY+3)/4*4;
            if(extraX==0&&extraY==0)return new(width,height);
            width+=extraX;height+=extraY;
        }
        throw new InvalidOperationException("Canvas aspect alignment did not converge.");
    }

    internal static SimulationWorldSnapshot Expand(SimulationWorldSnapshot source, Point size)
    {
        if(size.X<source.Width||size.Y<source.Height)throw new ArgumentOutOfRangeException(nameof(size));
        if(size.X==source.Width&&size.Y==source.Height)return source;
        int top=size.Y-source.Height;
        if(top%SimulationSettings.AirCellSize!=0)throw new ArgumentException("Air-aligned expansion required.");
        byte[]? Pad(byte[]? input,int oldW,int oldH,int newW,int newH,int shift,int stride,byte[]? fresh=null)
        {
            if(input is not {Length:>0})return input;
            byte[] output=new byte[checked(newW*newH*stride)];
            if(fresh!=null)for(int i=0;i<newW*newH;i++)fresh.CopyTo(output,i*stride);
            for(int y=0;y<oldH;y++)Buffer.BlockCopy(input,y*oldW*stride,output,(y+shift)*newW*stride,oldW*stride);
            return output;
        }
        int aw=(source.Width+3)/4,ah=(source.Height+3)/4,nw=(size.X+3)/4,nh=(size.Y+3)/4;
        byte[] oxygen=BitConverter.GetBytes(1f);
        byte[] heat=new byte[8];BitConverter.GetBytes(293.15f*.016f).CopyTo(heat,0);BitConverter.GetBytes(.016f).CopyTo(heat,4);
        return new(size.X,size.Y,
            Pad(source.Grid,source.Width,source.Height,size.X,size.Y,top,Marshal.SizeOf<GridCell>())!,
            Pad(source.Air,aw,ah,nw,nh,top/4,16),
            Pad(source.GasMotion,source.Width,source.Height,size.X,size.Y,top,16),
            Pad(source.Oxidizer,source.Width,source.Height,size.X,size.Y,top,4,oxygen),
            Pad(source.AirThermal,aw,ah,nw,nh,top/4,8,heat),
            Pad(source.ReactionPending,source.Width,source.Height,size.X,size.Y,top,16),
            Pad(source.ReactionPulse,aw,ah,nw,nh,top/4,16),
            Pad(source.Filters,source.Width,source.Height,size.X,size.Y,top,4));
    }
}
