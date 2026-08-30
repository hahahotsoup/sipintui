@echo off
rem ---------------------------------------------------------------------------
rem  Installs a newer ConPTY pair (conpty.dll + OpenConsole.exe) into WezTerm so
rem  that Sixel graphics are passed through to the terminal.
rem
rem  WezTerm is installed under "C:\Program Files", so this needs elevation.
rem  Double-click this file and accept the UAC prompt.
rem
rem  The elevated window is kept open (-NoExit) so you can read the result.
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% equ 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -NoExit -File upgrade-wezterm-conpty.ps1
    goto :eof
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath powershell -Verb RunAs -Wait -WorkingDirectory (Get-Location).Path -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','upgrade-wezterm-conpty.ps1'"

echo.
echo If nothing happened, the UAC prompt was dismissed. Run this file again.
echo.
pause
