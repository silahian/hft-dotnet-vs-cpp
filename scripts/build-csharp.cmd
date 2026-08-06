@echo off
setlocal

rem Builds both C# configurations:
rem   1. JIT   (tiered + dynamic PGO, the .NET default)
rem   2. NativeAOT (ahead-of-time, no JIT at run time)
rem
rem The NativeAOT link step shells out to vswhere.exe, which is not on PATH by
rem default -- without this the ILCompiler link fails with MSB3073.

set "ROOT=%~dp0.."
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

pushd "%ROOT%"

echo [build] JIT
dotnet build ".\src\csharp\Bench.Runner\Bench.Runner.csproj" -c Release -v q --nologo
if errorlevel 1 goto :fail

echo [build] NativeAOT
dotnet publish ".\src\csharp\Bench.Runner\Bench.Runner.csproj" -c Release -r win-x64 -p:PublishAot=true -o ".\build\aot" -v q --nologo
if errorlevel 1 goto :fail

echo [ok] jit: .\src\csharp\Bench.Runner\bin\Release\net10.0\bench-csharp.exe
echo [ok] aot: .\build\aot\bench-csharp.exe
popd
endlocal
exit /b 0

:fail
echo [error] build failed
popd
endlocal
exit /b 1
