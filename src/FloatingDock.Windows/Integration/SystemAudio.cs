using System.Runtime.InteropServices;

namespace FloatingDock.Windows.Integration;

/// <summary>Default output endpoint. Opens on demand so device changes never leave a stale endpoint.</summary>
public static class SystemAudio
{
    public sealed record State(float Volume, bool Muted);
    public static State Read() => WithEndpoint(e => { Marshal.ThrowExceptionForHR(e.GetMasterVolumeLevelScalar(out var v)); Marshal.ThrowExceptionForHR(e.GetMute(out var m)); return new State(v, m); });
    public static void SetVolume(float volume) => WithEndpoint(e => { Marshal.ThrowExceptionForHR(e.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0, 1), IntPtr.Zero)); return 0; });
    public static void SetMuted(bool mute) => WithEndpoint(e => { Marshal.ThrowExceptionForHR(e.SetMute(mute, IntPtr.Zero)); return 0; });
    private static T WithEndpoint<T>(Func<IEndpointVolume,T> action)
    {
        object? enumerator = null; IDevice? device = null; object? endpoint = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!);
            Marshal.ThrowExceptionForHR(((IDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 1, out device));
            var id = typeof(IEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref id, 23, IntPtr.Zero, out endpoint));
            return action((IEndpointVolume)endpoint);
        }
        finally
        {
            if(endpoint is not null) Marshal.ReleaseComObject(endpoint);
            if(device is not null) Marshal.ReleaseComObject(device);
            if(enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow,uint state,out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level,IntPtr context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level,IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel,float level,IntPtr context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel,float level,IntPtr context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel,out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel,out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted,IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}

