@echo off
setlocal

rem Emits an MSVC assembly listing with the EXACT same flags as the benchmark build,
rem so the published C++ codegen claims can be checked against real output instead of
rem being asserted. Written to build\asm-<tag>\main.asm
rem
rem   usage: asm-cpp.cmd [2022|18]

set "TOOLSET=%~1"
if "%TOOLSET%"=="" set "TOOLSET=2022"

if /I "%TOOLSET%"=="2022" (
  set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
  set "TAG=msvc1444"
) else (
  set "VCVARS=C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvars64.bat"
  set "TAG=msvc1451"
)

set "ROOT=%~dp0.."
set "SRC=%ROOT%\src\cpp"
set "OUT=%ROOT%\build\asm-%TAG%"
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

call "%VCVARS%" >nul
if not exist "%OUT%" mkdir "%OUT%"

rem Same flags as build-cpp.cmd, minus /GL: whole-program optimization defers codegen
rem to link time and emits no per-TU listing. Everything else is identical.
set "CFLAGS=/nologo /std:c++20 /EHsc /permissive- /Zc:inline /O2 /Oi /Ot /Oy /Gw /GS- /arch:AVX2 /fp:fast /DNDEBUG /MT"

cl %CFLAGS% /DBENCH_FLAGS="\"asm-listing\"" /I"%SRC%\include" "%SRC%\src\main.cpp" /FAcs /Fa"%OUT%\\" /Fo"%OUT%\\" /Fe"%OUT%\asmprobe.exe" >nul
if errorlevel 1 (
  echo [error] compile failed
  exit /b 1
)

echo [ok] %OUT%\main.asm
endlocal
