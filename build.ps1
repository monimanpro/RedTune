# Builds bin\RedTune.exe for .NET Framework 4.8 (built into Windows 10/11, nothing to install).
# Needs the Roslyn compiler from Visual Studio or Build Tools; the old csc in Windows is limited to C# 5.
# The icon is drawn by the app itself (--make-icon), so the first pass builds without it.
#   .\build.ps1              the program in bin\
#   .\build.ps1 -Installer   and dist\RedTune-Setup-<version>.exe (needs Inno Setup 6)
param([switch]$Installer)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = Get-ChildItem "C:\Program Files\Microsoft Visual Studio", "C:\Program Files (x86)\Microsoft Visual Studio" -Filter csc.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Roslyn\\csc\.exe$' } | Select-Object -First 1
if (-not $csc) { throw "Roslyn csc.exe not found (install Visual Studio Build Tools)" }
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
$refs = "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Management.dll", "System.Web.Extensions.dll", "System.ServiceProcess.dll", "System.Xml.dll" | ForEach-Object { "/r:$fw\$_" }
$src = (Get-ChildItem "$root\src\*.cs", "$root\src\Dist\*.cs", "$root\src\Ram\*.cs").FullName
# the PawnIO modules (the SMU, SMN registers, the SMBus) go next to the exe
# (only these: the folder is made anew, so files an older build put there, like ZenStates-Core or the scripts, do not ship)
foreach ($old in "$root\bin\ram", "$root\bin\tools") { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
New-Item -ItemType Directory -Force "$root\bin\ram" | Out-Null
Copy-Item "$root\ram\RyzenSMU.bin", "$root\ram\AMDFamily17.bin", "$root\ram\SmbusPIIX4.bin" "$root\bin\ram" -Force
# 21.11: the PawnIO driver's own installer (namazso, signed, unchanged; installer\pawnio), run with -install -silent by
# the setup and by «Компоненты» when the driver is missing
New-Item -ItemType Directory -Force "$root\bin\pawnio" | Out-Null
Copy-Item "$root\installer\pawnio\PawnIO_setup.exe" "$root\bin\pawnio" -Force
# the program's own heavy load for the auto-tune (native\): built again when a source is newer than the dll; without a
# C compiler the dll that is already there is used
$dll = "$root\native\RedTuneStress.dll"
$newer = Get-ChildItem "$root\native\*.c", "$root\native\*.h", "$root\native\*.inl" | Where-Object { -not (Test-Path $dll) -or $_.LastWriteTime -gt (Get-Item $dll).LastWriteTime }
if ($newer) { try { & "$root\native\build.ps1" | Out-Null } catch { if (Test-Path $dll) { Write-Warning "RedTuneStress.dll was not built again: $_" } else { throw } } }
Copy-Item $dll "$root\bin" -Force
# the sound effect Windows loads for the page «Звук» (native\apo\): built and tested again when a source is newer than the dll
$apo = "$root\native\apo\RedTuneApo.dll"
$newerApo = Get-ChildItem "$root\native\apo\*.cpp", "$root\native\apo\*.h", "$root\native\apo\*.def" | Where-Object { -not (Test-Path $apo) -or $_.LastWriteTime -gt (Get-Item $apo).LastWriteTime }
if ($newerApo) { try { & "$root\native\apo\build.ps1" | Out-Null } catch { if (Test-Path $apo) { Write-Warning "RedTuneApo.dll was not built again: $_" } else { throw } } }
New-Item -ItemType Directory -Force "$root\bin\audio" | Out-Null
Copy-Item $apo "$root\bin\audio" -Force
$ico = "$root\redtune.ico"
# built into the exe: the logo and the languages (lang\es.txt … : L.Load finds them as RedTune.lang.<code>.txt)
$res = @("/resource:$root\src\logo.png,RedTune.logo.png") + @(Get-ChildItem "$root\lang\*.txt" | ForEach-Object { "/resource:$($_.FullName),RedTune.lang.$($_.Name)" })

function Compile([string[]]$extra) {
    & $csc.FullName /nologo /target:winexe /platform:x64 /optimize+ /unsafe /langversion:latest /utf8output `
        "/out:$root\bin\RedTune.exe" "/win32manifest:$root\app.manifest" $res $extra $refs $src
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

if (-not (Test-Path $ico)) {
    Compile @()
    # the manifest asks for admin rights; run the icon step through a non-elevated copy without it
    & $csc.FullName /nologo /target:exe /platform:x64 /unsafe /langversion:latest "/out:$root\bin\icongen.exe" $res $refs $src | Out-Null
    & "$root\bin\icongen.exe" --make-icon $ico
    Remove-Item "$root\bin\icongen.exe" -ErrorAction SilentlyContinue
}
Compile @("/win32icon:$ico")
Get-Item "$root\bin\RedTune.exe" | Select-Object Name, Length, LastWriteTime

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
    $v = (Get-Item "$root\bin\RedTune.exe").VersionInfo
    $version = "{0}.{1}.{2}" -f $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart
    & $iscc /Q "/DAppVersion=$version" "$root\installer\RedTune.iss"
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
    Get-Item "$root\dist\RedTune-Setup-$version.exe" | Select-Object Name, Length, LastWriteTime
}
