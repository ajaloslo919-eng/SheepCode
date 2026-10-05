@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
if defined SHEEPCODE_WINDOWS_SDK (
set "INCLUDE=%SHEEPCODE_WINDOWS_SDK%\c\Include\10.0.26100.0\ucrt;%SHEEPCODE_WINDOWS_SDK%\c\Include\10.0.26100.0\shared;%SHEEPCODE_WINDOWS_SDK%\c\Include\10.0.26100.0\um;%INCLUDE%"
set "LIB=%SHEEPCODE_WINDOWS_SDK%\c\um\x64;%SHEEPCODE_WINDOWS_SDK%\c\ucrt\x64;%LIB%"
set "PATH=%SHEEPCODE_WINDOWS_SDK%\c\bin\10.0.26100.0\x64;%PATH%"
)
cd /d "%~dp0"
rc /nologo launcher.rc
if errorlevel 1 exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE launcher.cpp launcher.res /Fe:SheepCode.exe /link /SUBSYSTEM:WINDOWS user32.lib
if errorlevel 1 exit /b 1
if "%~1"=="launcher" exit /b 0
if not exist bootstrap.rc exit /b 0
rc /nologo bootstrap.rc
if errorlevel 1 exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++17 /DUNICODE /D_UNICODE bootstrap.cpp bootstrap.res /Fe:SheepCode-Setup.exe /link /SUBSYSTEM:WINDOWS user32.lib ole32.lib
exit /b %errorlevel%
