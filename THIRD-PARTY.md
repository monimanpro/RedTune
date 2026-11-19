# Third-party components

Red Tune ships these components unchanged, next to its own executable. Their licenses apply to them;
Red Tune itself is licensed under GPL-3.0 (see `LICENSE`). All three are modules for the PawnIO driver:
small programs the driver runs to reach the hardware. Everything else in the build is Red Tune's own code.

| Component | Where in the build | What it is used for | Author | License | Source |
|---|---|---|---|---|---|
| PawnIO module RyzenSMU | `ram\RyzenSMU.bin` | Reaching the SMU through the PawnIO driver: the Curve Optimizer, the AMD power limits, the power table, the processor temperature; taken from ryzen-smu-cli 0.1.5 (sp00n build from CoreCycler 0.11) | namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| PawnIO module AMDFamily17 | `ram\AMDFamily17.bin` | Reading SMN registers through the PawnIO driver: the memory timings; taken from ZenStates-Core 1.79, which carries it | namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| PawnIO module SmbusPIIX4 | `ram\SmbusPIIX4.bin` | The SMBus of the AMD chipset through the PawnIO driver: the temperature of the memory modules; taken from ZenStates-Core 1.79, which carries it | Steve-Tech, namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |

## Code adapted from other projects

| Project | What | License |
|---|---|---|
| LenovoLegionToolkit-Plugins, CustomFanCurve plugin | The fan curve logic in `src\FanControl.cs` (curve math, safety net, hysteresis, smoothing) | GPL-3.0 — https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit-Plugins |
| Lenovo Legion Toolkit | Lenovo WMI feature IDs and how they are used (power limits, fans, modes) | GPL-3.0 — https://github.com/BartoszCichecki/LenovoLegionToolkit |
| G-Helper | `src\Asus.cs`: how ASUS's firmware is called through the driver `\\.\ATKACPI` (the DSTS and DEVS methods), the device numbers and the values of the performance modes, the charge limit, the keyboard backlight, the Fn lock and the fan speeds | GPL-3.0 — https://github.com/seerge/g-helper |
| Linux kernel, `asus-wmi` driver | The same device numbers and values, checked against it (`src\Asus.cs`, the list in `src\Diag.cs`) | GPL-2.0-or-later — https://github.com/torvalds/linux |
| ZenStates-Core | `src\Smu.cs`: SMU message numbers and mailbox addresses per processor family, the mailbox exchange, the curve's core numbering, the power table layouts (memory clocks). `src\Ram\LiveTimings.cs`: the register map of the memory timings | GPL-3.0 — https://github.com/irusanov/ZenStates-Core |

## Not included

- **PawnIO** (the driver) is installed separately from https://pawnio.eu/ — it has its own signed installer.
- **Prime95** (used by the auto-tune) is not shipped; point Red Tune to it on the «Компоненты» / «Components» page.
