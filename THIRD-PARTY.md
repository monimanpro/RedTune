# Third-party components and acknowledgements

Red Tune itself is licensed under GPL-3.0 (see `LICENSE`). This file has two parts: what ships with the program
that is not Red Tune's own, and the projects Red Tune learned the hardware's interfaces from.

## 1. What ships with Red Tune

Four files, unchanged, next to the executable: three modules for the PawnIO driver (small programs the driver runs to
reach the hardware) and the driver's own installer. Two libraries are built into Red Tune's sound effect: RNNoise and DeepFilterNet (with the Rust crates it needs). Their
licenses apply to them. Everything else in the build is Red Tune's own code.

| Component | Where in the build | What it is used for | Author | License | Source |
|---|---|---|---|---|---|
| PawnIO module RyzenSMU | `ram\RyzenSMU.bin` | Reaching the processor's system management unit through the PawnIO driver: the Curve Optimizer, the power limits, the power table, the processor temperature; taken from ryzen-smu-cli 0.1.5 (sp00n build from CoreCycler 0.11) | namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| PawnIO module AMDFamily17 | `ram\AMDFamily17.bin` | Reading the processor's registers through the PawnIO driver: the memory timings; taken from ZenStates-Core 1.79, which carries it | namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| PawnIO module SmbusPIIX4 | `ram\SmbusPIIX4.bin` | The SMBus of the AMD chipset through the PawnIO driver: the temperature of the memory modules; taken from ZenStates-Core 1.79, which carries it | Steve-Tech, namazso | LGPL-2.1 | https://github.com/namazso/PawnIO.Modules |
| DeepFilterNet 3 (libDF 0.5.7-pre) and its model | built into `audio\RedTuneApo.dll` through Red Tune's wrapper `native\apo\dfn\src\lib.rs` (libDF and the model unchanged in `native\apo\dfn\DeepFilterNet`, commit 7b14b895fe7f) | The other model of the denoiser in «Sound → Microphone»: a neural network that removes noise from a voice, with its trained weights (DeepFilterNet3_onnx) | Hendrik Schröter and contributors | MIT or Apache-2.0 (MIT text below) | https://github.com/Rikorose/DeepFilterNet |
| Rust crates under DeepFilterNet (tract, ndarray, flate2, tar and others, 95 in all) | built into `audio\RedTuneApo.dll` with it; the list with versions and licenses in `native\apo\dfn\CRATES.md` | The engine that runs the model and what it needs | their authors (Sonos for tract) | MIT, Apache-2.0, Unlicense, Zlib, BSD-2, CC0 (each crate's own; all permissive) | https://crates.io |
| RNNoise 0.2 | built into `audio\RedTuneApo.dll` (sources unchanged in `native\apo\rnnoise`) | The denoiser of «Sound → Microphone»: a small neural network that removes noise from a voice, with its trained weights | Jean-Marc Valin, Xiph.Org Foundation, Mozilla, Amazon and contributors | BSD-3-Clause (text below) | https://github.com/xiph/rnnoise (release v0.2) |
| PawnIO 2.2.0 installer | `pawnio\PawnIO_setup.exe` | Installs the PawnIO driver (signed by Microsoft) when it is missing: by the Red Tune setup (a task, on by default) and by «Install» on the «Components» page, both with its own `-install -silent`. The official release file, unchanged: SHA-256 `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`, signed by namazso.eu | namazso | GPL-2.0 with an exception for programs using its IOCTL interface (the driver) | https://github.com/namazso/PawnIO (source), https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0 (this file) |

Not shipped, installed by you if you want them:

- **Prime95** — optional for the auto-tune, which has a load of its own; point Red Tune to it on the «Components» page.

### DeepFilterNet license (MIT; Apache-2.0 is the other choice it offers)

```
The MIT License (MIT)
Copyright (c) 2021 Hendrik Schröter

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### RNNoise license

```
Copyright (c) 2007-2017, 2024 Jean-Marc Valin
Copyright (c) 2023 Amazon
Copyright (c) 2017, Mozilla
Copyright (c) 2005-2017, Xiph.Org Foundation
Copyright (c) 2003-2004, Mark Borgerding

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.Org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

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
