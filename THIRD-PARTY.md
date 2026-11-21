# Third-party components and acknowledgements

Red Tune itself is licensed under GPL-3.0 (see `LICENSE`). This file has two parts: what ships with the program
that is not Red Tune's own, and the projects Red Tune learned the hardware's interfaces from.

## 1. What ships with Red Tune

Four files, unchanged, next to the executable: three modules for the PawnIO driver (small programs the driver runs to
reach the hardware) and the driver's own installer. Their licenses apply to them. Everything else in the build is Red
Tune's own code.

| Component | Where in the build | What it is used for | Author | License | Source |
|---|---|---|---|---|---|
| PawnIO module RyzenSMU | `ram\RyzenSMU.bin` | Reaching the processor's system management unit through the PawnIO driver: the Curve Optimizer, the power limits, the power table, the processor temperature; taken from ryzen-smu-cli 0.1.5 (sp00n build from CoreCycler 0.11) | namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| PawnIO module AMDFamily17 | `ram\AMDFamily17.bin` | Reading the processor's registers through the PawnIO driver: the memory timings; taken from ZenStates-Core 1.79, which carries it | namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| PawnIO module SmbusPIIX4 | `ram\SmbusPIIX4.bin` | The SMBus of the AMD chipset through the PawnIO driver: the temperature of the memory modules; taken from ZenStates-Core 1.79, which carries it | Steve-Tech, namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| PawnIO 2.2.0 installer | `pawnio\PawnIO_setup.exe` | Installs the PawnIO driver (signed by Microsoft) when it is missing: by the Red Tune setup (a task, on by default) and by «Install» on the «Components» page, both with its own `-install -silent`. The official release file, unchanged: SHA-256 `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`, signed by namazso.eu | namazso | GPL-2.0 with an exception for programs using its IOCTL interface (the driver) | https://github.com/namazso/PawnIO (source), https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0 (this file) |

Not shipped, installed by you if you want them:

- **Prime95** — optional for the auto-tune, which has a load of its own; point Red Tune to it on the «Components» page.

## 2. Where the knowledge of the hardware came from

Laptop firmware and the processor's management unit are not documented by their makers. What Red Tune knows about
them — which call, which number, which address, in which order — was learned by reading the open projects below and
then checked on real hardware. No source file of these projects is part of Red Tune: the code that uses this
knowledge was written for Red Tune. They are named here as thanks, and so that anyone can see where a number comes
from. All of them are under licenses compatible with Red Tune's GPL-3.0.

| Project | What was learned from it | License |
|---|---|---|
| [Lenovo Legion Toolkit](https://github.com/BartoszCichecki/LenovoLegionToolkit) | Lenovo's firmware interface in Windows: the WMI classes and feature numbers for power modes, limits, the fan table and the overclocking values, the energy driver's codes for the keyboard, the battery and the Fn keys | GPL-3.0 |
| [LenovoLegionToolkit-Plugins](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit-Plugins), CustomFanCurve | That a program can drive the fans itself by giving the firmware a speed, and the set of extras on the fan page with their thresholds. The controller (`src\FanControl.cs`) is Red Tune's own design | GPL-3.0 |
| [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) | The processor's management unit: message numbers and mailbox addresses per processor family, the layouts of the power table, the numbering of cores for the curve; the register map of the memory timings | GPL-3.0 |
| [RyzenAdj](https://github.com/FlyGoat/RyzenAdj) | The same for the laptop processors: the messages for the STAPM, fast and slow limits and where their values sit in the power table | LGPL-3.0 |
| [G-Helper](https://github.com/seerge/g-helper) | ASUS's firmware interface in Windows: the call through the driver `\\.\ATKACPI`, the device numbers and the values of the performance modes, the charge limit, the keyboard backlight, the Fn lock and the fan speeds | GPL-3.0 |
| [Linux kernel](https://github.com/torvalds/linux), `asus-wmi` driver | The same ASUS device numbers and values, checked one against the other | GPL-2.0-or-later |
