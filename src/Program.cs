// MiniAudio — switch between audio outputs and manage their volume (system tray app)
// Build: see README (uses the .NET Framework 4.x compiler that ships with Windows, nothing to install)
//
// Command line (works while the tray app is running, e.g. from a hotkey tool or Stream Deck):
//   MiniAudio.exe --list               list devices
//   MiniAudio.exe --switch <name|id>   make the device whose name contains <name> (or whose id matches) the default
//   MiniAudio.exe --next               switch to the next output in the list

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("MiniAudio")]
[assembly: System.Reflection.AssemblyProduct("MiniAudio")]
[assembly: System.Reflection.AssemblyDescription("Switch between audio outputs and manage their volume")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace MiniAudio
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0) return Cli(args);

            bool created;
            using (var mutex = new Mutex(true, "MiniAudio_SingleInstance", out created))
            {
                if (!created) return 0; // already running
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }

        static int Cli(string[] args)
        {
            Win32.AttachConsole(-1); // print to the console when started from a command prompt
            Console.WriteLine();
            var audio = new AudioService(null);
            var config = Config.Load();
            string cmd = args[0].ToLowerInvariant();

            if (cmd == "--list")
            {
                foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                {
                    Console.WriteLine(flow == EDataFlow.Render ? "Output:" : "Input:");
                    string def = audio.GetDefaultId(flow, ERole.Console);
                    string comm = audio.GetDefaultId(flow, ERole.Communications);
                    foreach (var d in audio.GetDevices(flow, DeviceState.Active))
                    {
                        string mark = (d.Id == def ? "*" : " ") + (d.Id == comm ? "c" : " ");
                        float v = d.GetVolume();
                        Console.WriteLine("  {0} {1,-30} {2,-34} {3,4}  {4}", mark, d.Name, d.Detail,
                            v < 0 ? "-" : ((int)Math.Round(v * 100)) + "%", d.Id);
                    }
                }
                Console.WriteLine("  (* default, c communications)");
                return 0;
            }

            if (cmd == "--switch" && args.Length > 1)
            {
                string q = string.Join(" ", args, 1, args.Length - 1);
                AudioDevice match = null;
                foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                    foreach (var d in audio.GetDevices(flow, DeviceState.Active))
                        if (match == null && (string.Equals(d.Id, q, StringComparison.OrdinalIgnoreCase)
                            || d.FriendlyName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                            || d.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0))
                            match = d;
                if (match == null) { Console.WriteLine("No matching active device: " + q); return 1; }
                bool ok = Switcher.Apply(audio, config, match);
                Console.WriteLine((ok ? "Default set: " : "Failed: ") + match.FriendlyName);
                return ok ? 0 : 1;
            }

            if (cmd == "--next")
            {
                var list = new List<AudioDevice>();
                var outs = audio.GetDevices(EDataFlow.Render, DeviceState.Active);
                foreach (var d in outs) if (config.IsIncluded(d.Id)) list.Add(d);
                if (list.Count == 0) list = outs;
                if (list.Count == 0) { Console.WriteLine("No active output device."); return 1; }
                list.Sort(delegate(AudioDevice a, AudioDevice b) { return config.Rank(a.Id).CompareTo(config.Rank(b.Id)); });
                string cur = audio.GetDefaultId(EDataFlow.Render, ERole.Console);
                int i = list.FindIndex(delegate(AudioDevice d) { return d.Id == cur; });
                var next = list[(i + 1) % list.Count];
                bool ok = Switcher.Apply(audio, config, next);
                Console.WriteLine((ok ? "Default set: " : "Failed: ") + next.FriendlyName);
                return ok ? 0 : 1;
            }

            if (cmd == "--apps")
            {
                var apps = new AppAudioService(audio, config);
                if (!AppRouting.Available) Console.WriteLine("(per-app output routing is not available on this Windows version)");
                foreach (var a in apps.GetApps())
                {
                    var routed = a.RouteId != null ? audio.GetDevice(a.RouteId) : null;
                    float v = a.GetVolume();
                    Console.WriteLine("  {0} {1,-28} {2,4}  bal {3,5:0.00}  -> {4}  [{5}]", a.Active ? ">" : " ", a.Name,
                        v < 0 ? "-" : ((int)Math.Round(v * 100)) + "%", a.GetBalance(),
                        a.IsSystem ? "-" : routed != null ? routed.Name : "Default", a.Key);
                }
                Console.WriteLine("  (> playing now)");
                return 0;
            }

            if (cmd == "--route" && args.Length > 2)
            {
                // --route <app> <device name|id|default>
                var apps = new AppAudioService(audio, config);
                AppSession app = null;
                foreach (var a in apps.GetApps())
                    if (app == null && !a.IsSystem && (a.Key.IndexOf(args[1], StringComparison.OrdinalIgnoreCase) >= 0
                        || a.Name.IndexOf(args[1], StringComparison.CurrentCultureIgnoreCase) >= 0))
                        app = a;
                if (app == null) { Console.WriteLine("No app with an audio session matches: " + args[1]); return 1; }
                string q = string.Join(" ", args, 2, args.Length - 2);
                AudioDevice target = null;
                if (!string.Equals(q, "default", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var d in audio.GetDevices(EDataFlow.Render, DeviceState.Active))
                        if (target == null && (string.Equals(d.Id, q, StringComparison.OrdinalIgnoreCase)
                            || d.FriendlyName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                            || d.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0))
                            target = d;
                    if (target == null) { Console.WriteLine("No matching active output: " + q); return 1; }
                }
                bool ok = apps.SetRoute(app, target != null ? target.Id : null);
                Console.WriteLine((ok ? "Routed: " : "Failed: ") + app.Name + " -> " + (target != null ? target.Name : "Default"));
                return ok ? 0 : 1;
            }

            if (cmd == "--balance" && args.Length > 2)
            {
                // --balance <app> <-100..100>  (negative = left, positive = right, 0 = center)
                var apps = new AppAudioService(audio, config);
                AppSession app = null;
                foreach (var a in apps.GetApps())
                    if (app == null && (a.Key.IndexOf(args[1], StringComparison.OrdinalIgnoreCase) >= 0
                        || a.Name.IndexOf(args[1], StringComparison.CurrentCultureIgnoreCase) >= 0))
                        app = a;
                int pct;
                if (app == null || !int.TryParse(args[2], out pct)) { Console.WriteLine("Usage: --balance <app> <-100..100>"); return 1; }
                if (!app.HasBalance) { Console.WriteLine(app.Name + " has no stereo channels to balance."); return 1; }
                apps.SetBalance(app, Math.Max(-100, Math.Min(100, pct)) / 100f);
                Console.WriteLine("Balance: " + app.Name + " = " + app.GetBalance().ToString("0.00"));
                return 0;
            }

            if (cmd == "--snapshot" && args.Length > 1)
            {
                // Renders the UI to PNG (development check); does not write settings
                string dir = args[1];
                System.IO.Directory.CreateDirectory(dir);
                Application.EnableVisualStyles();
                foreach (bool dark in new[] { true, false })
                {
                    Theme.ForceDark = dark;
                    string sfx = dark ? "-dark.png" : "-light.png";
                    foreach (FlyoutTab t in new[] { FlyoutTab.Devices, FlyoutTab.Apps })
                        using (var f = new Flyout(audio, new AppAudioService(audio, config), config))
                        using (var b = f.Snapshot(t)) b.Save(System.IO.Path.Combine(dir, "flyout-" + t.ToString().ToLowerInvariant() + sfx));
                    var dev = audio.GetDefault(EDataFlow.Render);
                    using (var o = new Osd())
                    using (var b = o.Snapshot(Glyphs.ForDevice(dev), dev != null ? dev.Name : "?", "Connected · now the default output"))
                        b.Save(System.IO.Path.Combine(dir, "osd" + sfx));
                }
                var outs = audio.GetDevices(EDataFlow.Render, DeviceState.Active);
                config.EnsureKnown(outs, audio.GetDefaultId(EDataFlow.Render, ERole.Console));
                using (var s = new SettingsForm(config, outs))
                {
                    s.StartPosition = FormStartPosition.Manual;
                    s.Location = new System.Drawing.Point(-4000, -4000);
                    s.Show();
                    Application.DoEvents();
                    using (var b = new System.Drawing.Bitmap(s.Width, s.Height))
                    {
                        s.DrawToBitmap(b, new System.Drawing.Rectangle(0, 0, s.Width, s.Height));
                        b.Save(System.IO.Path.Combine(dir, "settings.png"));
                    }
                }
                Console.WriteLine("Saved: " + dir);
                return 0;
            }

            Console.WriteLine("Usage: MiniAudio.exe [--list | --switch <name|id> | --next | --apps | --route <app> <device|default> | --balance <app> <-100..100>]");
            return 2;
        }
    }
}
