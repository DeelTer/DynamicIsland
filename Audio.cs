using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Island;

/// Microphone capture through WinMM: 16 kHz mono PCM straight into a WAV file,
/// plus a live loudness level for the on-screen waveform.
sealed class Mic : IDisposable
{
    const int Rate = 16000, Buffers = 4, BufferMs = 50, BufferBytes = Rate * 2 * BufferMs / 1000;
    const int HeaderSize = 48; // WAVEHDR on x64
    const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_EVENT = 0x50000, WHDR_DONE = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Extra; }

    [DllImport("winmm")] static extern int waveInOpen(out nint handle, uint device, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm")] static extern int waveInPrepareHeader(nint handle, nint header, int size);
    [DllImport("winmm")] static extern int waveInUnprepareHeader(nint handle, nint header, int size);
    [DllImport("winmm")] static extern int waveInAddBuffer(nint handle, nint header, int size);
    [DllImport("winmm")] static extern int waveInStart(nint handle);
    [DllImport("winmm")] static extern int waveInReset(nint handle);
    [DllImport("winmm")] static extern int waveInClose(nint handle);

    readonly nint handle;
    readonly nint[] headers = new nint[Buffers];
    readonly AutoResetEvent signal = new(false);
    readonly FileStream file;
    readonly Thread thread;
    volatile bool running = true, stopping;
    long dataBytes;

    /// Called on the capture thread with a 0..1 loudness every 50 ms.
    public event Action<float>? Level;

    public Mic(string path)
    {
        var format = new WaveFormat { Tag = 1, Channels = 1, Rate = Rate, BytesPerSec = Rate * 2, Align = 2, Bits = 16 };
        int error = waveInOpen(out handle, WAVE_MAPPER, ref format, signal.SafeWaitHandle.DangerousGetHandle(), 0, CALLBACK_EVENT);
        if (error != 0) throw new IOException("Microphone unavailable (" + error + ")");
        file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        file.Write(new byte[44]); // header is filled in on Stop
        for (int i = 0; i < Buffers; i++)
        {
            nint header = Marshal.AllocHGlobal(HeaderSize);
            for (int b = 0; b < HeaderSize; b++) Marshal.WriteByte(header, b, 0);
            Marshal.WriteIntPtr(header, 0, Marshal.AllocHGlobal(BufferBytes));
            Marshal.WriteInt32(header, 8, BufferBytes);
            waveInPrepareHeader(handle, header, HeaderSize);
            waveInAddBuffer(handle, header, HeaderSize);
            headers[i] = header;
        }
        thread = new Thread(Pump) { IsBackground = true, Name = "Island mic" };
        thread.Start();
        if (waveInStart(handle) != 0) { Dispose(); throw new IOException("Microphone failed to start"); }
    }

    void Pump()
    {
        var chunk = new byte[BufferBytes];
        while (running) { signal.WaitOne(200); Drain(chunk); }
        Drain(chunk); // buffers returned by waveInReset
    }

    void Drain(byte[] chunk)
    {
        {
            foreach (var header in headers)
            {
                if ((Marshal.ReadInt32(header, 24) & WHDR_DONE) == 0) continue;
                int count = Marshal.ReadInt32(header, 12);
                if (count > 0)
                {
                    Marshal.Copy(Marshal.ReadIntPtr(header, 0), chunk, 0, count);
                    lock (file) { file.Write(chunk, 0, count); dataBytes += count; }
                    double sum = 0;
                    for (int i = 0; i + 1 < count; i += 2) { double s = (short)(chunk[i] | chunk[i + 1] << 8) / 32768.0; sum += s * s; }
                    double rms = Math.Sqrt(sum / Math.Max(1, count / 2));
                    // Perceptual scale: -60 dBFS → 0, -12 dBFS → 1.
                    double db = 20 * Math.Log10(Math.Max(rms, 1e-6));
                    Level?.Invoke((float)Math.Clamp((db + 60) / 48, 0, 1));
                }
                if (stopping) { Marshal.WriteInt32(header, 12, 0); continue; }
                Marshal.WriteInt32(header, 24, Marshal.ReadInt32(header, 24) & ~(int)WHDR_DONE);
                Marshal.WriteInt32(header, 12, 0);
                waveInAddBuffer(handle, header, HeaderSize);
            }
        }
    }

    /// Stops recording and finalises the WAV header.
    public void Stop()
    {
        if (!running) return;
        stopping = true;
        waveInReset(handle); // marks every pending buffer done
        running = false;
        signal.Set();
        thread.Join(1000);
        lock (file)
        {
            file.Position = 0;
            using var w = new BinaryWriter(file, System.Text.Encoding.ASCII, true);
            w.Write("RIFF"u8); w.Write((int)(36 + dataBytes)); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write((int)dataBytes);
            file.Flush();
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch (Exception) { }
        foreach (var header in headers)
        {
            if (header == 0) continue;
            waveInUnprepareHeader(handle, header, HeaderSize);
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(header, 0));
            Marshal.FreeHGlobal(header);
        }
        waveInClose(handle);
        file.Dispose();
        signal.Dispose();
    }
}

/// Minimal UI Automation client: is the focused element (in any app) a text input?
static class Uia
{
    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")] class CUIAutomation { }

    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        [PreserveSig] int ElementFromHandle(nint window, out IUIAutomationElement element);
        void ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
        void GetRootElementBuildCache(); void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void ControlViewWalker(); void ContentViewWalker(); void RawViewWalker();
        void RawViewCondition(); void ControlViewCondition(); void ContentViewCondition(); void CreateCacheRequest();
        void CreateTrueCondition(); void CreateFalseCondition();
        [PreserveSig] int CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value, [MarshalAs(UnmanagedType.IUnknown)] out object condition);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId();
        [PreserveSig] int FindFirst(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, out IUIAutomationElement found);
        void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int property, out object value);
    }

    const int ValueValue = 30045, TreeScopeDescendants = 4;

    /// Text of the first edit box in a browser window — the address bar (toolbar precedes page content).
    public static string? AddressBar(nint window)
    {
        var automation = (IUIAutomation)new CUIAutomation();
        if (automation.ElementFromHandle(window, out var root) != 0 || root == null) return null;
        if (automation.CreatePropertyCondition(ControlType, Edit, out var condition) != 0) return null;
        if (root.FindFirst(TreeScopeDescendants, condition, out var edit) != 0 || edit == null) return null;
        return edit.GetCurrentPropertyValue(ValueValue, out var value) == 0 ? value as string : null;
    }

    const int ControlType = 30003, IsPassword = 30019, IsTextPattern = 30040, IsValuePattern = 30043, ValueReadOnly = 30046;
    const int Edit = 50004, Document = 50030;

    /// The focused element is a password box (in any app that exposes UI Automation).
    public static bool FocusedIsPassword()
    {
        var automation = (IUIAutomation)new CUIAutomation();
        if (automation.GetFocusedElement(out var element) != 0 || element == null) return false;
        return element.GetCurrentPropertyValue(IsPassword, out var v) == 0 && v is true;
    }

    public static bool FocusedIsEditable()
    {
        var automation = (IUIAutomation)new CUIAutomation();
        if (automation.GetFocusedElement(out var element) != 0 || element == null) return false;
        object Get(int id) => element.GetCurrentPropertyValue(id, out var v) == 0 ? v : null!;
        if (Get(IsPassword) is true) return false;
        if (Get(ControlType) is Edit) return true;
        // Whole web pages are Documents too; only count ones that report a writable value.
        if (Get(IsValuePattern) is true) return Get(ValueReadOnly) is not true;
        return Get(ControlType) is not Document && Get(IsTextPattern) is true;
    }
}

/// Audio output devices: list active speakers/headphones and switch the system default.
static class AudioDevices
{
    public record Device(string Id, string Name, bool Default);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public int Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Pointer; }

    // Undocumented but stable since Windows 7; used by every third-party output switcher.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
        void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
        void GetPropertyValue(); void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    [DllImport("ole32")] static extern int PropVariantClear(ref PropVariant value);

    public static List<Device> List()
    {
        var result = new List<Device>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        string defaultId = "";
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out var current) == 0) current.GetId(out defaultId);
        if (enumerator.EnumAudioEndpoints(0, 1, out var devices) != 0) return result;
        devices.GetCount(out int count);
        var nameKey = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
        for (int i = 0; i < count; i++)
        {
            if (devices.Item(i, out var device) != 0) continue;
            device.GetId(out string id);
            string name = id;
            if (device.OpenPropertyStore(0, out var store) == 0 && store.GetValue(ref nameKey, out var value) == 0)
            {
                if (value.Type == 31 && value.Pointer != 0) name = Marshal.PtrToStringUni(value.Pointer) ?? id;
                PropVariantClear(ref value);
            }
            result.Add(new Device(id, name, id == defaultId));
        }
        return result;
    }

    public static bool SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        bool ok = true;
        for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(id, role) == 0; // console, multimedia, communications
        return ok;
    }
}
