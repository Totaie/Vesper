@echo off
rem Builds the portable package's launcher (a 32-bit x86 Vesper.exe) with Visual Studio's C++ build
rem tools. Usage: build.cmd <output folder> <version, as 1.2.3>
setlocal
if "%~2"=="" (echo usage: build.cmd ^<output folder^> ^<version^> & exit /b 2)
set "OUT=%~f1"
set "VERSION=%~2"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VS="
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo Visual Studio's C++ build tools aren't installed. & exit /b 1)
rem vcvarsall looks for vswhere on PATH too.
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
> "%OUT%\version.h" (
  echo #define VESPER_VERSION_STRING "%VERSION%"
  echo #define VESPER_VERSION_NUMBERS %VERSION:.=,%,0
)
rem The resource script names its icon and manifest relative to this folder.
pushd "%~dp0"
rc /nologo /i "%OUT%" /fo "%OUT%\Launcher.res" Launcher.rc || (popd & exit /b 1)
cl /nologo /W4 /WX /O1 /MT /GS /guard:cf /DUNICODE /D_UNICODE /Fo"%OUT%\Launcher.obj" /Fe"%OUT%\Vesper.exe" Launcher.c "%OUT%\Launcher.res" /link /SUBSYSTEM:WINDOWS /MANIFEST:NO /GUARD:CF user32.lib || (popd & exit /b 1)
popd
