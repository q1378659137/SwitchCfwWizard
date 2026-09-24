@echo off
rem ===========================================================================
rem  SwitchCFWizard source packaging - one-click launcher (double-click this)
rem
rem  This file MUST stay pure ASCII: cmd.exe decodes a .bat using the console
rem  code page, so a UTF-8 / BOM-prefixed .bat with Chinese text gets executed
rem  as garbage (classic symptom: "'?-" is not recognized).
rem  All Chinese messages live in tools\pack-source.ps1 (UTF-8 with BOM).
rem
rem  Any extra arguments are passed through, e.g.: "pack-source.bat -List"
rem ===========================================================================
setlocal
cd /d "%~dp0"

echo.
echo  == SwitchCFWizard source package ==
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\pack-source.ps1" -OpenOutputDir %*

if errorlevel 1 (
  echo  [!] FAILED - see the messages above
) else (
  echo  [OK] done
)

echo.
pause
