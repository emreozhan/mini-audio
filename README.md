# MiniAudio

Small Windows tray app for switching between audio outputs (USB dongle headset, Bluetooth speaker, …) and managing their volume levels. Single EXE, no installation, built with the C# compiler that ships with Windows.

## Features

- **Flyout** (left-click the tray icon): every active output and input with its own volume slider, mute button and live level meter. Click a device name to make it the default. Mouse wheel over a row changes its volume.
- **Auto switching**
  - A newly connected output (e.g. the Bluetooth speaker) becomes the default.
  - When the device in use disconnects, MiniAudio returns to the first connected device in your priority list (instead of Windows picking a monitor's HDMI audio).
  - Monitor/HDMI audio and Bluetooth "Hands-Free" endpoints are excluded by default.
- **Hotkey** `Ctrl+Alt+S` (configurable): cycle to the next output; a short on-screen notice shows the new device.
- Optional: switch the communications (call) device and the matching microphone too.
- Middle-click the tray icon to mute/unmute the default output.
- Follows Windows light/dark theme and accent color.

## Build & run

Uses the C# compiler that ships with Windows (.NET Framework 4.x); nothing to install. From the repository root:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /optimize+ /win32manifest:app.manifest /out:MiniAudio.exe src\*.cs
start "" MiniAudio.exe
```

Only one instance runs at a time; the app lives in the system tray.

## Command line

Works while the tray app is running (useful for Stream Deck / shortcut tools):

```bat
MiniAudio.exe --list
MiniAudio.exe --switch Soundcore
MiniAudio.exe --next
```

## Settings

Right-click → *Ayarlar…* (settings): priority order, which devices take part in auto switching, hotkey, start with Windows. Stored in `%APPDATA%\MiniAudio\config.txt`.

## Notes

- Changing the default device uses the undocumented `IPolicyConfig` COM interface (the same one Windows' sound settings use); it has been stable from Windows 7 to 11.
- Some 2.4 GHz dongles keep their audio endpoint active even when the headset is switched off, so Windows sees no "connect" event for them. Use the flyout or the hotkey in that case.
