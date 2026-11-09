using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ProjectionMapper.Services
{
    /// <summary>Monitor interface identity supplements the display name; unavailable identity always requires review.</summary>
    public static class WindowsDisplayIdentity
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
        public static string ComputerId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName))).ToLowerInvariant();
        public static string MonitorId(string deviceName)
        {
            try
            {
                var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                return EnumDisplayDevices(deviceName, 0, ref display, 1) ? display.DeviceId ?? string.Empty : string.Empty;
            }
            catch (Exception ex) { Debug.WriteLine($"Display identity unavailable: {ex.Message}"); return string.Empty; }
        }
    }
}
