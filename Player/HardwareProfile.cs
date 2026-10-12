using System;
using System.Runtime.InteropServices;

namespace KSHPLAYER.Player;

public enum QualityTier { Light, Balanced, Max }

/// <summary>
/// Détecte la carte graphique du PC (via DXGI) pour adapter le rendu :
/// grosse carte = qualité max, puce intégrée = mode léger et fluide.
/// </summary>
public sealed class HardwareProfile
{
    public string GpuName { get; private init; } = "Carte graphique inconnue";
    public long DedicatedVramMb { get; private init; }
    public QualityTier Tier { get; private init; } = QualityTier.Balanced;

    public string Label => Tier switch
    {
        QualityTier.Max => "Qualité max",
        QualityTier.Light => "Mode léger",
        _ => "Équilibré",
    };

    public static HardwareProfile Detect()
    {
        try
        {
            var (name, vram) = FindBestAdapter();
            if (name == null) return new HardwareProfile();
            long mb = vram / (1024 * 1024);
            var tier = mb >= 6000 ? QualityTier.Max
                     : mb >= 1800 ? QualityTier.Balanced
                     : QualityTier.Light;   // puces intégrées (Intel UHD/Iris, AMD Radeon intégrées…)
            return new HardwareProfile { GpuName = name, DedicatedVramMb = mb, Tier = tier };
        }
        catch
        {
            return new HardwareProfile();   // en cas de doute : réglage équilibré
        }
    }

    // ---- DXGI : on parcourt les cartes et on garde celle qui a le plus de VRAM dédiée ----

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
    }

    private static unsafe (string? name, long vram) FindBestAdapter()
    {
        if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) < 0) return (null, 0);
        string? bestName = null; long bestVram = -1;
        try
        {
            var fvt = *(IntPtr**)factory;
            // IDXGIFactory1::EnumAdapters1 = entrée 12 de la vtable
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fvt[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0) break;   // DXGI_ERROR_NOT_FOUND = fin
                try
                {
                    var avt = *(IntPtr**)adapter;
                    // IDXGIAdapter1::GetDesc1 = entrée 10
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)avt[10];
                    var buf = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_ADAPTER_DESC1>());
                    try
                    {
                        if (getDesc1(adapter, buf) < 0) continue;
                        var d = Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>(buf);
                        if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                        long vram = (long)d.DedicatedVideoMemory;
                        if (vram > bestVram) { bestVram = vram; bestName = d.Description.Trim(); }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return (bestName, Math.Max(0, bestVram));
    }
}
