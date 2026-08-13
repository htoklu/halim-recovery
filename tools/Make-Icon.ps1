<#
.SYNOPSIS
  Converts a square source PNG into a multi-size Windows .ico.
  Makes the near-white area outside the rounded square transparent
  (flood fill from the corners), then packs 16/32/48/64/128/256 px
  PNG-compressed entries into the .ico.
.EXAMPLE
  .\Make-Icon.ps1 -Source ..\assets\icon.png -Output ..\src\HalimRecovery.App\Assets\app.ico
#>
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconMaker
{
    public static void Make(string source, string output, int[] sizes)
    {
        using (var src = new Bitmap(source))
        using (var work = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(work)) g.DrawImage(src, 0, 0, src.Width, src.Height);
            FloodTransparent(work);

            var frames = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (var frame = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(frame))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(work, 0, 0, s, s);
                    }
                    using (var ms = new MemoryStream())
                    {
                        frame.Save(ms, ImageFormat.Png);
                        frames.Add(ms.ToArray());
                    }
                }
            }
            WriteIco(output, sizes, frames);
        }
    }

    // BFS from the four corners: anything connected to a corner that is
    // near-white becomes fully transparent (background outside the rounded square).
    static void FloodTransparent(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var visited = new bool[w, h];
        var queue = new Queue<Point>();
        foreach (var p in new[] { new Point(0,0), new Point(w-1,0), new Point(0,h-1), new Point(w-1,h-1) })
            queue.Enqueue(p);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h || visited[p.X, p.Y]) continue;
            visited[p.X, p.Y] = true;
            var c = bmp.GetPixel(p.X, p.Y);
            if (c.R < 235 || c.G < 235 || c.B < 235) continue;
            bmp.SetPixel(p.X, p.Y, Color.Transparent);
            queue.Enqueue(new Point(p.X+1, p.Y)); queue.Enqueue(new Point(p.X-1, p.Y));
            queue.Enqueue(new Point(p.X, p.Y+1)); queue.Enqueue(new Point(p.X, p.Y-1));
        }
    }

    static void WriteIco(string path, int[] sizes, List<byte[]> frames)
    {
        using (var fs = new FileStream(path, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));  // width  (0 = 256)
                bw.Write((byte)(s >= 256 ? 0 : s));  // height
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write(frames[i].Length);
                bw.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) bw.Write(f);
        }
    }
}
"@

$sizes = @(256, 128, 64, 48, 32, 16)
[IconMaker]::Make((Resolve-Path $Source).Path, $Output, $sizes)
Write-Host "ICO written: $Output ($((Get-Item $Output).Length) bytes, sizes: $($sizes -join ', '))"
