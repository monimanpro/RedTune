# Red Tune

A control panel for AMD Ryzen laptops: power limits and presets, fan curves, a per-core voltage curve with an automatic tune, graphics card tuning, a system-wide sound effect, memory timings, program-to-chiplet pinning, startup items and clean-up — in one program, with one look.

Red Tune reads the hardware when it starts and shows only the pages that fit the machine. It is aimed at laptops with Ryzen 5000 to 9000 series processors of any brand; desktop PCs with a Ryzen processor run it in a generic mode.

> **Warning.** Red Tune changes processor, graphics card, memory and firmware settings. Wrong values can crash the computer, corrupt data or keep it from starting. You use it at your own risk. If something went wrong, hold **Shift** while Red Tune starts: it then applies nothing.

## What it does

### Home
Load, clocks, temperatures, fan speeds and power («now / limit») of the processor, the graphics card and memory; a card per drive; the power mode switch.

### Power
Presets of your own power mode, each holding everything below. The page has five tabs.

- **Processor** — power limits (sustained, short, peak), temperature limit, current limits. On laptops whose firmware offers a tuning interface the limits go through it; elsewhere they are written straight to the processor's management unit (PPT, TDC, EDC, or STAPM / fast / slow on mobile chips).
- **Graphics card** — the card's power and temperature limits where the firmware exposes them, and which graphics preset goes with the power preset.
- **Fans** — your own fan curves by temperature: one per fan or one for both, with a safety net (a critical temperature always wins), hysteresis and smoothing.
- **Power plans** — which Windows power plan belongs to each mode, and the plan's processor settings in one place: clock ceiling, boost mode, energy preference, minimum state, core parking, the plan's kind, and the Windows power mode laid over balanced plans.
- **Curves** — which voltage curve belongs to each mode.

### Curve
The Curve Optimizer value of every core, on any number of cores and chiplets: sliders, presets, history, and what to apply when Windows starts.

- **Auto-tune** finds values by itself. It loads the cores (its own load written for this, or Prime95 if you point to it), watches the hardware error records Windows keeps, steps back from the values that fail and leaves a margin. Two modes: *daily* (deeper, a value per core) and *strict* (holds whatever set of cores is busy, noticeably shallower). A run survives restarts: after a crash it goes on from where it stopped.
- **Watchdog** reads the processor's corrected-error records once a minute and advises which core to raise; it never writes without your button.

### Graphics card (NVIDIA)
The card's voltage/clock curve as points you drag, a memory clock offset, presets, step back and forward (Ctrl+Z / Ctrl+Y), and automatic apply with a power preset.

### Sound
A sound effect of Red Tune's own that Windows runs for every program on the output device you pick.

- **Equalizer** — up to 12 bands (bell, shelves, cuts, notch) drawn as a curve over the live spectrum; points are dragged, strips beside the plot give the same bands as faders and dials.
- **Compressor** — in one, two or three frequency bands, each with its own threshold, ratio, knee, attack, release and make-up gain. A picture shows the transfer curve with draggable points, eight dials, and a monitor that is either the last ten seconds or the bands across the frequencies with their live levels. Bands can be soloed and muted for listening.
- **Limiter** — a look-ahead limiter that measures the true peak of the wave, so the output never passes the ceiling and does not rasp.
- Presets, settings per device, step back and forward, a level meter, and a switch for a manufacturer's own effect (Nahimic) that would otherwise add to Red Tune's.

### Memory
Live timings and clocks, profiles, a memory test, and writing timings on machines whose BIOS exposes AMD's overclocking interface. Writing is enabled only on verified models; elsewhere the page is read-only.

### Clean-up
- **Memory** — closes what was started after you signed in, as if the computer had just started: with a white list and a black list, a polite close first and a question before anything is forced.
- **Files on disk** — measures, then deletes only what you tick: temporary files, the Recycle Bin, update and browser caches, error reports, thumbnail and shader caches, crash dumps. Personal folders are never looked at.
- **Removing programs** — runs the program's own uninstaller, then finds what it left behind (folders, registry keys, shortcuts, startup entries). Files go to the Recycle Bin; registry keys are exported before they are deleted.

### Device (laptops with a supported firmware interface)
Keyboard backlight, charging mode, USB power while asleep, the graphics card's working mode, display resolution, refresh rate, scale and HDR, one brightness for all power plans, and the Fn keys.

### Distribution (processors with two chiplets)
Pins programs to a chiplet by folder or process name: games on one, the background on the other.

### Startup
Everything that starts with Windows in one list: Run keys, startup folders, scheduled tasks, packaged apps.

### Components and Settings
- **Components** — what Red Tune found on this machine, what each part needs, and a system report you can copy when asking for a model to be supported.
- **Settings** — language, interface scale, start with Windows, close to the tray, expert functions; what the mini panel shows; notifications; the overlay.

### Around the window
- **Mini panel** — a click on the tray icon opens small glass blocks: power mode, power preset, curve, graphics preset, output device, overlay, memory clean-up, displays off.
- **Notifications** — an on-screen note when the power mode, a Fn key, Caps Lock and the like change; position, duration and look are yours to set.
- **Overlay** — frame rate, temperatures and clocks of the processor and the graphics card in a corner of the screen over games.

## How it works

- **One program, no service.** A single executable for .NET Framework 4.8 (part of Windows 10 and 11) plus two small native libraries of its own: the load for the auto-tune and the sound effect. A scheduled task starts it in the tray when you sign in, if you ask for that.
- **The processor** is reached through the [PawnIO](https://pawnio.eu/) driver: Red Tune talks to the processor's system management unit to read the power table and to write the voltage curve and the power limits. PawnIO is installed separately with its own signed installer; without it everything except those parts still works.
- **Laptop firmware.** Where a laptop's firmware offers a management interface in Windows (WMI), Red Tune uses it for power modes, limits, fans and device settings. Without one, those rows are hidden and the processor limits go the direct way.
- **Windows power plans** are read and written through Windows' own power API. Changes in the plan block wait for a button; nothing is written while you are still choosing.
- **The graphics card** is tuned through NVIDIA's own driver interfaces.
- **Sound** is an audio processing object: the kind of plug-in Windows' audio engine loads for a device. Red Tune registers it, places it in the device's chain of effects, and talks to it through a small shared file (settings in, meters and a spectrum back). The processing takes about one percent of a single processor core.
- **Memory timings** are read from the processor's registers and written through AMD's overclocking interface in the BIOS, where it exists.
- **No network.** Red Tune does not connect to the internet. The only links (PawnIO, Prime95) open in your browser when you click them.

### Safety nets
- **Safe start.** Hold **Shift** at start, or run with `--safe`: nothing is applied. If the previous start broke off within three minutes of applying (a freeze, a blue screen), the next one applies nothing either. Home then shows a banner with «Apply now».
- **Expert functions.** The curve, memory timings and direct processor limits are hidden until you switch them on in Settings and confirm.
- **Values the hardware did not report are never written** just because a slider stood somewhere: such sliders read «do not change».
- **Other tuning programs.** Red Tune warns when a program that changes the same settings starts with Windows: two of them would fight.

## Requirements

- Windows 10 or 11, 64-bit; administrator rights.
- An AMD Ryzen processor. Intel is not supported.
- [PawnIO](https://pawnio.eu/) for the curve, the direct processor limits and the processor temperature.
- An NVIDIA card for the «Graphics card» page.
- Prime95 is optional: the auto-tune has a load of its own.

## Install

1. Download `RedTune-Setup-x.y.z.exe` from [Releases](../../releases).
2. The installer is not code-signed, so Windows shows «Windows protected your PC»: choose **More info → Run anyway**.
3. If PawnIO is missing, the installer says so and offers to open its download page.

**Uninstall** from Windows' «Installed apps». It removes the sign-in task, takes the sound effect off every device and unregisters it, and asks whether to keep your settings.

**Where things are kept:** settings, presets, curves and the journal in `%APPDATA%\RedTune`; the sound effect's registered copy in `%ProgramFiles%\RedTune\Audio` and its shared file in `%ProgramData%\RedTune`.

## Languages

English, Spanish, Portuguese (Brazil), Russian, Simplified Chinese, Korean, Thai, Vietnamese and Indonesian (Settings → Language). A first start follows the language of Windows. The interface is written in Russian and translated; all translations except English are machine translations that no native speaker has reviewed yet — corrections are welcome (`tools\lang`).

## Status

Red Tune is developed and tested on one laptop: a Lenovo Legion R7000P with a Ryzen 9 8945HX and an RTX 5070 Laptop. On other machines it works out what is available and hides the rest, but writing the curve and the limits on other Ryzen families, the generic mode for other brands and for desktop PCs, and the sound effect on other audio drivers have not been verified on real hardware. The system report («Components» → «Copy the report») is what helps to add a model.

## Build from source

You need the Roslyn C# compiler from Visual Studio or the Build Tools, a C/C++ compiler for the two native libraries (the built ones are kept in `native\`, so the program builds without it), and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```powershell
.\build.ps1              # the program in bin\
.\build.ps1 -Installer   # and dist\RedTune-Setup-<version>.exe
```

The sound effect has a test bench that runs on every build of it; a library that fails a check is not kept.

Checks that write nothing to the hardware:

| Command | What it does |
|---|---|
| `RedTune.exe --hw-report out.txt` | what was found on this machine |
| `RedTune.exe --page-png <page> out.png` | a page drawn off screen (`RT_HW_SIM=cores8,nolenovo,…` pretends to be other hardware) |
| `RedTune.exe --lang-check out.txt` | how much of the interface each language covers |
| `RedTune.exe --comp-graph-check out.txt`, `--eq-strips-check`, `--motion-check`, `--sound-undo-check`, `--ui-edit-check` | the interface driven by mouse messages in a window of its own |
| `RedTune.exe --simulate-tune …` | the auto-tune against a made-up processor |

Translations: `tools\lang` holds the scripts and the translated texts (`tr\<code>.json`); `node build.js <repo>` writes `lang\<code>.txt`, which the build embeds.

## License

GPL-3.0 — see [LICENSE](LICENSE). The third-party parts that ship with Red Tune, and the projects whose code or findings it builds on, are listed with their licenses in [THIRD-PARTY.md](THIRD-PARTY.md).
