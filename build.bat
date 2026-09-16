@echo off
rem Builds DevTokenMeter.exe using the C# compiler that ships with Windows.
rem No SDK, no toolchain, nothing to install.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe - is .NET Framework 4.x present?
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:"%~dp0DevTokenMeter.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  "%~dp0src\DevTokenMeter.cs" "%~dp0src\App.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built %~dp0DevTokenMeter.exe
