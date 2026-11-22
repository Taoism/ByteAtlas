using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ByteAtlas.Controls;

/// <summary>
/// Explorer thumbnails (pictures, videos, PDFs, documents, icons for everything else) made on one background STA
/// thread, so slow thumbnail handlers never block the window. Results are cached per path and size.
/// </summary>
internal static partial class ShellThumbnails
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IntPtr item);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(IntPtr h, int size, out BITMAP bmp);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr h);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SIIGBF_RESIZETOFIT = 0, SIIGBF_BIGGERSIZEOK = 1;

    private static readonly BlockingCollection<(string Path, int Size, TaskCompletionSource<Bitmap?> Done)> Queue = [];
    private static readonly ConcurrentDictionary<(string, int), Bitmap?> Cache = new();
    private static Thread? _worker;

    /// <summary>The thumbnail of a file or folder, at most <paramref name="size"/> pixels wide and high (null when there is none).</summary>
    public static Task<Bitmap?> GetAsync(string path, int size)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<Bitmap?>(null);
        if (Cache.TryGetValue((path, size), out var b))
            return Task.FromResult(b);
        if (_worker == null)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "ByteAtlas thumbnails" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((path, size, tcs));
        return tcs.Task;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Work()
    {
        foreach (var (path, size, done) in Queue.GetConsumingEnumerable())
        {
            Bitmap? bmp = null;
            try
            {
                bmp = Cache.GetOrAdd((path, size), _ => Make(path, size));
            }
            catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException or IOException)
            {
                bmp = null;
            }
            done.TrySetResult(bmp);
            if (Cache.Count > 4000)
                Cache.Clear();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WriteableBitmap? Make(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero)
            return null;
        IntPtr hbmp = IntPtr.Zero;
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(ptr);
            var factory = (IShellItemImageFactory)obj;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            return ToBitmap(hbmp);
        }
        finally
        {
            if (hbmp != IntPtr.Zero)
                DeleteObject(hbmp);
            if (obj != null)
                Marshal.ReleaseComObject(obj);
            Marshal.Release(ptr);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WriteableBitmap? ToBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;
        var w = bm.bmWidth;
        var h = Math.Abs(bm.bmHeight);
        var bits = new byte[w * h * 4];
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbmp, 0, (uint)h, bits, ref bmi, 0) == 0)
                return null;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
        // pictures without an alpha channel come back fully transparent: make them opaque
        var anyAlpha = false;
        for (var i = 3; i < bits.Length; i += 4)
            if (bits[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        if (!anyAlpha)
            for (var i = 3; i < bits.Length; i += 4)
                bits[i] = 255;
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy(bits, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        }
        return wb;
    }
}
