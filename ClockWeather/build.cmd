@echo off
rem Rebuilds ClockWeather.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK or Visual Studio needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe under %WINDIR%\Microsoft.NET
  exit /b 1
)
pushd "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:ClockWeather.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll ^
  /r:System.Web.Extensions.dll /r:System.Core.dll ^
  ClockWeather.cs FlightMap.cs
if errorlevel 1 (popd & exit /b 1)
echo Built %CD%\ClockWeather.exe
popd
