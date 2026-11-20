<task>
Create a second promotional image for MiniAudio that puts its newest feature, **Reset device**, in the spotlight.

Repository: the MiniAudio checkout this file lives in (Windows 11, PowerShell 5.1; clean working tree). MiniAudio is a small Windows tray app (C# / WinForms, single EXE) for switching audio outputs, per-app routing and volume. `README.md` is up to date and is the only source of truth for what the app does; read its "Reset device" bullet and the "Reset device, in detail" note first.

Deliverables, all inside `promo/`:
- `promo/miniaudio-reset-4x3.html`: the source page, laid out at exactly 1600×1200 px.
- `promo/miniaudio-reset-4x3.png`: that page rendered at exactly 1600×1200 px.
- `promo/snapshots-reset/`: fresh pictures of the real UI that the page uses.

End state: a finished 4:3 image a stranger understands in a few seconds: what Reset device is, why they would want it, and how to use it. The existing image `promo/miniaudio-4x3.png` (source `promo/miniaudio-4x3.html`) stays as it is; use it as the style reference so both look like one family (dark background matching the dark flyout, Windows 11 Fluent look, Segoe UI Variable, Segoe Fluent Icons, accent `#4cc2ff`, same card and callout style).
</task>

<real_ui_only>
Every picture of the app must be a real rendering, never a drawn imitation.
- `MiniAudio.exe` in the repository root is the current build. This command renders the UI to PNG files without changing any setting or level: `.\MiniAudio.exe --snapshot promo\snapshots-reset` (run it from PowerShell; Git Bash swallows the backslash). It writes, among others, `flyout-devices-dark.png`, `flyout-apps-dark.png`, `tray-dark.png`, `osd-dark.png` and light variants. Flyout pictures are 380 px wide (100% scale); show them enlarged by about 1.15 to 1.5.
- The Reset button is the ↺ icon in the flyout's footer, directly left of the settings gear. In snapshot pixels its box is x 304 to 332, and vertically from 40 px to 8 px above the bottom edge of the picture. Verify this on the actual PNG before placing the highlight.
- The same icon is available as text: font "Segoe Fluent Icons", code point U+E777.
- The snapshots show whatever devices and apps exist right now (expected: one output "Arctis Nova 7 Gen 2" and its microphone). Work with what is there. Device descriptions may appear in Turkish ("Kulaklıklar · USB"); that is the real UI and is fine.
- There is no real picture of the Windows permission prompt or of the "Reset done" notice. Do not draw fake ones. Explain those steps with text and icons that are clearly illustration, not screenshots.
</real_ui_only>

<content>
All text on the image is English. Keep it uncluttered: one hero, three steps, a few facts, one slim strip for the rest of the app. You may tighten wording, but every claim must be supported by `README.md`.

Hero
- Small brand line: the MiniAudio tile and name, as in the existing image.
- Headline: "Reset device", with a "NEW" badge.
- Tagline: "Replug your USB headset without touching the plug."
- One supporting sentence: some wireless headset dongles stop applying volume changes until they are unplugged and plugged back in; Reset device reconnects the dongle by software.
- Main visual: the real Devices flyout (dark), large, with the ↺ button unmistakably highlighted (accent ring or glow plus a callout such as "Reset device"). This is the focal point of the whole image.

How it works, three numbered steps
1. "Click ↺": at the bottom of the flyout, or right-click the tray icon → Reset device.
2. "Approve the Windows prompt": it needs administrator rights.
3. "Back in seconds": the headset reconnects and is the default output again.

Good to know, short facts (pick the ones that fit cleanly)
- USB outputs only.
- Before anything is disconnected, MiniAudio checks that the port really holds your device.
- If software volume was on, it is turned on again when the device is back.
- Command line: `MiniAudio.exe --reset`.
- The port keeps its power: a device that needs a real power cycle still has to be unplugged by hand. (Include this one; it keeps the image honest.)

Slim strip, "Also in MiniAudio": Devices and Apps tabs, per-app output and balance, auto switching, next-device hotkey, software volume, single EXE with no install. One line each at most; this strip must not compete with the hero.
</content>

<rendering>
Render with headless Edge, with absolute paths:
`& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless --disable-gpu --hide-scrollbars --window-size=1600,1200 --screenshot="<abs>\promo\miniaudio-reset-4x3.png" "file:///<abs>/promo/miniaudio-reset-4x3.html"`
Reference images from the page with relative paths (`snapshots-reset/...`). Put the snapshot and render commands in an HTML comment at the top of the page, as the existing page does.
</rendering>

<action_safety>
Another program state matters here; stay inside these limits.
- Write only these paths: `promo/miniaudio-reset-4x3.html`, `promo/miniaudio-reset-4x3.png`, `promo/snapshots-reset/`. Do not modify anything else: not `README.md`, `src/`, `tests/`, `promo/miniaudio-4x3.*`, `promo/snapshots/`, and not this prompt file.
- `MiniAudio.exe` is running in the system tray as the user's daily app. Do not stop, rebuild, replace or restart it. Running it with `--snapshot` is safe.
- Never run `MiniAudio.exe --reset`, and do not change any audio device, volume or Windows setting. `--reset` would disconnect the user's headset.
- Do not commit, stage, stash, switch branches or push.
- Put temporary files outside the repository, or delete them before you finish.
</action_safety>

<default_follow_through_policy>
Make the layout and wording decisions yourself and keep going. Do not stop to ask about design choices. Stop only if `--snapshot` fails or the Reset button is not visible in the fresh flyout picture; report that instead of drawing around it.
</default_follow_through_policy>

<verification_loop>
Before finishing, check and fix until all of these hold:
- The PNG is exactly 1600×1200 (read its dimensions, do not assume).
- Look at the rendered PNG, whole and in full-resolution crops: no text is cut off, overflowing its card, or overlapping other elements; nothing important sits closer than 48 px to the edge.
- The highlight sits exactly on the ↺ icon of the real flyout picture (check a full-resolution crop of that area).
- Every image the page references exists and loads; fonts and icon glyphs render (no empty boxes).
- `git status --short` shows only the paths listed under action_safety, plus this prompt file if it was already untracked.
</verification_loop>

<compact_output_contract>
Report briefly, in this order:
1. Files created.
2. What the image shows, in two or three sentences.
3. What you verified, with the measured PNG size.
4. Anything you could not do or are unsure about.
</compact_output_contract>
