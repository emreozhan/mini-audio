// "Reset device": reconnects the USB device of an audio output by software, for a dongle that stops
// applying volume changes until it is unplugged and plugged back in.
//
// An audio endpoint hangs on the device's audio function ("USB\VID_x&PID_y&MI_00\...") or, for a plain
// USB audio device, on the device itself. The reset asks the hub the device is plugged into to cycle its
// port: Windows sees the device leave and arrive again, as when the plug is pulled (the port usually
// keeps its power, so this is not a full power cycle). If the hub cannot do that, the device is
// restarted with pnputil instead.
//
// Both need administrator rights, so the tray app starts "MiniAudio.exe --reset <endpoint id>" elevated.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace MiniAudio
{
    static class UsbReset
    {
        // CultureInvariant: with a Turkish locale "vid" would not match "VID" (dotted and dotless i)
        static readonly Regex UsbId = new Regex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(&MI_[0-9A-F]{2})?\\",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // The USB device to reset for an audio endpoint, from the endpoint's ancestors (parent first).
        // null when the endpoint is not directly on a USB device: Bluetooth (its adapter must not be reset),
        // monitor or built-in audio.
        public static string PickUsbDevice(IList<string> ancestors)
        {
            if (ancestors == null || ancestors.Count == 0) return null;
            var first = UsbId.Match(ancestors[0] ?? "");
            if (!first.Success) return null;
            if (!first.Groups[3].Success) return ancestors[0]; // not a function of a composite device: the device itself
            if (ancestors.Count < 2) return null;
            var parent = UsbId.Match(ancestors[1] ?? "");
            bool same = parent.Success && !parent.Groups[3].Success
                && string.Equals(parent.Groups[1].Value, first.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parent.Groups[2].Value, first.Groups[2].Value, StringComparison.OrdinalIgnoreCase);
            return same ? ancestors[1] : null;
        }

        // Whether a hub port holds the given device. connectionInfo is the hub's answer for that port
        // (USB_NODE_CONNECTION_INFORMATION_EX: the device descriptor starts at byte 4, vendor id at 12, product id at 14).
        public static bool PortHolds(byte[] connectionInfo, string instanceId)
        {
            var id = UsbId.Match(instanceId ?? "");
            if (!id.Success || connectionInfo == null || connectionInfo.Length < 16) return false;
            int vid = connectionInfo[12] | connectionInfo[13] << 8, pid = connectionInfo[14] | connectionInfo[15] << 8;
            return vid != 0 && vid == Convert.ToInt32(id.Groups[1].Value, 16) && pid == Convert.ToInt32(id.Groups[2].Value, 16);
        }

        // Instance id of the USB device behind an audio endpoint, or null when there is nothing to reset
        public static string FindDevice(AudioDevice d)
        {
            if (d == null || string.IsNullOrEmpty(d.ParentInstance)) return null;
            var chain = new List<string> { d.ParentInstance };
            uint node, parent;
            if (CM_Locate_DevNodeW(out node, d.ParentInstance, 0) == 0 && CM_Get_Parent(out parent, node, 0) == 0)
            {
                string id = DeviceId(parent);
                if (id != null) chain.Add(id);
            }
            return PickUsbDevice(chain);
        }

        public static bool CanReset(AudioDevice d) { return FindDevice(d) != null; }

        public static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Starts "MiniAudio.exe --reset <endpoint id>" with administrator rights (Windows asks the user).
        // Throws Win32Exception when the user declines.
        public static Process StartElevated(string endpointId)
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--reset \"" + endpointId + "\"");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            return Process.Start(psi);
        }

        // Reconnects the device; needs administrator rights. The device is gone for a few seconds afterwards.
        public static bool Reset(string usbInstanceId)
        {
            return CyclePort(usbInstanceId) || Restart(usbInstanceId);
        }

        // ---- hub port ----

        const uint IoctlConnectionInfo = 0x220448; // IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX
        const uint IoctlCyclePort = 0x220444;      // IOCTL_USB_HUB_CYCLE_PORT
        static Guid hubInterface = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8"); // GUID_DEVINTERFACE_USB_HUB

        static bool CyclePort(string usbInstanceId)
        {
            int port;
            using (var hub = OpenPort(usbInstanceId, out port))
            {
                if (hub == null) return false;
                var cycle = new byte[8]; // USB_CYCLE_PORT_PARAMS: port number, status
                BitConverter.GetBytes(port).CopyTo(cycle, 0);
                int returned;
                return DeviceIoControl(hub, IoctlCyclePort, cycle, cycle.Length, cycle, cycle.Length, out returned, IntPtr.Zero)
                    && BitConverter.ToInt32(cycle, 4) == 0; // the hub's own status
            }
        }

        // Opens the hub the device is plugged into, after checking that the port really holds this device
        // (so that no other device, such as a disk, is ever disconnected). null when that cannot be confirmed.
        static SafeFileHandle OpenPort(string usbInstanceId, out int port)
        {
            port = 0;
            uint node, parent;
            if (CM_Locate_DevNodeW(out node, usbInstanceId, 0) != 0 || CM_Get_Parent(out parent, node, 0) != 0) return null;

            var address = new byte[4]; // for a USB device, the address is its port number on the hub
            uint type, size = (uint)address.Length;
            if (CM_Get_DevNode_Registry_PropertyW(node, 0x1D /* CM_DRP_ADDRESS */, out type, address, ref size, 0) != 0) return null;
            port = BitConverter.ToInt32(address, 0);
            string hubId = DeviceId(parent);
            string path = hubId != null ? InterfacePath(hubId) : null;
            if (port <= 0 || path == null) return null;

            var hub = CreateFileW(path, 0x40000000 /* GENERIC_WRITE */, 3 /* share read and write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (hub.IsInvalid) { hub.Dispose(); return null; }
            var info = new byte[512];
            BitConverter.GetBytes(port).CopyTo(info, 0);
            int returned;
            if (!DeviceIoControl(hub, IoctlConnectionInfo, info, info.Length, info, info.Length, out returned, IntPtr.Zero)
                || !PortHolds(info, usbInstanceId))
            {
                hub.Dispose();
                return null;
            }
            return hub;
        }

        static string DeviceId(uint node)
        {
            var sb = new StringBuilder(260);
            return CM_Get_Device_IDW(node, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
        }

        static string InterfacePath(string hubInstanceId)
        {
            uint size;
            if (CM_Get_Device_Interface_List_SizeW(out size, ref hubInterface, hubInstanceId, 0) != 0 || size < 2) return null;
            var buffer = new char[size];
            if (CM_Get_Device_Interface_ListW(ref hubInterface, hubInstanceId, buffer, size, 0) != 0) return null;
            string first = new string(buffer).Split('\0')[0]; // a list of strings, each ending in a null character
            return first.Length > 0 ? first : null;
        }

        // ---- restart (when the hub cannot cycle the port) ----

        static bool Restart(string usbInstanceId)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), "/restart-device \"" + usbInstanceId + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                    return p.WaitForExit(30000) && p.ExitCode == 0;
            }
            catch { return false; } // no pnputil, or one without /restart-device (before Windows 10 2004)
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, byte[] buffer, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid interfaceClass, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string deviceId, char[] buffer, uint size, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    }

    // A reset in progress: the device leaves and comes back a few seconds later. The tray app then makes it
    // the default output again and turns software volume back on if it was on.
    class ResetWatch
    {
        public enum Step { Wait, Back, Unchanged, Lost }

        const int UnchangedMs = 5000; // never seen gone: the reset did not reach the device
        const int LostMs = 30000;     // still gone: it is not coming back on its own

        public string Id, Name, Glyph;
        public bool Soft; // software volume was on for the device
        public int StartTick;
        bool gone;

        public Step Next(int elapsedMs, bool active)
        {
            if (!active)
            {
                gone = true;
                return elapsedMs >= LostMs ? Step.Lost : Step.Wait;
            }
            return gone ? Step.Back : elapsedMs >= UnchangedMs ? Step.Unchanged : Step.Wait;
        }
    }
}
