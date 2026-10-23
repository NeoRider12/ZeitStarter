@echo off
rem Builds ZeitStarter.exe with the C# compiler that ships with Windows (.NET Framework 4)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize /codepage:65001 /out:ZeitStarter.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll ZeitStarter.cs
if "%1"=="" pause
