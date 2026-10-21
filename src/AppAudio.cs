// Per-app audio: audio sessions (volume, mute, left/right balance) and per-app output routing.
//
// Routing uses the undocumented Windows.Media.Internal.AudioPolicyConfig activation factory;
// Windows persists the choice per app. Interface layout as used by EarTrumpet (21H2+ variant).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MiniAudio
{
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    // IAudioSessionControl methods are repeated first: COM interop interfaces cannot inherit
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IChannelAudioVolume
    {
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetChannelVolume(int index, float level, ref Guid context);
        [PreserveSig] int GetChannelVolume(int index, out float level);
    }

    static class SessionState { public const int Inactive = 0, Active = 1, Expired = 2; }

    // Session channel volumes carry the balance (channels 0/1) and, with software volume, a gain on every channel.
    // Without software volume the loudest channel of a session is always 1.
    static class ChannelMath
    {
        // Linear gain for an endpoint level in dB, so software volume sounds like the device's own volume control
        public static float GainFromDb(float db)
        {
            if (float.IsNaN(db)) return 1f;
            return (float)Math.Max(0.0, Math.Min(1.0, Math.Pow(10.0, db / 20.0)));
        }

        // -1 = fully left, 0 = center, 1 = fully right; channels after the first two (center, surround) get only the gain
        public static float[] Levels(float balance, float gain, int count)
        {
            if (count <= 0) return new float[0];
            balance = Math.Max(-1f, Math.Min(1f, balance));
            gain = Math.Max(0f, Math.Min(1f, gain));
            var levels = new float[count];
            for (int i = 0; i < count; i++) levels[i] = gain;
            if (count >= 2)
            {
                levels[0] = (balance > 0 ? 1f - balance : 1f) * gain;
                levels[1] = (balance < 0 ? 1f + balance : 1f) * gain;
            }
            return levels;
        }

        // Balance from the left/right channel volumes; independent of any gain applied to both
        public static float Balance(float left, float right)
        {
            float max = Math.Max(left, right);
            if (max <= 1e-9f || Math.Abs(left - right) < 0.005f * max) return 0f;
            return left > right ? right / left - 1f : 1f - left / right;
        }

        // Removes a gain: scales the levels so the loudest channel is 1 again (keeps the balance)
        public static float[] Normalize(float[] levels)
        {
            float max = 0f;
            foreach (float v in levels) max = Math.Max(max, v);
            var result = new float[levels.Length];
            for (int i = 0; i < levels.Length; i++) result[i] = max <= 1e-9f ? 1f : Math.Min(1f, levels[i] / max);
            return result;
        }
    }

    // One audio session of a process on one output device
    class SessionHandle
    {
        public string InstanceId;
        public int Pid;
        public int State;
        public IAudioSessionControl2 Control;
        public ISimpleAudioVolume Volume;
        public IChannelAudioVolume Channels;
        public IAudioMeterInformation Meter;
        public float Gain = 1f; // software volume of its device, 1 when off
    }

    // An app, i.e. all sessions of the same executable across all output devices
    class AppSession
    {
        public string Key;      // lower-case exe name, or "#system"
        public string Name;
        public bool IsSystem;
        public bool Active;
        public Bitmap Icon;
        public string RouteId;  // persisted output device id; null = follows the default device
        public List<int> Pids = new List<int>();
        internal List<SessionHandle> Handles = new List<SessionHandle>();

        static Guid noContext = Guid.Empty;

        public float GetVolume()
        {
            float v;
            foreach (var h in Handles)
                try { if (h.Volume != null && h.Volume.GetMasterVolume(out v) == 0) return v; } catch { }
            return -1f;
        }

        public void SetVolume(float level)
        {
            level = Math.Max(0f, Math.Min(1f, level));
            foreach (var h in Handles)
                try { if (h.Volume != null) h.Volume.SetMasterVolume(level, ref noContext); } catch { }
        }

        public bool GetMute()
        {
            bool m;
            foreach (var h in Handles)
                try { if (h.Volume != null && h.Volume.GetMute(out m) == 0) return m; } catch { }
            return false;
        }

        public void SetMute(bool mute)
        {
            foreach (var h in Handles)
                try { if (h.Volume != null) h.Volume.SetMute(mute, ref noContext); } catch { }
        }

        public float GetPeak()
        {
            float max = 0f, p;
            foreach (var h in Handles)
                try { if (h.Meter != null && h.Meter.GetPeakValue(out p) == 0 && p > max) max = p; } catch { }
            return max;
        }

        public bool HasBalance
        {
            get
            {
                foreach (var h in Handles) if (ChannelCount(h) >= 2) return true;
                return false;
            }
        }

        // -1 = fully left, 0 = center, 1 = fully right
        public float GetBalance()
        {
            foreach (var h in Handles)
            {
                if (ChannelCount(h) < 2) continue;
                float l, r;
                try
                {
                    if (h.Channels.GetChannelVolume(0, out l) != 0 || h.Channels.GetChannelVolume(1, out r) != 0) continue;
                }
                catch { continue; }
                return ChannelMath.Balance(l, r);
            }
            return 0f;
        }

        public void SetBalance(float balance)
        {
            foreach (var h in Handles) ApplyBalance(h, balance);
        }

        // Channels 0/1 are front left/right; every channel also carries the session's software volume gain
        internal static void ApplyBalance(SessionHandle h, float balance)
        {
            SetChannels(h, ChannelMath.Levels(balance, h.Gain, ChannelCount(h)));
        }

        internal static float[] GetChannels(SessionHandle h)
        {
            var levels = new float[ChannelCount(h)];
            try
            {
                for (int i = 0; i < levels.Length; i++)
                    if (h.Channels.GetChannelVolume(i, out levels[i]) != 0) return null;
            }
            catch { return null; }
            return levels;
        }

        internal static void SetChannels(SessionHandle h, float[] levels)
        {
            try
            {
                for (int i = 0; i < levels.Length; i++)
                {
                    float cur;
                    if (h.Channels.GetChannelVolume(i, out cur) != 0 || Math.Abs(cur - levels[i]) > 0.0001f)
                        h.Channels.SetChannelVolume(i, levels[i], ref noContext);
                }
            }
            catch { }
        }

        internal static int ChannelCount(SessionHandle h)
        {
            int n;
            try { return h.Channels != null && h.Channels.GetChannelCount(out n) == 0 ? n : 0; }
            catch { return 0; }
        }
    }

    class AppAudioService
    {
        readonly AudioService audio;
        readonly Config config;
        readonly Dictionary<string, SessionHandle> handles = new Dictionary<string, SessionHandle>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> prepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // sessions whose channels were set up
        float softGain = 1f;

        // Output whose volume is also applied on this PC (to every session's channels), for devices that
        // accept but ignore volume changes. Lives only while MiniAudio runs; null = off.
        public string SoftwareDeviceId { get; private set; }
        readonly Dictionary<int, string> pathByPid = new Dictionary<int, string>();
        readonly Dictionary<string, string> nameByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Bitmap> iconByPath = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, IAudioSessionManager2> managers = new Dictionary<string, IAudioSessionManager2>(StringComparer.OrdinalIgnoreCase);

        public AppAudioService(AudioService audio, Config config)
        {
            this.audio = audio;
            this.config = config;
        }

        // All apps with an open (active or inactive) audio session, sorted by name; system sounds last.
        // Sessions seen for the first time get their saved balance, or their full level back when an earlier
        // software volume left them lowered (Windows remembers channel volumes per app).
        public List<AppSession> GetApps()
        {
            var groups = new Dictionary<string, AppSession>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dev in audio.GetDevices(EDataFlow.Render, DeviceState.Active))
            {
                IAudioSessionEnumerator en = Sessions(dev);
                int count;
                try { if (en == null || en.GetCount(out count) != 0) continue; }
                catch { continue; }
                bool soft = IsSoftware(dev.Id);

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 c;
                    int state, pid;
                    string inst;
                    try
                    {
                        if (en.GetSession(i, out c) != 0 || c == null) continue;
                        if (c.GetState(out state) != 0 || state == SessionState.Expired) continue;
                        if (c.GetSessionInstanceIdentifier(out inst) != 0 || inst == null) continue;
                        c.GetProcessId(out pid); // may return AUDCLNT_S_NO_SINGLE_PROCESS (success code)
                    }
                    catch { continue; }

                    bool system = false;
                    try { system = c.IsSystemSoundsSession() == 0; } catch { }
                    string path = system ? null : ProcessPath(pid);
                    if (!system && path == null) continue; // process already gone
                    string key = system ? "#system" : System.IO.Path.GetFileName(path).ToLowerInvariant();

                    string handleKey = dev.Id + "|" + inst;
                    seen.Add(handleKey);
                    SessionHandle h;
                    if (!handles.TryGetValue(handleKey, out h))
                    {
                        h = new SessionHandle();
                        h.InstanceId = handleKey;
                        h.Control = c;
                        h.Volume = c as ISimpleAudioVolume;
                        h.Channels = c as IChannelAudioVolume;
                        h.Meter = c as IAudioMeterInformation;
                        handles[handleKey] = h;
                    }
                    h.Pid = pid;
                    h.State = state;
                    h.Gain = soft ? softGain : 1f;

                    AppSession app;
                    if (!groups.TryGetValue(key, out app))
                    {
                        app = new AppSession();
                        app.Key = key;
                        app.IsSystem = system;
                        app.Name = system ? "System sounds" : AppName(path, c);
                        app.Icon = system ? null : AppIcon(path);
                        groups[key] = app;
                    }
                    app.Handles.Add(h);
                    if (!system && pid > 0 && !app.Pids.Contains(pid)) app.Pids.Add(pid);
                    if (state == SessionState.Active) app.Active = true;

                    if (!prepared.Contains(handleKey))
                    {
                        float bal;
                        if (config.Balances.TryGetValue(key, out bal)) AppSession.ApplyBalance(h, bal);
                        else if (!soft) RestoreFullLevel(h);
                        prepared.Add(handleKey);
                    }
                }
            }

            // forget sessions that disappeared
            var stale = new List<string>();
            foreach (var k in handles.Keys) if (!seen.Contains(k)) stale.Add(k);
            foreach (var k in stale) { handles.Remove(k); prepared.Remove(k); }

            var list = new List<AppSession>(groups.Values);
            foreach (var a in list)
                a.RouteId = a.Pids.Count > 0 ? AppRouting.GetRoute(a.Pids[0]) : null;
            list.Sort(delegate(AppSession a, AppSession b)
            {
                if (a.IsSystem != b.IsSystem) return a.IsSystem ? 1 : -1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        // Routes every process of the app; deviceId null = back to the default device
        public bool SetRoute(AppSession app, string deviceId)
        {
            bool ok = app.Pids.Count > 0;
            foreach (int pid in app.Pids) ok &= AppRouting.SetRoute(pid, deviceId);
            if (ok) app.RouteId = deviceId;
            return ok;
        }

        public void SetBalance(AppSession app, float balance)
        {
            if (Math.Abs(balance) < 0.005f) { balance = 0f; config.Balances.Remove(app.Key); }
            else config.Balances[app.Key] = balance;
            app.SetBalance(balance);
            config.Save();
        }

        // ---- software volume ----

        // Turns software volume on for one output, or off (null). Sessions of a device that leaves
        // software volume get their full level back.
        public void SetSoftwareVolume(AudioDevice dev)
        {
            string id = dev != null ? dev.Id : null;
            if (string.Equals(id, SoftwareDeviceId, StringComparison.OrdinalIgnoreCase)) return;
            string old = SoftwareDeviceId;
            SoftwareDeviceId = null;
            softGain = 1f;
            if (old != null) ApplyGain(old, 1f);
            if (dev == null) return;
            SoftwareDeviceId = id;
            ApplySoftwareVolume();
        }

        // Applies the device's current volume to every session on it; called about every 100 ms while on.
        // Returns false (and turns software volume off) when the device is gone.
        public bool ApplySoftwareVolume()
        {
            if (SoftwareDeviceId == null) return false;
            var dev = audio.GetDevice(SoftwareDeviceId);
            if (dev == null || !dev.IsActive)
            {
                // its sessions are gone; anything left lowered is restored when the apps show up again
                SoftwareDeviceId = null;
                softGain = 1f;
                return false;
            }
            float db = dev.GetVolumeDb();
            if (!float.IsNaN(db)) softGain = ChannelMath.GainFromDb(db);
            ApplyGain(SoftwareDeviceId, softGain);
            return true;
        }

        bool IsSoftware(string deviceId)
        {
            return SoftwareDeviceId != null && string.Equals(deviceId, SoftwareDeviceId, StringComparison.OrdinalIgnoreCase);
        }

        // Sets every live session on the device to its balance (read from its channels) times gain
        void ApplyGain(string deviceId, float gain)
        {
            foreach (var h in handles.Values)
                if (h.InstanceId.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase)) h.Gain = gain;

            var dev = audio.GetDevice(deviceId);
            IAudioSessionEnumerator en = dev != null && dev.IsActive ? Sessions(dev) : null;
            int count;
            try { if (en == null || en.GetCount(out count) != 0) return; }
            catch { return; }
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 c;
                int state;
                try
                {
                    if (en.GetSession(i, out c) != 0 || c == null) continue;
                    if (c.GetState(out state) != 0 || state == SessionState.Expired) continue;
                }
                catch { continue; }
                var h = new SessionHandle { Channels = c as IChannelAudioVolume, Gain = gain };
                var levels = AppSession.GetChannels(h);
                if (levels == null || levels.Length == 0) continue;
                var target = ChannelMath.Normalize(levels);
                for (int k = 0; k < target.Length; k++) target[k] *= gain;
                AppSession.SetChannels(h, target);
            }
        }

        // A session left lowered by an earlier software volume (e.g. MiniAudio quit unexpectedly) gets its
        // full level back; without software volume the loudest channel is always 1
        static void RestoreFullLevel(SessionHandle h)
        {
            var levels = AppSession.GetChannels(h);
            if (levels == null || levels.Length == 0) return;
            float max = 0f;
            foreach (float v in levels) max = Math.Max(max, v);
            if (max < 0.995f) AppSession.SetChannels(h, ChannelMath.Normalize(levels));
        }

        // Session list of a device. A cached session manager goes stale when the device is unplugged and
        // plugged back in; it is then activated again.
        IAudioSessionEnumerator Sessions(AudioDevice dev)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                IAudioSessionManager2 m;
                if (!managers.TryGetValue(dev.Id, out m) || m == null)
                {
                    m = dev.Activate(typeof(IAudioSessionManager2).GUID) as IAudioSessionManager2;
                    managers[dev.Id] = m;
                    if (m == null) return null;
                }
                IAudioSessionEnumerator en;
                try { if (m.GetSessionEnumerator(out en) == 0 && en != null) return en; }
                catch { }
                managers.Remove(dev.Id);
            }
            return null;
        }

        string ProcessPath(int pid)
        {
            if (pid <= 0) return null;
            string path;
            if (pathByPid.TryGetValue(pid, out path) && path != null) return path;
            path = Win32.ProcessImagePath(pid);
            if (pathByPid.Count > 512) pathByPid.Clear(); // pids are reused; keep the cache small
            pathByPid[pid] = path;
            return path;
        }

        string AppName(string path, IAudioSessionControl2 c)
        {
            string name;
            if (nameByPath.TryGetValue(path, out name)) return name;
            try { name = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
            if (string.IsNullOrEmpty(name))
            {
                // session display names are often resource references ("@%SystemRoot%\...,-123")
                string dn;
                try { if (c.GetDisplayName(out dn) == 0 && !string.IsNullOrEmpty(dn) && dn[0] != '@') name = dn; } catch { }
            }
            if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
            nameByPath[path] = name.Trim();
            return nameByPath[path];
        }

        Bitmap AppIcon(string path)
        {
            Bitmap bmp;
            if (iconByPath.TryGetValue(path, out bmp)) return bmp;
            try { using (var ic = Icon.ExtractAssociatedIcon(path)) bmp = ic != null ? ic.ToBitmap() : null; }
            catch { bmp = null; }
            iconByPath[path] = bmp;
            return bmp;
        }
    }

    // Per-app default output device
    static class AppRouting
    {
        const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
        const string FactoryIid = "ab3d4648-e242-459f-b02f-541c70306324";
        const string Prefix = @"\\?\SWD#MMDEVAPI#";
        const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        // vtable: IUnknown (3) + IInspectable (3) + 19 methods we do not use, then:
        const int SlotSet = 25, SlotGet = 26;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int SetFn(IntPtr self, int pid, int flow, int role, IntPtr deviceId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetFn(IntPtr self, int pid, int flow, int role, out IntPtr deviceId);

        static IntPtr factory;
        static bool tried;

        [DllImport("combase.dll")]
        static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll")]
        static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);
        [DllImport("combase.dll")]
        static extern int WindowsDeleteString(IntPtr hstring);
        [DllImport("combase.dll")]
        static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);

        public static bool Available { get { return Factory != IntPtr.Zero; } }

        static IntPtr Factory
        {
            get
            {
                if (tried) return factory;
                tried = true;
                IntPtr cls;
                try
                {
                    if (WindowsCreateString(ClassName, ClassName.Length, out cls) != 0) return IntPtr.Zero;
                    try
                    {
                        Guid iid = new Guid(FactoryIid);
                        IntPtr f;
                        if (RoGetActivationFactory(cls, ref iid, out f) == 0) factory = f;
                    }
                    finally { WindowsDeleteString(cls); }
                }
                catch { factory = IntPtr.Zero; } // older Windows: per-app routing unavailable
                return factory;
            }
        }

        static T Slot<T>(int index) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(Factory);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
        }

        public static bool SetRoute(int pid, string deviceId)
        {
            if (!Available || pid <= 0) return false;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                {
                    string full = Prefix + deviceId + RenderSuffix;
                    if (WindowsCreateString(full, full.Length, out h) != 0) return false;
                }
                var set = Slot<SetFn>(SlotSet);
                bool ok = set(Factory, pid, (int)EDataFlow.Render, (int)ERole.Multimedia, h) == 0;
                ok &= set(Factory, pid, (int)EDataFlow.Render, (int)ERole.Console, h) == 0;
                return ok;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
        }

        // null when the app follows the default device
        public static string GetRoute(int pid)
        {
            if (!Available || pid <= 0) return null;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (Slot<GetFn>(SlotGet)(Factory, pid, (int)EDataFlow.Render, (int)ERole.Multimedia, out h) != 0 || h == IntPtr.Zero)
                    return null;
                int len;
                string s = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out len), len);
                if (s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(Prefix.Length);
                if (s.EndsWith(RenderSuffix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - RenderSuffix.Length);
                return s.Length > 0 ? s : null;
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
        }
    }
}
