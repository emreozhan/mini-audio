// Enumerates audio devices, reads/changes the default device, handles volume and mute.
// Device events arrive on COM threads and are re-raised on the UI thread.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MiniAudio
{
    class AudioDevice
    {
        public string Id;
        public EDataFlow Flow;
        public int State;
        public string Name;          // short name: "Arctis Nova 7 Gen 2", "DELL S2721DGF"
        public string Detail;        // second line: "Headphones · USB"
        public string FriendlyName;  // full name as Windows shows it
        public string InterfaceName; // physical device it belongs to (used for mic matching)
        public string Bus;           // "Bluetooth", "USB" or ""
        public int FormFactor;
        public bool HandsFree;       // Bluetooth call profile (HFP): mono, low quality

        internal IMMDevice Com;
        IAudioEndpointVolume volume;
        IAudioMeterInformation meter;
        bool volumeFailed, meterFailed;

        public bool IsActive { get { return State == DeviceState.Active; } }

        static Guid noContext = Guid.Empty;

        IAudioEndpointVolume Volume
        {
            get
            {
                if (volume == null && !volumeFailed && IsActive)
                {
                    object o = Activate(typeof(IAudioEndpointVolume).GUID);
                    volume = o as IAudioEndpointVolume;
                    volumeFailed = volume == null;
                }
                return volume;
            }
        }

        IAudioMeterInformation Meter
        {
            get
            {
                if (meter == null && !meterFailed && IsActive)
                {
                    object o = Activate(typeof(IAudioMeterInformation).GUID);
                    meter = o as IAudioMeterInformation;
                    meterFailed = meter == null;
                }
                return meter;
            }
        }

        object Activate(Guid iid)
        {
            try
            {
                object o;
                return Com.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) == 0 ? o : null;
            }
            catch { return null; }
        }

        internal void ResetHandles()
        {
            volume = null; meter = null;
            volumeFailed = false; meterFailed = false;
        }

        // 0..1; -1 if unreadable
        public float GetVolume()
        {
            var v = Volume;
            float level;
            try { if (v != null && v.GetMasterVolumeLevelScalar(out level) == 0) return level; }
            catch { }
            return -1f;
        }

        public void SetVolume(float level)
        {
            var v = Volume;
            if (v == null) return;
            level = Math.Max(0f, Math.Min(1f, level));
            try { v.SetMasterVolumeLevelScalar(level, ref noContext); } catch { }
        }

        public bool GetMute()
        {
            var v = Volume;
            bool m;
            try { if (v != null && v.GetMute(out m) == 0) return m; }
            catch { }
            return false;
        }

        public void SetMute(bool mute)
        {
            var v = Volume;
            if (v == null) return;
            try { v.SetMute(mute, ref noContext); } catch { }
        }

        public float GetPeak()
        {
            var m = Meter;
            float p;
            try { if (m != null && m.GetPeakValue(out p) == 0) return p; }
            catch { }
            return 0f;
        }
    }

    class AudioService : IDisposable
    {
        readonly IMMDeviceEnumerator enumerator;
        readonly NotificationClient client;
        readonly Dictionary<string, AudioDevice> cache = new Dictionary<string, AudioDevice>(StringComparer.OrdinalIgnoreCase);
        bool registered;

        // A device was added/removed/changed state, or the default changed (raised on the UI thread)
        public event Action DevicesChanged;

        public AudioService(Control invoker)
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (invoker != null)
            {
                client = new NotificationClient(invoker, delegate { if (DevicesChanged != null) DevicesChanged(); });
                registered = enumerator.RegisterEndpointNotificationCallback(client) == 0;
            }
        }

        public List<AudioDevice> GetDevices(EDataFlow flow, int stateMask)
        {
            var list = new List<AudioDevice>();
            IMMDeviceCollection col;
            if (enumerator.EnumAudioEndpoints(flow, stateMask, out col) != 0 || col == null) return list;
            int count;
            col.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IMMDevice dev;
                if (col.Item(i, out dev) != 0 || dev == null) continue;
                var d = Wrap(dev, flow);
                if (d != null) list.Add(d);
            }
            return list;
        }

        public AudioDevice GetDevice(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            IMMDevice dev;
            if (enumerator.GetDevice(id, out dev) != 0 || dev == null) return null;
            // Data flow comes from the id prefix: {0.0.0...} render / {0.0.1...} capture
            EDataFlow flow = id.StartsWith("{0.0.1.", StringComparison.Ordinal) ? EDataFlow.Capture : EDataFlow.Render;
            return Wrap(dev, flow);
        }

        public string GetDefaultId(EDataFlow flow, ERole role)
        {
            IMMDevice dev;
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out dev) != 0 || dev == null) return null;
            string id;
            return dev.GetId(out id) == 0 ? id : null;
        }

        public AudioDevice GetDefault(EDataFlow flow)
        {
            return GetDevice(GetDefaultId(flow, ERole.Console));
        }

        // Sets the default device; when includeComms is false the communications device is left alone
        public bool SetDefault(string id, bool includeComms)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigClientCom();
                bool ok = pc.SetDefaultEndpoint(id, ERole.Console) == 0;
                ok &= pc.SetDefaultEndpoint(id, ERole.Multimedia) == 0;
                if (includeComms) ok &= pc.SetDefaultEndpoint(id, ERole.Communications) == 0;
                Marshal.ReleaseComObject(pc);
                return ok;
            }
            catch { return false; }
        }

        AudioDevice Wrap(IMMDevice dev, EDataFlow flow)
        {
            string id;
            if (dev.GetId(out id) != 0 || id == null) return null;
            int state;
            dev.GetState(out state);

            AudioDevice d;
            if (cache.TryGetValue(id, out d))
            {
                if (d.State != state) d.ResetHandles(); // interfaces from before a reconnect are stale
                d.State = state;
                d.Com = dev;
                return d;
            }

            d = new AudioDevice();
            d.Id = id;
            d.Flow = flow;
            d.State = state;
            d.Com = dev;
            ReadProperties(d);
            cache[id] = d;
            return d;
        }

        static void ReadProperties(AudioDevice d)
        {
            IPropertyStore ps;
            if (d.Com.OpenPropertyStore(0 /* STGM_READ */, out ps) != 0 || ps == null) { d.Name = d.Id; d.Detail = ""; return; }

            d.FriendlyName = GetString(ps, PropKeys.FriendlyName) ?? d.Id;
            string desc = GetString(ps, PropKeys.DeviceDesc) ?? "";
            d.InterfaceName = GetString(ps, PropKeys.InterfaceName) ?? "";
            d.FormFactor = GetUInt(ps, PropKeys.FormFactor);
            string parent = (GetString(ps, PropKeys.ParentInstance) ?? "").ToUpperInvariant();
            Marshal.ReleaseComObject(ps);

            if (parent.Contains("BTHENUM") || parent.Contains("BTHHFENUM") || parent.Contains("BTHLE")) d.Bus = "Bluetooth";
            else if (parent.Contains("USB\\")) d.Bus = "USB";
            else d.Bus = "";
            d.HandsFree = parent.Contains("BTHHFENUM");

            // For external devices (USB/Bluetooth) the product name is meaningful: "Arctis Nova 7 Gen 2".
            // Elsewhere the interface name is generic ("NVIDIA High Definition Audio"), so the endpoint name leads.
            if (d.Bus.Length > 0 && d.InterfaceName.Length > 0)
            {
                d.Name = d.InterfaceName;
                d.Detail = desc + " · " + d.Bus;
            }
            else
            {
                d.Name = desc.Length > 0 ? desc : d.FriendlyName;
                d.Detail = d.InterfaceName;
            }
        }

        static string GetString(IPropertyStore ps, PropertyKey key)
        {
            PropVariant pv;
            if (ps.GetValue(ref key, out pv) != 0) return null;
            try { return pv.VarType == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.Pointer) : null; }
            finally { Win32.PropVariantClear(ref pv); }
        }

        static int GetUInt(IPropertyStore ps, PropertyKey key)
        {
            PropVariant pv;
            if (ps.GetValue(ref key, out pv) != 0) return -1;
            try { return pv.VarType == 19 /* VT_UI4 */ ? (int)pv.UInt : -1; }
            finally { Win32.PropVariantClear(ref pv); }
        }

        public void Dispose()
        {
            if (registered)
            {
                try { enumerator.UnregisterEndpointNotificationCallback(client); } catch { }
                registered = false;
            }
        }

        // Forwards COM events to the UI thread. Calling the audio API inside the callback is discouraged,
        // so only a notification is posted; the real work happens on the UI side.
        class NotificationClient : IMMNotificationClient
        {
            readonly Control invoker;
            readonly Action handler;

            public NotificationClient(Control invoker, Action handler) { this.invoker = invoker; this.handler = handler; }

            void Post()
            {
                try { if (!invoker.IsDisposed) invoker.BeginInvoke(handler); } catch { }
            }

            public void OnDeviceStateChanged(string deviceId, int newState) { Post(); }
            public void OnDeviceAdded(string deviceId) { Post(); }
            public void OnDeviceRemoved(string deviceId) { Post(); }
            public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId) { Post(); }
            public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
        }
    }
}
