using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Island;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public int Size; public Rect Screen, Work; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BlurBehind { public uint Flags; public int Enable; public nint Region; public int Transition; }

    internal delegate void WinEvent(nint hook, uint type, nint window, int obj, int child, uint thread, uint time);
    delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rectangle, nint data);

    [DllImport("user32")] internal static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32")] internal static extern bool RemoveClipboardFormatListener(nint window);
    [DllImport("user32")] internal static extern nint GetClipboardOwner();
    [DllImport("user32")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32")] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32")] internal static extern nint GetForegroundWindow();
    [DllImport("user32")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32")] internal static extern bool IsWindow(nint window);
    [DllImport("user32")] internal static extern bool IsIconic(nint window);
    [DllImport("user32")] internal static extern bool ReleaseCapture();
    [DllImport("user32")] internal static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32")] internal static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(nint window, StringBuilder value, int capacity);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32")] static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32")] static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("shcore")] static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("dwmapi")] static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect rect, int size);
    [DllImport("dwmapi")] static extern int DwmEnableBlurBehindWindow(nint window, ref BlurBehind blur);
    [DllImport("dwmapi")] internal static extern int DwmFlush();
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("gdi32")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32")] internal static extern bool DeleteObject(nint handle);

    /// Per-pixel alpha for a normal (non-layered) window: DWM composites the GPU surface directly,
    /// so transparent WebView2 content stays hardware accelerated.
    internal static void MakeTransparent(nint window)
    {
        nint region = CreateRectRgn(0, 0, -1, -1);
        var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
        DwmEnableBlurBehindWindow(window, ref blur);
        DeleteObject(region);
        int none = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(window, 33, ref none, 4);
    }

    internal static void MediaKey(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo { public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport("user32")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    /// True when the window's UI thread shows a text caret (classic edits, many native apps).
    internal static bool HasCaret(nint window)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) && (info.Caret != 0 || (info.Flags & 1) != 0);
    }

    /// Hotkey chords leave Ctrl/Alt/Shift held; a synthetic Ctrl+V must wait until they are released.
    internal static async System.Threading.Tasks.Task ModifiersUp()
    {
        for (int i = 0; i < 40 && ((GetAsyncKeyState(0x11) | GetAsyncKeyState(0x12) | GetAsyncKeyState(0x10)) & 0x8000) != 0; i++)
            await System.Threading.Tasks.Task.Delay(40);
    }

    internal static void Paste()
    {
        keybd_event(0x11, 0, 0, 0);
        keybd_event(0x56, 0, 0, 0);
        keybd_event(0x56, 0, 2, 0);
        keybd_event(0x11, 0, 2, 0);
    }

    internal static bool MouseDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    internal static Point Cursor { get { GetCursorPos(out var p); return p; } }

    internal static Rect Bounds(nint window)
    {
        if (DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<Rect>()) != 0)
            GetWindowRect(window, out rect);
        return rect;
    }

    internal static MonitorInfo Monitor(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        return info;
    }

    internal static double MonitorScale(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? x / 96.0 : 1.0;

    internal static List<nint> Monitors()
    {
        var result = new List<nint>();
        MonitorCallback callback = (nint monitor, nint dc, ref Rect rectangle, nint data) => { result.Add(monitor); return true; };
        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return result;
    }

    internal static string WindowClass(nint window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    internal static string ProcessName(nint window)
    {
        if (window == 0) return "";
        GetWindowThreadProcessId(window, out var pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; }
        catch (Exception) { return ""; }
    }

    // ---- Crisp shell icons (IShellItemImageFactory gives up to 256 px, with alpha) ----

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(Size size, int flags, out nint bitmap); }

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, nint context, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [StructLayout(LayoutKind.Sequential)]
    struct DibSection
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public nint bmBits;
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant;
        public int f0, f1, f2; public nint section; public int offset;
    }
    [DllImport("gdi32")] static extern int GetObject(nint handle, int size, out DibSection value);

    internal static Bitmap? ShellImage(string path, int size, bool iconOnly)
    {
        try
        {
            SHCreateItemFromParsingName(path, 0, typeof(IShellItemImageFactory).GUID, out var factory);
            // SIIGBF_BIGGERSIZEOK | (ICONONLY or THUMBNAILONLY fallback)
            if (factory.GetImage(new Size(size, size), iconOnly ? 0x5 : 0x1, out nint handle) != 0 || handle == 0) return null;
            try
            {
                if (GetObject(handle, Marshal.SizeOf<DibSection>(), out var dib) == 0 || dib.bmBits == 0 || dib.bmBitsPixel != 32) return Image.FromHbitmap(handle);
                int w = dib.bmWidth, h = Math.Abs(dib.bmHeight), stride = dib.bmWidthBytes;
                var result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                var data = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                byte[] row = new byte[stride];
                bool bottomUp = dib.biHeight > 0;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(dib.bmBits + (bottomUp ? h - 1 - y : y) * stride, row, 0, stride);
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, Math.Min(stride, data.Stride));
                }
                result.UnlockBits(data);
                return result;
            }
            finally { DeleteObject(handle); }
        }
        catch (Exception) { return null; }
    }
}
