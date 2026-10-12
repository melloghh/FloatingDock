using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FloatingDock.Windows.Integration;

public static class WirelessNetworks
{
    public sealed record Network(Guid Adapter,string Name,string Profile,uint Signal,bool Connected,bool Secured,bool Available=true);
    public sealed record Snapshot(IReadOnlyList<Network> Networks,string? Error);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Interface { public Guid Id;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Name;public uint State; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Profile { [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Name;public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Ssid { public uint Length;[MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] Bytes; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Available
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Profile;
        public Ssid Ssid;public uint BssType,Bssids;public int Connectable;public uint Reason,PhyCount;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=8)] public uint[] Phys;
        public int MorePhy;public uint Signal;public int Security;public uint Auth,Cipher,Flags,Reserved;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Connection { public uint Mode;[MarshalAs(UnmanagedType.LPWStr)]public string Profile;public IntPtr Ssid,Bssids;public uint Type,Flags; }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version,IntPtr reserved,out uint negotiated,out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle,IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle,IntPtr reserved,out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(IntPtr handle,ref Guid id,uint flags,IntPtr reserved,out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(IntPtr handle,ref Guid id,IntPtr reserved,out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr handle,ref Guid id,IntPtr ssid,IntPtr ie,IntPtr reserved);
    [StructLayout(LayoutKind.Sequential)] private struct Notification { public uint Source,Code;public Guid Adapter;public uint Size;public IntPtr Data; }
    private delegate void Notify(ref Notification notification,IntPtr context);
    [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(IntPtr handle,uint source,int ignore,Notify? callback,IntPtr context,IntPtr reserved,out uint previous);
    [DllImport("wlanapi.dll")] private static extern uint WlanConnect(IntPtr handle,ref Guid id,ref Connection connection,IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr handle,ref Guid id,IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    public static Snapshot Read(bool scan=false)
    {
        var networks=new List<Network>();string? error=null;
        uint code=WlanOpenHandle(2,IntPtr.Zero,out _,out var handle);
        if(code!=0)return new(networks,code==1062?null:new Win32Exception((int)code).Message);
        try
        {
            code=WlanEnumInterfaces(handle,IntPtr.Zero,out var interfaces);
            if(code!=0)return new(networks,new Win32Exception((int)code).Message);
            try
            {
                int count=Math.Clamp(Marshal.ReadInt32(interfaces),0,32);
                for(int i=0;i<count;i++)
                {
                    var adapter=Marshal.PtrToStructure<Interface>(interfaces+8+i*Marshal.SizeOf<Interface>());
                    if(scan)ScanAndWait(handle,adapter.Id);
                    code=WlanGetAvailableNetworkList(handle,ref adapter.Id,0,IntPtr.Zero,out var list);
                    if(code!=0)
                    {
                        error=code==5?"Поиск Wi-Fi ограничен Windows. Сохранённые сети доступны без изменения разрешений.":new Win32Exception((int)code).Message;
                        if(WlanGetProfileList(handle,ref adapter.Id,IntPtr.Zero,out var profiles)==0)
                        {
                            try
                            {
                                int n=Math.Clamp(Marshal.ReadInt32(profiles),0,512);
                                for(int j=0;j<n;j++) { var p=Marshal.PtrToStructure<Profile>(profiles+8+j*Marshal.SizeOf<Profile>());if(p.Name.Length>0)networks.Add(new(adapter.Id,p.Name,p.Name,0,false,false,Available:false)); }
                            }
                            finally { WlanFreeMemory(profiles); }
                        }
                        continue;
                    }
                    try
                    {
                        int size=Math.Clamp(Marshal.ReadInt32(list),0,512);
                        for(int j=0;j<size;j++)
                        {
                            var n=Marshal.PtrToStructure<Available>(list+8+j*Marshal.SizeOf<Available>());
                            string name=Encoding.UTF8.GetString(n.Ssid.Bytes,0,(int)Math.Min(n.Ssid.Length,32));
                            if(name.Length>0)networks.Add(new(adapter.Id,name,n.Profile,n.Signal,(n.Flags&1)!=0,n.Security!=0));
                        }
                    }
                    finally { WlanFreeMemory(list); }
                }
            }
            finally { WlanFreeMemory(interfaces); }
        }
        finally { WlanCloseHandle(handle,IntPtr.Zero); }
        return new(networks.GroupBy(n=>(n.Adapter,n.Name)).Select(g=>g.OrderByDescending(n=>n.Connected).ThenByDescending(n=>n.Profile.Length>0).First()).OrderByDescending(n=>n.Connected).ThenByDescending(n=>n.Signal).ToArray(),error);
    }
    public static void ConnectSaved(Network network)
    {
        if(string.IsNullOrEmpty(network.Profile))throw new InvalidOperationException("Для этой сети ещё не сохранён профиль Windows.");
        WithHandle(h=> { var id=network.Adapter;var p=new Connection { Mode=0,Profile=network.Profile,Type=1 };return WlanConnect(h,ref id,ref p,IntPtr.Zero); });
    }
    public static void Disconnect(Network network)=>WithHandle(h=> { var id=network.Adapter;return WlanDisconnect(h,ref id,IntPtr.Zero); });
    private static void ScanAndWait(IntPtr handle,Guid id)
    {
        using var done=new ManualResetEventSlim();
        Notify callback=(ref Notification n,IntPtr context)=>
        {
            if(n.Adapter==id&&(n.Source&8)!=0&&n.Code is 7 or 8)
                try { done.Set(); }catch(ObjectDisposedException) { }
        };
        bool registered=WlanRegisterNotification(handle,8,1,callback,IntPtr.Zero,IntPtr.Zero,out _)==0;
        try
        {
            if(WlanScan(handle,ref id,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero)==0)
                if(registered)done.Wait(4500);else Thread.Sleep(2000);
        }
        finally { if(registered)WlanRegisterNotification(handle,0,1,null,IntPtr.Zero,IntPtr.Zero,out _);GC.KeepAlive(callback); }
    }
    private static void WithHandle(Func<IntPtr,uint> action)
    {
        uint code=WlanOpenHandle(2,IntPtr.Zero,out _,out var h);
        if(code!=0)throw new Win32Exception((int)code);
        try { code=action(h);if(code!=0)throw new Win32Exception((int)code); }
        finally { WlanCloseHandle(h,IntPtr.Zero); }
    }
}
