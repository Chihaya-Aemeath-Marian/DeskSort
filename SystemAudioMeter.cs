using System;
using System.Runtime.InteropServices;

namespace DeskSort
{
    /// <summary>音声データを保存せず、Windows の既定の再生デバイスのピークレベルを読み取ります。</summary>
    internal sealed class SystemAudioMeter : IDisposable
    {
        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioMeterInformation? _meter;

        public SystemAudioMeter()
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(_enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out _device));
            var iid = typeof(IAudioMeterInformation).GUID;
            _device.Activate(ref iid, 23, IntPtr.Zero, out var meterObject);
            _meter = (IAudioMeterInformation)meterObject;
        }

        public float ReadPeak()
        {
            if (_meter == null) return 0;
            _meter.GetPeakValue(out var value);
            return Math.Max(0, Math.Min(1, value));
        }

        public void Dispose()
        {
            Release(_meter); _meter = null;
            Release(_device); _device = null;
            Release(_enumerator); _enumerator = null;
        }

        private static void Release(object? value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        private enum DataFlow { Render = 0 }
        private enum Role { Multimedia = 1 }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            void Activate(ref Guid iid, uint classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            void OpenPropertyStore(uint access, out IntPtr properties);
            void GetId(out IntPtr id);
            void GetState(out uint state);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            void GetPeakValue(out float peak);
            void GetMeteringChannelCount(out int channelCount);
            void GetChannelsPeakValues(int channelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] peakValues);
            void QueryHardwareSupport(out int hardwareSupportMask);
        }
    }
}
