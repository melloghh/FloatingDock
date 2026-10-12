using System.Runtime.InteropServices;
using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FloatingDock.UI.Dock;

/// <summary>System-compositor texture upload. D3D/D2D resources are shared by one dock window; decoding is bounded to the requested icon size.</summary>
internal sealed class SystemDockGraphics : IDisposable
{
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out uint feature, out IntPtr context);
    [DllImport("d2d1.dll")] private static extern int D2D1CreateDevice(IntPtr dxgi, IntPtr properties, out IntPtr device);
    [StructLayout(LayoutKind.Sequential)] private struct Size { public uint Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapProperties { public uint Format, Alpha; public float DpiX, DpiY; }
    internal Compositor Compositor { get; } = new();
    internal ContainerVisual Root { get; }
    internal ContainerVisual Content { get; }
    private readonly DesktopWindowTarget _target;
    private readonly CompositionGraphicsDevice _graphics;
    private bool _disposed;

    internal unsafe SystemDockGraphics(IntPtr hwnd)
    {
        Root = Compositor.CreateContainerVisual(); Content = Compositor.CreateContainerVisual(); Root.Children.InsertAtTop(Content);
        IntPtr compositor = WinRT.MarshalInspectable<Compositor>.FromManaged(Compositor), interop = IntPtr.Zero;
        IntPtr d3d = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, d2d = IntPtr.Zero;
        try
        {
            var desktopIid = new Guid("29e691fa-4567-4dca-b319-d0f207eb6807");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositor, in desktopIid, out interop));
            IntPtr target = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)(*(IntPtr**)interop)[3])(interop, hwnd, 1, &target));
            try { _target = WinRT.MarshalInspectable<DesktopWindowTarget>.FromAbi(target); } finally { Marshal.Release(target); }
            _target.Root = Root;
            Marshal.Release(interop); interop = IntPtr.Zero;
            int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0x20 /* BGRA */, IntPtr.Zero, 0, 7, out d3d, out _, out context);
            if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out d3d, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
            var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in dxgiIid, out dxgi));
            Marshal.ThrowExceptionForHR(D2D1CreateDevice(dxgi, IntPtr.Zero, out d2d));
            var compositorIid = new Guid("25297d5c-3ad4-4c9c-b5cf-e36a38512330");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositor, in compositorIid, out interop));
            IntPtr graphics = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(IntPtr**)interop)[5])(interop, d2d, &graphics));
            try { _graphics = WinRT.MarshalInspectable<CompositionGraphicsDevice>.FromAbi(graphics); } finally { Marshal.Release(graphics); }
        }
        finally
        {
            foreach(var pointer in new[] { interop, compositor, d2d, dxgi, context, d3d }) if (pointer != IntPtr.Zero) Marshal.Release(pointer);
        }
    }

    internal void SetViewport(float width, float height, double scale)
    {
        Root.Size = new Vector2(width * (float)scale, height * (float)scale); Content.Size = new Vector2(width, height); Content.Scale = new Vector3((float)scale, (float)scale, 1);
    }

    internal async Task<CompositionDrawingSurface> DecodeAsync(byte[] bytes, int pixels)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double factor = Math.Min(1, pixels / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * factor)), height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * factor));
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform { ScaledWidth = width, ScaledHeight = height }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        if (_disposed) throw new ObjectDisposedException(nameof(SystemDockGraphics));
        return Upload(data.DetachPixelData(), (int)width, (int)height);
    }

    internal unsafe CompositionDrawingSurface Upload(byte[] pixels, int width, int height)
    {
        if (width < 1 || height < 1 || width > 2048 || height > 2048 || pixels.Length < checked(width * height * 4)) throw new ArgumentException("Invalid texture size.");
        var surface = _graphics.CreateDrawingSurface(new global::Windows.Foundation.Size(width, height), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        IntPtr abi = WinRT.MarshalInspectable<CompositionDrawingSurface>.FromManaged(surface), interop = IntPtr.Zero, context = IntPtr.Zero, bitmap = IntPtr.Zero;
        bool drawing = false;
        try
        {
            var iid = new Guid("fd04e6e3-fe0c-4c3c-ab19-a07601a576ee");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in iid, out interop));
            var contextIid = new Guid("e8f7fe7a-191c-466d-ad95-975678bda998");
            Point offset = default;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, Point*, int>)(*(IntPtr**)interop)[3])(interop, IntPtr.Zero, &contextIid, &context, &offset));
            drawing = true;
            var properties = new BitmapProperties { Format = 87, Alpha = 1, DpiX = 96, DpiY = 96 };
            fixed(byte* source = pixels)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Size, byte*, uint, BitmapProperties*, IntPtr*, int>)(*(IntPtr**)context)[4])(context, new Size { Width = (uint)width, Height = (uint)height }, source, (uint)width * 4, &properties, &bitmap));
            Vector4 transparent = default;
            ((delegate* unmanaged[Stdcall]<IntPtr, Vector4*, void>)(*(IntPtr**)context)[47])(context, &transparent);
            var destination = new Rect { Left = offset.X, Top = offset.Y, Right = offset.X + width, Bottom = offset.Y + height };
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Rect*, float, uint, IntPtr, void>)(*(IntPtr**)context)[26])(context, bitmap, &destination, 1, 1, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(IntPtr**)interop)[4])(interop)); drawing = false;
            return surface;
        }
        catch
        {
            if (drawing) { ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(IntPtr**)interop)[4])(interop); drawing = false; }
            surface.Dispose(); throw;
        }
        finally
        {
            if (drawing) ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(IntPtr**)interop)[4])(interop);
            foreach(var pointer in new[] { bitmap, context, interop, abi }) if (pointer != IntPtr.Zero) Marshal.Release(pointer);
        }
    }

    public void Dispose() { _disposed = true; _target.Root = null; _target.Dispose(); _graphics.Dispose(); Content.Dispose(); Root.Dispose(); Compositor.Dispose(); }
}

