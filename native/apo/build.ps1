# Builds RedTuneApo.dll (Red Tune's sound effect for Windows, see apo.cpp) and its test bench apotest.exe with the
# C++ compiler of Visual Studio or Build Tools, then runs the bench: a dll that fails a check is not kept.
# The dll stays next to these sources; ..\..\build.ps1 copies it into bin\audio and calls this script when a source
# is newer than the dll. Without a compiler the dll that is already here is used.
#   .\build.ps1            build and test
#   .\build.ps1 -NoTest    build only
param([switch]$NoTest)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$vcvars = Get-ChildItem "C:\Program Files\Microsoft Visual Studio", "C:\Program Files (x86)\Microsoft Visual Studio" -Recurse -Filter vcvars64.bat -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $vcvars) { throw "C++ compiler not found (Visual Studio with Desktop development with C++, or Build Tools)" }
$obj = Join-Path $env:TEMP "redtune-apo-obj"; New-Item -ItemType Directory -Force $obj | Out-Null
$out = Join-Path $obj "RedTuneApo.dll"
# /MT: the C++ runtime inside the dll (audiodg has none to share); /fp:precise: the bench compares bit for bit
$flags = "/nologo /O2 /Oi /MT /W3 /EHsc /fp:precise /DUNICODE /D_UNICODE /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS"
$cmd = @(
    "call `"$($vcvars.FullName)`" >nul 2>nul",
    "cl $flags /c /Fo`"$obj\apo.obj`" `"$here\apo.cpp`"",
    "link /nologo /DLL /OPT:REF /OPT:ICF /NODEFAULTLIB:atls.lib /DEF:`"$here\RedTuneApo.def`" /OUT:`"$out`" /IMPLIB:`"$obj\RedTuneApo.lib`" `"$obj\apo.obj`" audiobaseprocessingobject.lib audiomediatypecrt.lib legacy_stdio_definitions.lib audioeng.lib ole32.lib shell32.lib advapi32.lib user32.lib uuid.lib",
    "cl $flags /Fo`"$obj\apotest.obj`" /Fe`"$obj\apotest.exe`" `"$here\apotest.cpp`" /link /NODEFAULTLIB:atls.lib audiomediatypecrt.lib legacy_stdio_definitions.lib audioeng.lib ole32.lib uuid.lib"
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
