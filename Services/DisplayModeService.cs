using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ProjectionMapper.Services
{
    public sealed record DisplayMode(int Width, int Height, int RefreshRate, int BitsPerPixel, int Flags)
    {
        public string Label => $"{Width} × {Height}" + (RefreshRate > 1 ? $" · {RefreshRate} Hz" : " · Default refresh") + ((Flags & 2) != 0 ? " · Interlaced" : "");
    }
    public interface IDisplayModeBackend
    {
        DisplayMode Current(string device);
        IReadOnlyList<DisplayMode> Supported(string device);
        void Test(string device, DisplayMode mode);
        void Apply(string device, DisplayMode mode, bool persist);
    }
    /// <summary>A single-display transaction. Unconfirmed changes are restored on timeout, close, or failure.</summary>
    public sealed class DisplayModeChange : IDisposable
    {
        private readonly IDisplayModeBackend _backend;
        private readonly string _device;
        private readonly DisplayMode _original, _requested;
        private readonly object _gate = new();
        private bool _finished;
        private DisplayModeChange(IDisplayModeBackend backend, string device, DisplayMode original, DisplayMode requested)
        { _backend = backend; _device = device; _original = original; _requested = requested; }
        private static bool Matches(DisplayMode actual, DisplayMode requested) =>
            actual.Width == requested.Width && actual.Height == requested.Height && actual.BitsPerPixel == requested.BitsPerPixel &&
            actual.Flags == requested.Flags && (requested.RefreshRate <= 1 || actual.RefreshRate == requested.RefreshRate);
        public static DisplayModeChange Begin(IDisplayModeBackend backend, string device, DisplayMode requested)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(device);
            var original = backend.Current(device);
            if (!backend.Supported(device).Contains(requested)) throw new InvalidOperationException("This display no longer reports the selected mode. Refresh the list.");
            backend.Test(device, requested);
            var change = new DisplayModeChange(backend, device, original, requested);
            try
            {
                backend.Apply(device, requested, persist: false);
                if (!Matches(backend.Current(device), requested)) throw new InvalidOperationException("Windows did not activate the requested mode.");
                return change;
            }
            catch (Exception error)
            {
                try { change.Revert(); }
                catch (Exception rollback) { throw new AggregateException("Changing the display failed and its previous mode could not be restored. Use Windows Display settings.", error, rollback); }
                throw;
            }
        }
        public void Keep()
        {
            lock (_gate)
            {
                if (_finished) throw new InvalidOperationException("This resolution change has already finished.");
                if (!Matches(_backend.Current(_device), _requested)) throw new InvalidOperationException("The display mode changed again. Restore and refresh before trying another mode.");
                _backend.Apply(_device, _requested, persist: true);
                if (!Matches(_backend.Current(_device), _requested)) throw new InvalidOperationException("Windows did not retain the requested mode.");
                _finished = true;
            }
        }
        public void Revert()
        {
            lock (_gate)
            {
                if (_finished) return;
                _backend.Apply(_device, _original, persist: false);
                if (!Matches(_backend.Current(_device), _original)) throw new InvalidOperationException("Windows could not restore the previous display mode. Use Windows Display settings.");
                _finished = true;
            }
        }
        public void Dispose() => Revert();
    }
    /// <summary>Full DEVMODEW layout: read and change only the selected Windows display's pixel mode.</summary>
    public sealed class WindowsDisplayModes : IDisplayModeBackend
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumMode(string device, int number, ref NativeMode mode, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        private static extern int ChangeMode(string device, ref NativeMode mode, IntPtr window, uint flags, IntPtr parameter);
        private static NativeMode Empty() => new() { Size = (ushort)Marshal.SizeOf<NativeMode>(), DeviceName = string.Empty, FormName = string.Empty };
        private static DisplayMode Read(NativeMode mode) => new(checked((int)mode.Width), checked((int)mode.Height), checked((int)mode.Frequency), checked((int)mode.BitsPerPixel), checked((int)mode.DisplayFlags));
        public DisplayMode Current(string device)
        {
            var mode = Empty();
            if (!EnumMode(device, -1, ref mode, 0) || mode.Width == 0 || mode.Height == 0) throw new InvalidOperationException($"Windows could not read the active resolution for {device}. The display may be disconnected.");
            return Read(mode);
        }
        public IReadOnlyList<DisplayMode> Supported(string device)
        {
            var current = Current(device);
            var modes = new HashSet<DisplayMode> { current };
            for (int i = 0; i < 4096; i++)
            {
                var native = Empty();
                if (!EnumMode(device, i, ref native, 0)) break;
                if (native.Width > 0 && native.Height > 0 && native.BitsPerPixel == current.BitsPerPixel) modes.Add(Read(native));
            }
            return modes.OrderByDescending(m => m.Width).ThenByDescending(m => m.Height).ThenByDescending(m => m.RefreshRate).ThenBy(m => m.Flags).ToList();
        }
        private static void Set(string device, DisplayMode mode, uint flags)
        {
            var native = Empty();
            // Do not change display position, rotation, primary status, or another display's configuration.
            native.Fields = 0x00040000 | 0x00080000 | 0x00100000 | 0x00200000 | 0x00400000;
            native.BitsPerPixel = checked((uint)mode.BitsPerPixel); native.Width = checked((uint)mode.Width); native.Height = checked((uint)mode.Height);
            native.Frequency = checked((uint)mode.RefreshRate); native.DisplayFlags = checked((uint)mode.Flags);
            int result = ChangeMode(device, ref native, IntPtr.Zero, flags, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException(result switch
            {
                1 => "Windows requires a restart for this display mode. Select another mode or use Windows Display settings.",
                -2 => "The display driver rejected this resolution or refresh rate.",
                -3 => "Windows could not save the display setting.",
                -5 => "Windows rejected the display mode parameters.",
                _ => $"Windows could not change this display (code {result}). Check the projector connection and Windows Display settings."
            });
            Debug.WriteLine($"Display mode {device}: {mode.Label}; flags={flags}");
        }
        public void Test(string device, DisplayMode mode) => Set(device, mode, 2);
        public void Apply(string device, DisplayMode mode, bool persist) => Set(device, mode, persist ? 1u : 0u);
    }
}
