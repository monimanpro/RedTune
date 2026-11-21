# Builds RedTuneApo.dll (Red Tune's sound effect for Windows, see apo.cpp) and its test bench apotest.exe with the
# C++ compiler of Visual Studio or Build Tools, then runs the bench: a dll that fails a check is not kept.
# The dll stays next to these sources; ..\..\build.ps1 copies it into bin\audio and calls this script when a source
# is newer than the dll. Without a compiler the dll that is already here is used.
#   .\build.ps1            build and test
#   .\build.ps1 -NoTest    build only
param([switch]$NoTest)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
# Visual Studio: vswhere first (quick, and what GitHub Actions has), then a search through its folders
$vsw = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoots = @(if (Test-Path $vsw) { & $vsw -latest -products * -property installationPath }) + @("C:\Program Files\Microsoft Visual Studio", "C:\Program Files (x86)\Microsoft Visual Studio") | Where-Object { $_ -and (Test-Path $_) }
$vcvars = $vsRoots | ForEach-Object { Get-Item (Join-Path $_ "VC\Auxiliary\Build\vcvars64.bat") -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $vcvars) { $vcvars = Get-ChildItem $vsRoots -Recurse -Filter vcvars64.bat -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $vcvars) { throw "C++ compiler not found (Visual Studio with Desktop development with C++, or Build Tools)" }
$obj = Join-Path $env:TEMP "redtune-apo-obj"; New-Item -ItemType Directory -Force $obj | Out-Null
$out = Join-Path $obj "RedTuneApo.dll"
# /MT: the C++ runtime inside the dll (audiodg has none to share); /fp:precise: the bench compares bit for bit
$flags = "/nologo /O2 /Oi /MT /W3 /EHsc /fp:precise /DUNICODE /D_UNICODE /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS"
# 21.11: RNNoise (rnnoise\, BSD-3), the denoiser: its own C files with AVX2 (every Ryzen has it; vec_avx.h), linked in
$rnnObj = Join-Path $obj "rnn"; New-Item -ItemType Directory -Force $rnnObj | Out-Null
$rnnSrc = (Get-ChildItem "$here\rnnoise\src\*.c").FullName | ForEach-Object { "`"$_`"" }
$rnnFlags = "/nologo /c /O2 /arch:AVX2 /MT /W1 /D_USE_MATH_DEFINES /I`"$here\rnnoise\include`" /I`"$here\rnnoise\src`""
$rnnLib = (Get-ChildItem "$here\rnnoise\src\*.c").BaseName | ForEach-Object { "`"$rnnObj\$_.obj`"" }
# 21.11: DeepFilterNet 3 (dfn\DeepFilterNet, MIT/Apache-2.0) through Red Tune's Rust wrapper (dfn\src\lib.rs): cargo
# builds it into a static library (the versions pinned by dfn\Cargo.lock; AVX2 and the static C runtime by
# dfn\.cargo\config.toml, which cargo reads from the folder it runs in). Without cargo a library built before is used;
# without either the effect is built without it (RNNoise only).
$cargo = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
if (-not (Test-Path $cargo)) { $c = Get-Command cargo -ErrorAction SilentlyContinue; $cargo = if ($c) { $c.Source } else { $null } }
$dfTarget = Join-Path $obj "dfn"; $dfLib = Join-Path $dfTarget "release\rtdf.lib"
if ($cargo) {
    Push-Location "$here\dfn"; $ErrorActionPreference = "Continue"
    # 21.11 (release 0.4.5): the source paths Rust keeps for its panic messages named the folders of this PC (the
    # profile with cargo's registry, this folder); they are written as "~" and "dfn" instead. The flags of
    # .cargo\config.toml go along, since this variable replaces them; its parts are split by 0x1F (paths have spaces).
    $env:CARGO_ENCODED_RUSTFLAGS = (@("-C", "target-cpu=x86-64-v3", "-C", "target-feature=+crt-static", "--remap-path-prefix=$env:USERPROFILE=~", "--remap-path-prefix=$here\dfn=dfn") -join [char]0x1F)
    & $cargo build --release --locked --target-dir $dfTarget 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match "error|warning: unused|Finished" }
    $ok = $LASTEXITCODE -eq 0; $ErrorActionPreference = "Stop"; Pop-Location; Remove-Item Env:CARGO_ENCODED_RUSTFLAGS -ErrorAction SilentlyContinue
    if (-not $ok) { throw "DeepFilterNet (dfn): cargo build failed" }
}
$dfFlags = ""; $dfLink = ""
if (Test-Path $dfLib) { $dfFlags = "/DRTD_DFN"; $dfLink = "`"$dfLib`" ws2_32.lib userenv.lib bcrypt.lib ntdll.lib synchronization.lib oleaut32.lib" }
else { Write-Host "DeepFilterNet left out: no cargo and no library built before" }
$cmd = @(
    "call `"$($vcvars.FullName)`" >nul 2>nul",
    "cl $rnnFlags /Fo`"$rnnObj\\`" $($rnnSrc -join ' ')",
    "cl $flags $dfFlags /c /Fo`"$obj\apo.obj`" `"$here\apo.cpp`"",
    "link /nologo /DLL /OPT:REF /OPT:ICF /NODEFAULTLIB:atls.lib /DEF:`"$here\RedTuneApo.def`" /OUT:`"$out`" /IMPLIB:`"$obj\RedTuneApo.lib`" `"$obj\apo.obj`" $($rnnLib -join ' ') $dfLink audiobaseprocessingobject.lib audiomediatypecrt.lib legacy_stdio_definitions.lib audioeng.lib ole32.lib shell32.lib advapi32.lib user32.lib uuid.lib",
    "cl $flags $dfFlags /Fo`"$obj\apotest.obj`" /Fe`"$obj\apotest.exe`" `"$here\apotest.cpp`" $($rnnLib -join ' ') $dfLink /link /STACK:16777216 /NODEFAULTLIB:atls.lib audiomediatypecrt.lib legacy_stdio_definitions.lib audioeng.lib ole32.lib uuid.lib"
) -join " && "
$ErrorActionPreference = "Continue" # the compiler's notes on stderr are not failures; its exit code is what counts
cmd /c "$cmd 2>&1"
if ($LASTEXITCODE -ne 0) { throw "RedTuneApo.dll: build failed" }
if (-not $NoTest) {
    & "$obj\apotest.exe" $out $obj
    if ($LASTEXITCODE -ne 0) { throw "RedTuneApo.dll: the test bench failed ($LASTEXITCODE)" }
}
Copy-Item $out "$here\RedTuneApo.dll" -Force
Get-Item "$here\RedTuneApo.dll" | Select-Object Name, Length, LastWriteTime
