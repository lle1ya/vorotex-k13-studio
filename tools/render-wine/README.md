# Screenshots on Linux (Wine + Xvfb)

WPF renders under Wine 11 (Kron4ek wow64 build) if the prefix has fonts *registered* in
`HKLM\Software\Microsoft\Windows NT\CurrentVersion\Fonts` (otherwise WPF finds no font and fail-fasts in
`TypefaceMap.MapUnresolvedCharacters`).

1. `mkdj.py` – DejaVu Sans renamed to "Segoe UI", "Segoe UI Variable Text", "Arial", "Tahoma"… (+ cmap aliases for ⏺, 🔊 …).
2. `mkicons.py` – stand-in "Segoe Fluent Icons" built from Material Symbols (Apache-2.0), mapped to the PUA codes the app uses.
3. Register the files with `wine regedit fonts.reg`, then
   `xvfb-run wine VorotexK13Studio.exe --render-v47 Z:\path\out`.

Screens look close to Windows but use substitute fonts and icons.
