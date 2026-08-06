@echo off
setlocal

rem Builds the C++ benchmark with MSVC.
rem   usage: build-cpp.cmd [2022|18]
rem     2022 -> MSVC 14.44 from VS 2022 Build Tools (stable/GA)
rem     18   -> MSVC 14.51 from VS 18 Insiders     (newest available)
rem
rem Both are built and reported. The published C++ headline is whichever is faster,
rem so the comparison never rests on having handicapped the C++ compiler.
rem
rem Flags are echoed into the results JSON via BENCH_FLAGS, so every published
rem number carries its own build provenance.

set "TOOLSET=%~1"
if "%TOOLSET%"=="" set "TOOLSET=2022"

if /I "%TOOLSET%"=="2022" (
  set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
  set "TAG=msvc1444"
) else if /I "%TOOLSET%"=="18" (
  set "VCVARS=C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Auxiliary\Build\vcvars64.bat"
  set "TAG=msvc1451"
) else (
  echo [error] unknown toolset "%TOOLSET%" -- expected 2022 or 18
  exit /b 1
)

set "ROOT=%~dp0.."
set "SRC=%ROOT%\src\cpp"
set "OUT=%ROOT%\build\cpp-%TAG%"

rem vswhere lives here and is NOT on PATH by default; vcvars64 and the NativeAOT
rem link step both shell out to it.
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

if not exist "%VCVARS%" (
  echo [error] vcvars64.bat not found at "%VCVARS%"
  exit /b 1
)
call "%VCVARS%" >nul
if errorlevel 1 (
  echo [error] vcvars64.bat failed
  exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"

set "CFLAGS=/nologo /std:c++20 /EHsc /permissive- /Zc:inline /O2 /Oi /Ot /Oy /GL /Gw /GS- /arch:AVX2 /fp:fast /DNDEBUG /MT"
set "LFLAGS=/LTCG /OPT:REF /OPT:ICF"

echo [build %TAG%] cl %CFLAGS%
cl %CFLAGS% /DBENCH_FLAGS="\"%CFLAGS% ^| link %LFLAGS%\"" /I"%SRC%\include" "%SRC%\src\main.cpp" /Fo"%OUT%\\" /Fe"%OUT%\bench-cpp.exe" /link %LFLAGS%
if errorlevel 1 (
  echo [error] compile failed
  exit /b 1
)

echo [ok] %OUT%\bench-cpp.exe
endlocal
