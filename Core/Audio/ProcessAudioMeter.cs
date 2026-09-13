using System.Runtime.InteropServices;
using AutoPickup.Logging;

namespace AutoPickup.Core.Audio;

/// <summary>CoreAudio 会话峰值表：枚举默认渲染设备的音频会话，找到 GTA5_Enhanced.exe 的会话并读 IAudioMeterInformation。</summary>
public sealed class ProcessAudioMeter : IAudioCueSource
{
    private readonly LogBus _log;
    private readonly string _processName;
    private readonly int _intervalMs;
    private volatile float _peak;
    private Thread? _thread;
    private volatile bool _stop;

    public string Name => "GTA音频会话峰值";
    public bool IsAvailable { get; private set; }
    public float CurrentPeak => _peak;

    public ProcessAudioMeter(LogBus log, string processName, int intervalMs = 100)
    {
        _log = log;
        _processName = processName;
        _intervalMs = Math.Max(40, intervalMs);
        try
        {
            var mgr = FindSessionManager(out int pid);
            if (mgr is not null && pid != 0)
            {
                _meter = FindMeter(mgr, pid);
                if (_meter is not null)
                {
                    IsAvailable = true;
                    _log.Okay("音频会话级计量就绪 (PID " + pid + ")", "Audio");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            _log.Warn("会话级计量失败: " + e.Message, "Audio");
        }
        // 回退：默认渲染设备端点级计量（整机输出，含系统/游戏声音）
        try
        {
            _meter = DeviceEndpointMeter();
            if (_meter is not null)
            {
                IsAvailable = true;
                _log.Okay("默认设备端点级计量就绪（含系统声音）", "Audio");
                return;
            }
        }
        catch (Exception e)
        {
            _log.Warn("设备级计量失败: " + e.Message, "Audio");
        }
        _log.Warn("音频计量不可用（未找到 GTA 会话且默认设备计量失败）", "Audio");
    }

    private IAudioMeterInformation? _meter;

    public bool Start()
    {
        if (!IsAvailable || _thread is { IsAlive: true }) return IsAvailable;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        _stop = true;
        try { _thread?.Join(500); } catch { }
        _thread = null;
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                var m = _meter;
                if (m is not null && m.GetPeakValue(out float v) == 0)
                    _peak = v;
            }
            catch { }
            Thread.Sleep(_intervalMs);
        }
    }

    public void Dispose() => Stop();

    // ================= COM 互操作 =================
    private const uint CLSCTX_ALL = 23;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private IAudioSessionManager2? FindSessionManager(out int pid)
    {
        pid = 0;
        Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!;
        object? obj = null;
        try
        {
            obj = Activator.CreateInstance(t);
            var en = (IMMDeviceEnumerator)obj!;
            IMMDevice? dev = null;
            int hr = en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out dev);
            if (hr != 0 || dev is null) return null;
            IntPtr pMgr = IntPtr.Zero;
            hr = dev.Activate(IID_IAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero, out pMgr);
            if (hr != 0 || pMgr == IntPtr.Zero) return null;
            var mgr = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(pMgr);
            // 找 GTA 会话 PID
            IAudioSessionEnumerator? ses = null;
            if (mgr.GetSessionEnumerator(out ses) == 0 && ses is not null)
            {
                ses.GetCount(out int n);
                var wanted = System.Diagnostics.Process.GetProcessesByName(
                    System.IO.Path.GetFileNameWithoutExtension(_processName));
                var wantedPid = wanted.Length > 0 ? wanted[0].Id : -1;
                for (int i = 0; i < n && pid == 0; i++)
                {
                    IAudioSessionControl2? c2 = null;
                    if (ses.GetSession(i, out c2) == 0 && c2 is not null)
                    {
                        if (c2.GetProcessId(out uint p) == 0 && (int)p == wantedPid)
                            pid = wantedPid;
                    }
                }
            }
            return mgr;
        }
        catch (Exception e)
        {
            _log.Warn("枚举音频会话失败: " + e.Message, "Audio");
            return null;
        }
        finally
        {
            try { if (obj is not null) Marshal.ReleaseComObject(obj); } catch { }
        }
    }

    private IAudioMeterInformation? FindMeter(IAudioSessionManager2 mgr, int pid)
    {
        IAudioSessionEnumerator? ses = null;
        try
        {
            if (mgr.GetSessionEnumerator(out ses) != 0 || ses is null) return null;
            ses.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                IAudioSessionControl2? c2 = null;
                if (ses.GetSession(i, out c2) != 0 || c2 is null) continue;
                if (c2.GetProcessId(out uint p) != 0 || (int)p != pid) continue;
                IntPtr unk = Marshal.GetIUnknownForObject(c2);
                try
                {
                    IntPtr meterPtr = IntPtr.Zero;
                    Guid iid = IID_IAudioMeterInformation;
                    int hr = Marshal.QueryInterface(unk, ref iid, out meterPtr);
                    if (hr == 0 && meterPtr != IntPtr.Zero)
                        return (IAudioMeterInformation)Marshal.GetObjectForIUnknown(meterPtr);
                }
                finally { Marshal.Release(unk); }
            }
            return null;
        }
        catch { return null; }
        finally { try { if (ses is not null) Marshal.ReleaseComObject(ses); } catch { } }
    }

    private IAudioMeterInformation? DeviceEndpointMeter()
    {
        Type tt = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!;
        object? obj = null;
        try
        {
            obj = Activator.CreateInstance(tt);
            var en = (IMMDeviceEnumerator)obj!;
            IMMDevice? dev = null;
            if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev is null) return null;
            IntPtr pm = IntPtr.Zero;
            Guid iid = IID_IAudioMeterInformation;
            int hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out pm);
            if (hr != 0 || pm == IntPtr.Zero) return null;
            return (IAudioMeterInformation)Marshal.GetObjectForIUnknown(pm);
        }
        catch { return null; }
        finally { try { if (obj is not null) Marshal.ReleaseComObject(obj); } catch { } }
    }

    // ---------- COM 接口（只声明用到的成员；vtable 顺序必须完整） ----------
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId(out IntPtr ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr pp);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr pp);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator ppEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr p);
        [PreserveSig] int UnregisterSessionNotification(IntPtr p);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string s, IntPtr p);
        [PreserveSig] int UnregisterDuckNotification(IntPtr p);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int idx, out IAudioSessionControl2 pp);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr p);
        [PreserveSig] int SetDisplayName(IntPtr p, int flags);
        [PreserveSig] int GetIconPath(out IntPtr p);
        [PreserveSig] int SetIconPath(IntPtr p, int flags);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, int flags);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr p);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr p);
        [PreserveSig] int GetSessionIdentifier(out IntPtr p);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr p);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int enable);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out int pn);
        [PreserveSig] int GetChannelsPeakValues(uint cnt, IntPtr peaks);
        [PreserveSig] int QueryHardwareSupport(out int pdw);
    }
}