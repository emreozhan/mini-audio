# MiniAudio

Small Windows tray app for switching between audio outputs (USB dongle headset, Bluetooth speaker, …) and managing their volume levels. Single EXE, no installation, built with the C# compiler that ships with Windows.

## Features

- **Flyout** (left-click the tray icon): every active output and input with its own volume slider, mute button and live level meter. Click a device's name to make it the default; the mouse wheel over a row changes its volume. The toggle at the bottom turns auto switching on or off, the gear opens the settings.
- **Auto switching** (applies to devices checked in the settings list)
  - A newly connected output (e.g. a Bluetooth speaker) becomes the default.
  - When the device in use disconnects, MiniAudio switches to the first connected device in your priority list, so Windows does not fall back to something like a monitor's HDMI audio.
  - Monitor/HDMI audio and Bluetooth "Hands-Free" outputs are unchecked by default.
- **Next-device hotkey** (default `Ctrl+Alt+S`, can be changed or turned off): cycles through the checked outputs and shows a short on-screen notice.
- The communications (call) device is switched together with the default device (can be turned off). Optionally the matching microphone is switched too.
- Middle-click the tray icon to mute/unmute the default output.
- Follows the Windows light/dark theme and accent color.

## Requirements

- Windows 10 or 11 (tested on Windows 11; rounded corners only on Windows 11)
- .NET Framework 4.x, which is part of Windows

## Build & run

No SDK needed; the C# compiler ships with the .NET Framework. In **Command Prompt**, from the repository root:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /optimize+ /win32manifest:app.manifest /out:MiniAudio.exe src\*.cs
start "" MiniAudio.exe
```

Only one instance runs at a time; the app lives in the system tray.

## Command line

Works whether or not the tray app is running (handy for Stream Deck or hotkey tools):

```bat
MiniAudio.exe --list
MiniAudio.exe --switch Soundcore
MiniAudio.exe --next
```

- `--list` prints active outputs and inputs; `*` marks the default device, `c` the communications device.
- `--switch <name|id>` makes the first active device whose name contains the text (or whose id matches) the default.
- `--next` switches to the next checked output in priority order.

## Settings

Right-click the tray icon → **Settings…** (or the gear in the flyout):

- Device priority order, and which devices take part in auto switching and the hotkey cycle
- Auto switching rules, communications device, microphone, on-screen notice
- Hotkey, microphones in the flyout, start with Windows

Settings are stored in `%APPDATA%\MiniAudio\config.txt`.

## Notes

- Changing the default device uses `IPolicyConfig`, an undocumented Windows COM interface. A future Windows release could change it. Tested on Windows 11 (build 26200).
- Some 2.4 GHz dongles keep their audio endpoint active even when the headset is switched off, so Windows reports no "connected" event for them. Use the flyout or the hotkey in that case.
