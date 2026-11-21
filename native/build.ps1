# Builds RedTuneStress.dll (the program's own processor load with a self-check, see stress.h) with the C compiler of
# Visual Studio or Build Tools. The dll is kept next to these sources; ..\build.ps1 copies it beside RedTune.exe and
# calls this script when a source is newer than the dll. Without a C compiler the dll that is already here is used.
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
# Visual Studio: vswhere first (quick, and what GitHub Actions has), then a search through its folders
$vsw = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoots = @(if (Test-Path $vsw) { & $vsw -latest -products * -property installationPath }) + @("C:\Program Files\Microsoft Visual Studio", "C:\Program Files (x86)\Microsoft Visual Studio") | Where-Object { $_ -and (Test-Path $_) }
$vcvars = $vsRoots | ForEach-Object { Get-Item (Join-Path $_ "VC\Auxiliary\Build\vcvars64.bat") -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $vcvars) { $vcvars = Get-ChildItem $vsRoots -Recurse -Filter vcvars64.bat -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $vcvars) { throw "C compiler not found (Visual Studio with «Desktop development with C++», or Build Tools)" }
$obj = Join-Path $env:TEMP "redtune-stress-obj"; New-Item -ItemType Directory -Force $obj | Out-Null
# /MT: the C runtime inside the dll; /GS- /Oi: no stack cookies, intrinsics in line; one file per instruction set
$cmd = @(
    "call `"$($vcvars.FullName)`" >nul 2>nul",
    "cl /nologo /c /O2 /Oi /GS- /MT /W3 /Fo`"$obj\stress.obj`" `"$here\stress.c`"",
    "cl /nologo /c /O2 /Oi /GS- /MT /W3 /arch:AVX2 /Fo`"$obj\stress_avx2.obj`" `"$here\stress_avx2.c`"",
    "cl /nologo /c /O2 /Oi /GS- /MT /W3 /arch:AVX512 /Fo`"$obj\stress_avx512.obj`" `"$here\stress_avx512.c`"",
    "cl /nologo /c /O2 /Oi /GS- /MT /W3 /arch:AVX2 /Fo`"$obj\stress_fft.obj`" `"$here\stress_fft.c`"",
    "cl /nologo /c /O2 /Oi /GS- /MT /W3 /arch:AVX2 /Fo`"$obj\stress_fma.obj`" `"$here\stress_fma.c`"",
    "cl /nologo /c /O2 /Oi /GS- /MT /W3 /arch:AVX2 /Fo`"$obj\stress_like.obj`" `"$here\stress_like.c`"",
    "link /nologo /DLL /OPT:REF /OUT:`"$here\RedTuneStress.dll`" `"$obj\stress.obj`" `"$obj\stress_avx2.obj`" `"$obj\stress_avx512.obj`" `"$obj\stress_fft.obj`" `"$obj\stress_fma.obj`" `"$obj\stress_like.obj`""
) -join " && "
$ErrorActionPreference = "Continue" # the compiler's notes on stderr are not failures; its exit code is what counts
cmd /c "$cmd 2>&1"
if ($LASTEXITCODE -ne 0) { throw "RedTuneStress.dll: build failed" }
Remove-Item "$here\RedTuneStress.lib", "$here\RedTuneStress.exp" -ErrorAction SilentlyContinue
Get-Item "$here\RedTuneStress.dll" | Select-Object Name, Length, LastWriteTime
