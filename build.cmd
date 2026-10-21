@echo off
rem Builds single-file, self-contained, portable RestPeach.exe and restpeach-cli.exe into .\dist
setlocal
cd /d "%~dp0"
if exist dist rmdir /s /q dist
rem Always from scratch: a stale incremental Release build can ship without the compiled XAML (the exe dies at startup).
if exist src\RestPeach\obj\Release rmdir /s /q src\RestPeach\obj\Release
dotnet publish src\RestPeach\RestPeach.csproj -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -o dist
if errorlevel 1 exit /b 1
rem The CLI has no UI: trimmed, it is a small exe for CI agents.
dotnet publish src\RestPeach.Cli\RestPeach.Cli.csproj -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:PublishTrimmed=true ^
  -p:TrimMode=partial ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -o dist
if errorlevel 1 exit /b 1
echo.
echo Listo: %~dp0dist\RestPeach.exe y %~dp0dist\restpeach-cli.exe
echo Para modo 100%% portable (datos junto al exe) crea un archivo "portable.txt" al lado del exe.
