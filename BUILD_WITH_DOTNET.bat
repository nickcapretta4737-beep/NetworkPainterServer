@echo off
setlocal
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo .NET SDK was not found.
  echo Install the current .NET 8 SDK from Microsoft, then run this file again.
  echo OR build this folder with the included GitHub Actions workflow.
  pause
  exit /b 1
)
dotnet build NetworkPainterServer.csproj -c Release
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  pause
  exit /b 1
)
echo.
echo BUILD SUCCESSFUL
echo DLL: bin\Release\netstandard2.1\NetworkPainterServer.dll
echo Upload that DLL to BepInEx\plugins\NetworkPainterServer\ on the SERVER only.
pause
