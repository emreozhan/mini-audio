// Tray app: icon, menu, hotkey and auto switching rules.
//
// Auto switching (device events are coalesced for 700 ms, then evaluated):
//  - If a newly connected output is checked in the list, switch to it.
//  - If the device in use disconnected, switch to the first connected, checked device in priority order
//    (otherwise Windows often falls back to monitor audio).
//  - Anything else (a manual choice, a monitor waking up, ...) is left alone.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MiniAudio
{
    class HotkeyWindow : NativeWindow
    {
        public event Action Pressed;
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_HOTKEY && Pressed != null) Pressed();
            base.WndProc(ref m);
        }
    }

    class TrayApp : ApplicationContext
    {
        const int HotkeyId = 1;

        readonly Config config;
        readonly AudioService audio;
        readonly AppAudioService appAudio;
        readonly Control invoker;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Flyout flyout;
        readonly Osd osd;
        readonly HotkeyWindow hotkeyWin;
        readonly Timer debounce, poll, softTimer;
        readonly Dictionary<string, Icon> iconCache = new Dictionary<string, Icon>();
        SettingsForm settingsForm;
        string registeredHotkey;

        HashSet<string> knownActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string snapshotDefault;
        bool initialized;
        string trayKey, trayText;

        public TrayApp()
        {
            invoker = new Control();
            IntPtr forceHandle = invoker.Handle; // a handle is required to marshal COM events to the UI thread

            config = Config.Load();
            audio = new AudioService(invoker);
            audio.DevicesChanged += ScheduleEvaluate;

            osd = new Osd();
            appAudio = new AppAudioService(audio, config, true);
            flyout = new Flyout(audio, appAudio, config);
            flyout.SwitchRequested += delegate(AudioDevice d) { if (SwitchTo(d, null)) flyout.RefreshDevices(); };
            flyout.SettingsRequested += OpenSettings;
            flyout.AutoToggled += delegate { config.Auto = !config.Auto; config.Save(); };
            flyout.SoftwareVolumeToggled += delegate { ToggleSoftwareVolume(false); }; // the flyout shows the state itself

            menu = new ContextMenuStrip();
            menu.Opening += delegate { BuildMenu(); };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.MouseClick += OnTrayClick;

            hotkeyWin = new HotkeyWindow();
            hotkeyWin.Pressed += CycleNext;
            RegisterHotkey(false);

            debounce = new Timer();
            debounce.Interval = 700;
            debounce.Tick += delegate { debounce.Stop(); Evaluate(); };

            poll = new Timer(); // refresh icon/tooltip after volume keys etc.
            poll.Interval = 1000;
            poll.Tick += delegate { OnPoll(); };
            poll.Start();

            softTimer = new Timer(); // software volume follows the volume keys and catches new sessions
            softTimer.Interval = 100;
            softTimer.Tick += delegate { if (!appAudio.ApplySoftwareVolume()) SoftwareVolumeEnded("Software volume off · device disconnected"); };
            SystemEvents.SessionEnding += OnSessionEnding;

            bool firstRun = config.IsNew;
            Evaluate();
            tray.Visible = true;
            if (firstRun)
            {
                config.Save();
                tray.ShowBalloonTip(4000, "MiniAudio is running",
                    "Click the icon to manage devices and volume. Right-click: menu, middle-click: mute.",
                    ToolTipIcon.Info);
            }
        }

        int pollCount;

        void OnPoll()
        {
            UpdateTray();
            // Prepare the channels of apps that start playing: saved balance, or the full level back after an
            // earlier software volume (the flyout does this itself while open)
            if (++pollCount % 3 == 0 && !flyout.Visible) appAudio.GetApps();
        }

        // ---- software volume ----

        string softName, softGlyph; // for the notice when software volume ends on its own

        // Turns software volume on for the default output, or off again (wherever it is on)
        void ToggleSoftwareVolume(bool notice)
        {
            bool on = appAudio.SoftwareDeviceId == null;
            if (on)
            {
                var d = audio.GetDefault(EDataFlow.Render);
                if (d == null) return;
                appAudio.SetSoftwareVolume(d);
                softName = d.Name;
                softGlyph = Glyphs.ForDevice(d);
                softTimer.Start();
            }
            else StopSoftwareVolume();
            if (notice && config.ShowOsd) osd.ShowMessage(softGlyph, softName, on ? "Software volume on" : "Software volume off");
            UpdateTray();
            flyout.Invalidate();
        }

        void SoftwareVolumeEnded(string reason)
        {
            softTimer.Stop();
            if (config.ShowOsd && softName != null) osd.ShowMessage(softGlyph, softName, reason);
            UpdateTray();
            flyout.RefreshDevices();
        }

        // Windows is shutting down or signing out: give apps their full level back before MiniAudio is closed
        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            if (invoker.InvokeRequired) { invoker.Invoke((Action)StopSoftwareVolume); return; }
            StopSoftwareVolume();
        }

        void StopSoftwareVolume()
        {
            softTimer.Stop();
            appAudio.SetSoftwareVolume(null);
        }

        // ---- auto switching ----

        void ScheduleEvaluate()
        {
            debounce.Stop();
            debounce.Start();
        }

        void Evaluate()
        {
            var outs = audio.GetDevices(EDataFlow.Render, DeviceState.Active);
            string cur = audio.GetDefaultId(EDataFlow.Render, ERole.Console);
            if (config.EnsureKnown(outs, cur)) config.Save();

            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in outs) active.Add(d.Id);

            AudioDevice target = null;
            string reason = null;
            if (initialized && config.Auto)
            {
                if (config.AutoConnect)
                {
                    foreach (var d in outs)
                        if (!knownActive.Contains(d.Id) && config.IsIncluded(d.Id)
                            && (target == null || config.Rank(d.Id) < config.Rank(target.Id)))
                            target = d;
                    if (target != null) reason = "Connected · now the default output";
                }
                if (target == null && config.AutoFallback && snapshotDefault != null && !active.Contains(snapshotDefault))
                {
                    target = BestIncluded(outs);
                    if (target != null) reason = "Previous device disconnected · switched back";
                }
            }
            knownActive = active;
            initialized = true;

            if (target != null && !string.Equals(target.Id, cur, StringComparison.OrdinalIgnoreCase))
            {
                if (SwitchTo(target, reason)) cur = target.Id;
            }
            snapshotDefault = cur;
            flyout.RefreshDevices();
            UpdateTray();
        }

        AudioDevice BestIncluded(List<AudioDevice> outs)
        {
            AudioDevice best = null;
            foreach (var d in outs)
                if (config.IsIncluded(d.Id) && (best == null || config.Rank(d.Id) < config.Rank(best.Id)))
                    best = d;
            return best;
        }

        // shows an on-screen notice when reason is not null
        bool SwitchTo(AudioDevice d, string reason)
        {
            if (!Switcher.Apply(audio, config, d))
            {
                tray.ShowBalloonTip(3000, "MiniAudio", d.Name + " could not be set as default.", ToolTipIcon.Warning);
                return false;
            }
            if (d.Flow == EDataFlow.Render) snapshotDefault = d.Id;
            if (reason != null && config.ShowOsd) osd.ShowMessage(Glyphs.ForDevice(d), d.Name, reason);
            UpdateTray();
            return true;
        }

        void CycleNext()
        {
            var outs = audio.GetDevices(EDataFlow.Render, DeviceState.Active);
            var list = new List<AudioDevice>();
            foreach (var d in outs) if (config.IsIncluded(d.Id)) list.Add(d);
            if (list.Count == 0) list = outs;
            if (list.Count == 0) return;
            list.Sort(delegate(AudioDevice a, AudioDevice b) { return config.Rank(a.Id).CompareTo(config.Rank(b.Id)); });

            string cur = audio.GetDefaultId(EDataFlow.Render, ERole.Console);
            int i = list.FindIndex(delegate(AudioDevice d) { return string.Equals(d.Id, cur, StringComparison.OrdinalIgnoreCase); });
            var next = list[(i + 1) % list.Count];
            if (next.Id == cur) { if (config.ShowOsd) osd.ShowMessage(Glyphs.ForDevice(next), next.Name, "No other device to switch to"); return; }
            SwitchTo(next, "Default output · " + (i + 2 > list.Count ? 1 : i + 2) + "/" + list.Count);
        }

        // ---- tray ----

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Clicking the icon while open first deactivates and hides the flyout; don't reopen it right away
                if (Environment.TickCount - flyout.LastHiddenTick < 300) return;
                flyout.ShowNear(Cursor.Position);
            }
            else if (e.Button == MouseButtons.Middle)
            {
                var d = audio.GetDefault(EDataFlow.Render);
                if (d != null) { d.SetMute(!d.GetMute()); UpdateTray(); }
            }
        }

        void UpdateTray()
        {
            var d = audio.GetDefault(EDataFlow.Render);
            float vol = d != null ? d.GetVolume() : -1f;
            bool muted = d != null && d.GetMute();
            bool light = Theme.TaskbarLight();
            string softId = appAudio.SoftwareDeviceId;
            bool soft = softId != null;
            string glyph = muted ? Glyphs.Mute : Glyphs.ForDevice(d);

            // the accent color is part of the key: it can change while MiniAudio runs
            string key = glyph + (light ? "L" : "D") + (soft ? Theme.AccentOn(light).ToArgb().ToString("X8") : "");
            if (key != trayKey)
            {
                trayKey = key;
                tray.Icon = GetIcon(key, glyph, light, soft);
            }

            bool softElsewhere = soft && (d == null || !string.Equals(d.Id, softId, StringComparison.OrdinalIgnoreCase));
            string text = TrayLook.Text(d != null ? d.Name : null, vol, muted, soft, softElsewhere ? softName : null);
            if (text != trayText) { trayText = text; tray.Text = text; }
        }

        Icon GetIcon(string key, string glyph, bool light, bool soft)
        {
            Icon cached;
            if (iconCache.TryGetValue(key, out cached)) return cached;

            Icon result;
            using (var bmp = TrayLook.Icon(glyph, light, soft, SystemInformation.SmallIconSize.Width))
            {
                IntPtr h = bmp.GetHicon();
                try { using (var tmp = Icon.FromHandle(h)) result = (Icon)tmp.Clone(); }
                finally { Win32.DestroyIcon(h); }
            }
            iconCache[key] = result;
            return result;
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            var head = new ToolStripMenuItem("Output device");
            head.Enabled = false;
            menu.Items.Add(head);

            var outs = audio.GetDevices(EDataFlow.Render, DeviceState.Active);
            outs.Sort(delegate(AudioDevice a, AudioDevice b) { return config.Rank(a.Id).CompareTo(config.Rank(b.Id)); });
            string cur = audio.GetDefaultId(EDataFlow.Render, ERole.Console);
            foreach (var d in outs)
            {
                var it = new ToolStripMenuItem(d.Name);
                it.Checked = string.Equals(d.Id, cur, StringComparison.OrdinalIgnoreCase);
                AudioDevice dev = d;
                it.Click += delegate { SwitchTo(dev, null); };
                menu.Items.Add(it);
            }
            var soft = new ToolStripMenuItem("Software volume");
            soft.ToolTipText = "Also lowers the sound on this PC, for a device that ignores volume changes. Ends when MiniAudio closes.";
            soft.Checked = appAudio.SoftwareDeviceId != null;
            soft.Enabled = soft.Checked || cur != null;
            soft.Click += delegate { ToggleSoftwareVolume(true); };
            menu.Items.Add(soft);

            menu.Items.Add(new ToolStripSeparator());
            var auto = new ToolStripMenuItem("Auto switch");
            auto.Checked = config.Auto;
            auto.Click += delegate { config.Auto = !config.Auto; config.Save(); };
            menu.Items.Add(auto);
            var hk = config.HotkeyPreset;
            var next = new ToolStripMenuItem("Next device");
            if (hk.Vk != 0) next.ShortcutKeyDisplayString = hk.Label.Replace(" ", "");
            next.Click += delegate { CycleNext(); };
            menu.Items.Add(next);
            var settings = new ToolStripMenuItem("Settings…");
            settings.Click += delegate { OpenSettings(); };
            menu.Items.Add(settings);

            menu.Items.Add(new ToolStripSeparator());
            var winSettings = new ToolStripMenuItem("Windows sound settings");
            winSettings.Click += delegate { Launch("ms-settings:sound", null); };
            menu.Items.Add(winSettings);
            var panel = new ToolStripMenuItem("Sound control panel");
            panel.Click += delegate { Launch("control.exe", "mmsys.cpl"); };
            menu.Items.Add(panel);

            menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);
        }

        static void Launch(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file);
                if (args != null) psi.Arguments = args;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        void OpenSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(config, audio.GetDevices(EDataFlow.Render, DeviceState.Active));
            settingsForm.Changed += delegate { RegisterHotkey(true); };
            settingsForm.FormClosed += delegate { settingsForm = null; };
            settingsForm.Show();
            settingsForm.Activate();
        }

        // ---- hotkey ----

        void RegisterHotkey(bool showError)
        {
            var p = config.HotkeyPreset;
            if (p.Id == registeredHotkey) return;
            Win32.UnregisterHotKey(hotkeyWin.Handle, HotkeyId);
            registeredHotkey = p.Id;
            if (p.Vk == 0) return;
            bool ok = Win32.RegisterHotKey(hotkeyWin.Handle, HotkeyId, p.Mods | Win32.MOD_NOREPEAT, p.Vk);
            if (!ok && showError)
                tray.ShowBalloonTip(3000, "MiniAudio", p.Label + " could not be registered; another app may be using it.", ToolTipIcon.Warning);
        }

        // ---- exit ----

        void ExitApp()
        {
            StopSoftwareVolume(); // apps get their full level back
            SystemEvents.SessionEnding -= OnSessionEnding;
            poll.Stop();
            debounce.Stop();
            Win32.UnregisterHotKey(hotkeyWin.Handle, HotkeyId);
            hotkeyWin.DestroyHandle();
            audio.Dispose();
            tray.Visible = false;
            tray.Dispose();
            flyout.Dispose();
            osd.Dispose();
            foreach (var ic in iconCache.Values) ic.Dispose();
            invoker.Dispose();
            ExitThread();
        }
    }

    // What the tray icon tells without a click: the default device, its volume, and whether software volume is on
    static class TrayLook
    {
        // Device glyph in the taskbar's text color, or in the accent color while software volume is on
        public static Bitmap Icon(string glyph, bool lightTaskbar, bool softwareVolume, int size)
        {
            Color color = softwareVolume ? Theme.AccentOn(lightTaskbar)
                : lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var f = Glyphs.Font(size * 0.82f))
            using (var b = new SolidBrush(color))
            using (var sf = new StringFormat())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, f, b, new RectangleF(0, 0, size, size), sf);
            }
            return bmp;
        }

        // softwareOther: the device software volume is on for, when that is not the default device
        public static string Text(string device, float volume, bool muted, bool softwareVolume, string softwareOther)
        {
            if (device == null) return "MiniAudio — no output device";
            string text = device + (muted ? " — muted" : volume >= 0 ? " — " + (int)Math.Round(volume * 100) + "%" : "");
            if (softwareVolume) text += " · Software volume" + (softwareOther != null ? ": " + softwareOther : "");
            return text.Length > 63 ? text.Substring(0, 62) + "…" : text; // the text of a tray icon is limited to 63 characters
        }
    }

    // Shared by the tray app and the command line
    static class Switcher
    {
        public static bool Apply(AudioService audio, Config config, AudioDevice d)
        {
            if (!audio.SetDefault(d.Id, config.SwitchComms)) return false;
            if (d.Flow == EDataFlow.Render && config.MicFollow)
            {
                var mic = MatchingMic(audio, d);
                if (mic != null) audio.SetDefault(mic.Id, config.SwitchComms);
            }
            return true;
        }

        // Microphone of the same physical device: "Soundcore Motion B" → "Soundcore Motion B Hands-Free"
        public static AudioDevice MatchingMic(AudioService audio, AudioDevice output)
        {
            string name = output.InterfaceName;
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var c in audio.GetDevices(EDataFlow.Capture, DeviceState.Active))
            {
                string cn = c.InterfaceName ?? "";
                if (string.Equals(cn, name, StringComparison.OrdinalIgnoreCase)
                    || cn.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }
    }
}
