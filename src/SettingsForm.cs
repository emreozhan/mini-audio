// Settings window: auto switching rules, device priority, hotkey, startup.
// Changes apply and save immediately.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MiniAudio
{
    class SettingsForm : Form
    {
        readonly Config config;
        readonly HashSet<string> activeIds;
        readonly CheckedListBox list;
        readonly CheckBox chkConnect, chkFallback;
        readonly Button btnUp, btnDown, btnForget;
        bool loading;

        public event Action Changed;

        public SettingsForm(Config config, List<AudioDevice> activeOutputs)
        {
            this.config = config;
            activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in activeOutputs) activeIds.Add(d.Id);

            Text = "MiniAudio — Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = new Font("Segoe UI", 9f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var root = new FlowLayoutPanel();
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Controls.Add(root);

            // --- auto switching ---
            root.Controls.Add(Heading("Auto switching"));
            var chkAuto = Check("Enable auto switching", config.Auto, delegate(bool v) { config.Auto = v; UpdateEnabled(); });
            root.Controls.Add(chkAuto);
            chkConnect = Check("Switch to a newly connected device", config.AutoConnect, delegate(bool v) { config.AutoConnect = v; });
            chkConnect.Margin = new Padding(20, 2, 3, 2);
            root.Controls.Add(chkConnect);
            chkFallback = Check("When the device in use disconnects, return to the first connected device in the list", config.AutoFallback,
                delegate(bool v) { config.AutoFallback = v; });
            chkFallback.Margin = new Padding(20, 2, 3, 2);
            root.Controls.Add(chkFallback);

            // --- priority list ---
            root.Controls.Add(Heading("Output devices and priority"));
            root.Controls.Add(Note("Devices higher in the list win. Unchecked devices are skipped by auto switching and the hotkey\ncycle (monitor/HDMI audio and Bluetooth Hands-Free are unchecked by default)."));

            var listRow = new FlowLayoutPanel();
            listRow.AutoSize = true;
            listRow.WrapContents = false;
            list = new CheckedListBox();
            list.Size = new Size(380, 190);
            list.CheckOnClick = true;
            list.IntegralHeight = false;
            list.ItemCheck += OnItemCheck;
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            listRow.Controls.Add(list);

            var btnCol = new FlowLayoutPanel();
            btnCol.FlowDirection = FlowDirection.TopDown;
            btnCol.AutoSize = true;
            btnUp = MakeButton("Up", delegate { MoveItem(-1); });
            btnDown = MakeButton("Down", delegate { MoveItem(1); });
            btnForget = MakeButton("Forget", delegate { Forget(); });
            btnCol.Controls.Add(btnUp);
            btnCol.Controls.Add(btnDown);
            btnCol.Controls.Add(btnForget);
            listRow.Controls.Add(btnCol);
            root.Controls.Add(listRow);

            // --- switching behavior ---
            root.Controls.Add(Heading("When switching"));
            root.Controls.Add(Check("Also change the communications (call) device", config.SwitchComms, delegate(bool v) { config.SwitchComms = v; }));
            root.Controls.Add(Check("Also switch the microphone to the same device", config.MicFollow, delegate(bool v) { config.MicFollow = v; }));
            root.Controls.Add(Note("Using the microphone of a Bluetooth speaker/headset can drop audio to phone-call quality."));
            root.Controls.Add(Check("Show an on-screen notice when switching", config.ShowOsd, delegate(bool v) { config.ShowOsd = v; }));

            // --- general ---
            root.Controls.Add(Heading("General"));
            var hkRow = new FlowLayoutPanel();
            hkRow.AutoSize = true;
            hkRow.WrapContents = false;
            var hkLabel = new Label();
            hkLabel.Text = "Next device hotkey:";
            hkLabel.AutoSize = true;
            hkLabel.Margin = new Padding(3, 7, 6, 3);
            hkRow.Controls.Add(hkLabel);
            var hk = new ComboBox();
            hk.DropDownStyle = ComboBoxStyle.DropDownList;
            hk.Width = 170;
            foreach (var p in Config.Hotkeys)
            {
                hk.Items.Add(p.Label);
                if (p.Id == config.Hotkey) hk.SelectedIndex = hk.Items.Count - 1;
            }
            hk.SelectedIndexChanged += delegate
            {
                if (hk.SelectedIndex >= 0) { config.Hotkey = Config.Hotkeys[hk.SelectedIndex].Id; Commit(); }
            };
            hkRow.Controls.Add(hk);
            root.Controls.Add(hkRow);

            root.Controls.Add(Check("Show microphones in the flyout", config.ShowMics, delegate(bool v) { config.ShowMics = v; }));
            var auto = new CheckBox();
            auto.Text = "Start with Windows";
            auto.AutoSize = true;
            auto.Checked = Config.IsAutostart();
            auto.CheckedChanged += delegate { Config.SetAutostart(auto.Checked); };
            root.Controls.Add(auto);

            var close = new Button();
            close.Text = "Close";
            close.AutoSize = true;
            close.Margin = new Padding(3, 14, 3, 3);
            close.Click += delegate { Close(); };
            root.Controls.Add(close);
            CancelButton = close;

            FillList(0);
            UpdateEnabled();
        }

        // ---- list ----

        void FillList(int select)
        {
            loading = true;
            list.Items.Clear();
            foreach (var e in config.Outputs)
            {
                string label = e.Name + (activeIds.Contains(e.Id) ? "" : "   (not connected)");
                list.Items.Add(label, e.Included);
            }
            loading = false;
            if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, Math.Min(select, list.Items.Count - 1));
            UpdateButtons();
        }

        void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (loading || e.Index < 0 || e.Index >= config.Outputs.Count) return;
            config.Outputs[e.Index].Included = e.NewValue == CheckState.Checked;
            Commit();
        }

        void MoveItem(int dir)
        {
            int i = list.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= config.Outputs.Count) return;
            var tmp = config.Outputs[i];
            config.Outputs[i] = config.Outputs[j];
            config.Outputs[j] = tmp;
            FillList(j);
            Commit();
        }

        void Forget()
        {
            int i = list.SelectedIndex;
            if (i < 0 || activeIds.Contains(config.Outputs[i].Id)) return;
            config.Outputs.RemoveAt(i);
            FillList(i);
            Commit();
        }

        void UpdateButtons()
        {
            int i = list.SelectedIndex;
            btnUp.Enabled = i > 0;
            btnDown.Enabled = i >= 0 && i < list.Items.Count - 1;
            btnForget.Enabled = i >= 0 && !activeIds.Contains(config.Outputs[i].Id);
        }

        void UpdateEnabled()
        {
            chkConnect.Enabled = config.Auto;
            chkFallback.Enabled = config.Auto;
        }

        void Commit()
        {
            config.Save();
            if (Changed != null) Changed();
        }

        // ---- helpers ----

        CheckBox Check(string text, bool value, Action<bool> apply)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Checked = value;
            c.Margin = new Padding(3, 2, 3, 2);
            c.CheckedChanged += delegate { apply(c.Checked); Commit(); };
            return c;
        }

        static Label Heading(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI Semibold", 10f);
            l.Margin = new Padding(0, 12, 3, 4);
            return l;
        }

        static Label Note(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 0, 3, 6);
            return l;
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.Width = 90;
            b.Height = 28;
            b.Click += click;
            return b;
        }
    }
}
