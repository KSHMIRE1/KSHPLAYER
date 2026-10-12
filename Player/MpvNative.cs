using System;
using System.Runtime.InteropServices;

namespace KSHPLAYER.Player;

/// <summary>Liaisons bas niveau vers libmpv-2.dll (API C de mpv).</summary>
internal static class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    public const int MPV_FORMAT_NONE = 0;
    public const int MPV_FORMAT_STRING = 1;
    public const int MPV_FORMAT_FLAG = 3;
    public const int MPV_FORMAT_DOUBLE = 5;

    public const int MPV_EVENT_NONE = 0;
    public const int MPV_EVENT_SHUTDOWN = 1;
    public const int MPV_EVENT_END_FILE = 7;
    public const int MPV_EVENT_FILE_LOADED = 8;
    public const int MPV_EVENT_PROPERTY_CHANGE = 22;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);

    // struct mpv_event { int event_id; int error; uint64 reply_userdata; void* data; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    // struct mpv_event_property { const char* name; int format; void* data; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }
}
