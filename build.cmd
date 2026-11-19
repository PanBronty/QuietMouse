@echo off
setlocal
cd /d "%~dp0"
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "vs=%%i"
if not defined vs exit /b 1
call "%vs%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist portable mkdir portable
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /codepage:65001 /win32icon:QuietMouse.ico "/resource:Getting started.txt,QuietMouse.Guide" /out:portable\QuietMouse.Settings.exe Settings.cs || exit /b 1
rc /nologo QuietMouse.rc || exit /b 1
cl /nologo /std:c++17 /W4 /O2 /MT /utf-8 /DUNICODE /D_UNICODE QuietMouse.cpp QuietMouse.res user32.lib shell32.lib advapi32.lib /link /SUBSYSTEM:WINDOWS /OUT:portable\QuietMouse.exe || exit /b 1
del QuietMouse.obj QuietMouse.res
copy /y "Getting started.txt" portable\ >nul || exit /b 1
copy /y LICENSE portable\ >nul || exit /b 1
tar.exe -a -cf QuietMouse-Windows-x64.zip -C portable QuietMouse.exe QuietMouse.Settings.exe "Getting started.txt" LICENSE || exit /b 1
