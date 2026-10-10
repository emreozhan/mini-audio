// Settings: %APPDATA%\MiniAudio\config.txt, one key=value per line.
// Output devices are stored in priority order as "out=<id>|<1/0 included>|<name>" lines.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MiniAudio
{
    class DeviceEntry
    {
        public string Id;
        public string Name;
        public bool Included; // takes part in auto switching and the hotkey cycle
    }

    class HotkeyPreset
    {
        public string Id, Label;
        public uint Mods, Vk;
        public HotkeyPreset(string id, string label, uint mods, uint vk) { Id = id; Label = label; Mods = mods; Vk = vk; }
    }

    class Config
    {
        public static readonly HotkeyPreset[] Hotkeys = new HotkeyPreset[]
        {
            new HotkeyPreset("none",           "None",               0, 0),
            new HotkeyPreset("ctrl+alt+s",     "Ctrl + Alt + S",     Win32.MOD_CONTROL | Win32.MOD_ALT,   0x53),
            new HotkeyPreset("ctrl+alt+a",     "Ctrl + Alt + A",     Win32.MOD_CONTROL | Win32.MOD_ALT,   0x41),
            new HotkeyPreset("ctrl+alt+f12",   "Ctrl + Alt + F12",   Win32.MOD_CONTROL | Win32.MOD_ALT,   0x7B),
            new HotkeyPreset("ctrl+shift+f12", "Ctrl + Shift + F12", Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x7B),
            new HotkeyPreset("win+alt+s",      "Win + Alt + S",      Win32.MOD_WIN | Win32.MOD_ALT,       0x53),
        };

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "MiniAudio";

        public bool Auto = true;          // auto switch master toggle
        public bool AutoConnect = true;   // switch to a newly connected device
        public bool AutoFallback = true;  // fall back to the top-priority device when the current one disconnects
        public bool SwitchComms = true;   // also change the communications (call) device
        public bool MicFollow = false;    // switch the matching microphone with the output
        public bool ShowMics = true;      // show microphones in the flyout
        public bool ShowOsd = true;       // on-screen notice on switch
        public string Hotkey = "ctrl+alt+s";
        public List<DeviceEntry> Outputs = new List<DeviceEntry>();
        public Dictionary<string, float> Balances = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); // app exe -> -1..1
        public bool IsNew = true;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniAudio"); } }
        static string FilePath { get { return Path.Combine(Dir, "config.txt"); } }

        public HotkeyPreset HotkeyPreset
        {
            get
            {
                foreach (var h in Hotkeys) if (h.Id == Hotkey) return h;
                return Hotkeys[0];
            }
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                c.IsNew = false;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "auto": c.Auto = v == "1"; break;
                        case "autoConnect": c.AutoConnect = v == "1"; break;
                        case "autoFallback": c.AutoFallback = v == "1"; break;
                        case "switchComms": c.SwitchComms = v == "1"; break;
                        case "micFollow": c.MicFollow = v == "1"; break;
                        case "showMics": c.ShowMics = v == "1"; break;
                        case "showOsd": c.ShowOsd = v == "1"; break;
                        case "hotkey": c.Hotkey = v; break;
                        case "bal":
                            int sep = v.LastIndexOf('|');
                            float b;
                            if (sep > 0 && float.TryParse(v.Substring(sep + 1), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out b))
                                c.Balances[v.Substring(0, sep)] = Math.Max(-1f, Math.Min(1f, b));
                            break;
                        case "out":
                            string[] p = v.Split(new[] { '|' }, 3);
                            if (p.Length == 3 && p[0].Length > 0 && c.Find(p[0]) == null)
                                c.Outputs.Add(new DeviceEntry { Id = p[0], Included = p[1] == "1", Name = p[2] });
                            break;
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            var lines = new List<string>();
            lines.Add("auto=" + B(Auto));
            lines.Add("autoConnect=" + B(AutoConnect));
            lines.Add("autoFallback=" + B(AutoFallback));
            lines.Add("switchComms=" + B(SwitchComms));
            lines.Add("micFollow=" + B(MicFollow));
            lines.Add("showMics=" + B(ShowMics));
            lines.Add("showOsd=" + B(ShowOsd));
            lines.Add("hotkey=" + Hotkey);
            foreach (var e in Outputs)
                lines.Add("out=" + e.Id + "|" + B(e.Included) + "|" + (e.Name ?? "").Replace("\r", " ").Replace("\n", " "));
            foreach (var kv in Balances)
                lines.Add("bal=" + kv.Key + "|" + kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, lines.ToArray());
                IsNew = false;
            }
            catch { }
        }

        static string B(bool b) { return b ? "1" : "0"; }

        public DeviceEntry Find(string id)
        {
            foreach (var e in Outputs) if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public int Rank(string id)
        {
            for (int i = 0; i < Outputs.Count; i++)
                if (string.Equals(Outputs[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        public bool IsIncluded(string id)
        {
            var e = Find(id);
            return e != null && e.Included;
        }

        // Appends newly seen devices to the priority list and refreshes names. Returns true if anything changed.
        // Monitor/HDMI audio is excluded by default: monitors connect and disconnect as they sleep and wake.
        // Bluetooth "Hands-Free" outputs are excluded too: the same device's low-quality call profile.
        public bool EnsureKnown(List<AudioDevice> outputs, string defaultId)
        {
            bool changed = false;
            var fresh = new List<AudioDevice>();
            foreach (var d in outputs)
            {
                var e = Find(d.Id);
                if (e == null) fresh.Add(d);
                else if (e.Name != d.Name) { e.Name = d.Name; changed = true; }
            }
            if (fresh.Count == 0) return changed;

            // Sensible initial order: current default, then external (USB/Bluetooth), then the rest
            fresh.Sort(delegate(AudioDevice a, AudioDevice b) { return Score(a, defaultId).CompareTo(Score(b, defaultId)); });
            foreach (var d in fresh)
                Outputs.Add(new DeviceEntry { Id = d.Id, Name = d.Name, Included = d.FormFactor != FormFactor.DisplayAudio && !d.HandsFree });
            return true;
        }

        static int Score(AudioDevice d, string defaultId)
        {
            if (string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase)) return 0;
            if (d.Bus.Length > 0 && !d.HandsFree) return 1;
            if (d.FormFactor == FormFactor.DisplayAudio || d.HandsFree) return 3;
            return 2;
        }

        public static bool IsAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }
    }
}
