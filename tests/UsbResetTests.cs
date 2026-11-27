// Tests for the parts of "Reset device" that decide what gets reset: which USB device belongs to an
// audio endpoint, whether a hub port really holds that device, and when a reset device counts as back.
// Nothing here touches a real device.

namespace MiniAudio.Tests
{
    static class UsbResetTests
    {
        const string Function = @"USB\VID_1038&PID_227E&MI_00\7&3160C799&0&0000";
        const string Dongle = @"USB\VID_1038&PID_227E\6&28CF390B&0&5";
        const string RootHub = @"USB\ROOT_HUB30\5&1F3A8C2B&0&0";

        public static void AddTo(TestList t)
        {
            t.Add("PickUsbDevice: an audio function of a composite device gives the device itself",
                delegate { Check.Equal(Dongle, UsbReset.PickUsbDevice(new[] { Function, Dongle, RootHub })); });
            t.Add("PickUsbDevice: a plain USB audio device is the device",
                delegate { Check.Equal(@"USB\VID_0D8C&PID_0014\5&2A7B9D1&0&2", UsbReset.PickUsbDevice(new[] { @"USB\VID_0D8C&PID_0014\5&2A7B9D1&0&2", RootHub })); });
            t.Add("PickUsbDevice: lower-case ids work",
                delegate { Check.Equal(Dongle.ToLowerInvariant(), UsbReset.PickUsbDevice(new[] { Function.ToLowerInvariant(), Dongle.ToLowerInvariant() })); });
            t.Add("PickUsbDevice: a Bluetooth headset never gives the Bluetooth adapter", delegate
            {
                Check.Equal(null, UsbReset.PickUsbDevice(new[]
                {
                    @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CD3\7&1A2B3C4D&0&AC80F1E2D3C4_C00000000",
                    @"BTH\MS_BTHBRB\6&2C1D0E9F&0&1",
                    @"USB\VID_8087&PID_0029\5&2A7B9D1&0&14",
                }));
            });
            t.Add("PickUsbDevice: monitor (HDMI) audio is not a USB device",
                delegate { Check.Equal(null, UsbReset.PickUsbDevice(new[] { @"HDAUDIO\FUNC_01&VEN_10DE&DEV_00A3&SUBSYS_10DE0000&REV_1001\5&2E0C8F1&0&0001", @"PCI\VEN_10DE&DEV_22BA" })); });
            t.Add("PickUsbDevice: a function whose parent is another device gives nothing",
                delegate { Check.Equal(null, UsbReset.PickUsbDevice(new[] { Function, @"USB\VID_05E3&PID_0610\6&28CF390B&0&3" })); });
            t.Add("PickUsbDevice: a function without a known parent gives nothing",
                delegate { Check.Equal(null, UsbReset.PickUsbDevice(new[] { Function })); });
            t.Add("PickUsbDevice: a hub is never picked",
                delegate { Check.Equal(null, UsbReset.PickUsbDevice(new[] { RootHub })); });
            t.Add("PickUsbDevice: no ancestors gives nothing",
                delegate { Check.Equal(null, UsbReset.PickUsbDevice(new string[0])); });

            t.Add("PortHolds: the port's device has the same vendor and product id",
                delegate { Check.True(UsbReset.PortHolds(ConnectionInfo(5, 0x1038, 0x227E), Dongle), "expected a match"); });
            t.Add("PortHolds: another product on the port does not match",
                delegate { Check.True(!UsbReset.PortHolds(ConnectionInfo(5, 0x1038, 0x1830), Dongle), "expected no match"); });
            t.Add("PortHolds: an empty port does not match",
                delegate { Check.True(!UsbReset.PortHolds(ConnectionInfo(5, 0, 0), Dongle), "expected no match"); });
            t.Add("PortHolds: a short answer from the hub does not match",
                delegate { Check.True(!UsbReset.PortHolds(new byte[8], Dongle), "expected no match"); });
            t.Add("PortHolds: an id without vendor and product does not match",
                delegate { Check.True(!UsbReset.PortHolds(ConnectionInfo(5, 0, 0), RootHub), "expected no match"); });

            t.Add("ResetWatch: waits while the device has not disconnected yet",
                delegate { Check.Equal(ResetWatch.Step.Wait, new ResetWatch().Next(1000, true)); });
            t.Add("ResetWatch: waits while the device is gone",
                delegate { Check.Equal(ResetWatch.Step.Wait, new ResetWatch().Next(2000, false)); });
            t.Add("ResetWatch: a device that was gone and is active again is back", delegate
            {
                var w = new ResetWatch();
                w.Next(1000, false);
                Check.Equal(ResetWatch.Step.Back, w.Next(2000, true));
            });
            t.Add("ResetWatch: a device that never left has not been reset after 5 seconds",
                delegate { Check.Equal(ResetWatch.Step.Unchanged, new ResetWatch().Next(5000, true)); });
            t.Add("ResetWatch: a device that comes back late is still back", delegate
            {
                var w = new ResetWatch();
                w.Next(1000, false);
                Check.Equal(ResetWatch.Step.Back, w.Next(12000, true));
            });
            t.Add("ResetWatch: a device still gone after 30 seconds is lost",
                delegate { Check.Equal(ResetWatch.Step.Lost, new ResetWatch().Next(30000, false)); });
        }

        // USB_NODE_CONNECTION_INFORMATION_EX: port number, then the device descriptor (vendor at byte 12, product at 14)
        static byte[] ConnectionInfo(int port, int vid, int pid)
        {
            var b = new byte[35];
            b[0] = (byte)port;
            b[12] = (byte)(vid & 0xFF); b[13] = (byte)(vid >> 8);
            b[14] = (byte)(pid & 0xFF); b[15] = (byte)(pid >> 8);
            return b;
        }
    }
}
