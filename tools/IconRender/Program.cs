// Renders art/byteatlas.svg to PNGs and a multi-size .ico: IconRender <svg> <outDir>
using SkiaSharp;
using Svg.Skia;

var svgPath = args.Length > 0 ? args[0] : "art/byteatlas.svg";
var outDir = args.Length > 1 ? args[1] : "art/out";
Directory.CreateDirectory(outDir);
using var svg = new SKSvg();
var picture = svg.Load(svgPath) ?? throw new InvalidOperationException("SVG did not load");
var bounds = picture.CullRect;
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512];
var pngs = new Dictionary<int, byte[]>();
foreach (var size in sizes)
{
    using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / bounds.Width, size / bounds.Height);
        canvas.DrawPicture(picture);
    }
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    pngs[size] = data.ToArray();
    File.WriteAllBytes(Path.Combine(outDir, $"byteatlas-{size}.png"), pngs[size]);
}
// .ico with PNG entries (Vista+ format)
int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
using var ico = new BinaryWriter(File.Create(Path.Combine(outDir, "byteatlas.ico")));
ico.Write((ushort)0); ico.Write((ushort)1); ico.Write((ushort)icoSizes.Length);
var offset = 6 + 16 * icoSizes.Length;
foreach (var s in icoSizes)
{
    ico.Write((byte)(s >= 256 ? 0 : s)); ico.Write((byte)(s >= 256 ? 0 : s));
    ico.Write((byte)0); ico.Write((byte)0); ico.Write((ushort)1); ico.Write((ushort)32);
    ico.Write(pngs[s].Length); ico.Write(offset);
    offset += pngs[s].Length;
}
foreach (var s in icoSizes)
    ico.Write(pngs[s]);
Console.WriteLine("Wrote " + Path.GetFullPath(outDir));
