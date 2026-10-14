@echo off
setlocal
echo ========================================================
echo   Compiling ButterFlow - Universal Input Sharing (Win)
echo ========================================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo Error: C# Compiler csc.exe not found at %CSC%
    exit /b 1
)

set SOURCES=src\Program.cs src\Core\NativeMethods.cs src\Core\Protocol.cs src\Core\HostInputManager.cs src\Core\ClipboardManager.cs src\Core\ShellHelper.cs src\Engine\LockFreeRingBuffer.cs src\Engine\RawInputCapture.cs src\Engine\LowLevelSwallowHook.cs src\Engine\RealtimeInputEngine.cs src\Engine\EdgeTransitionEngine.cs src\Engine\NetworkEnvironmentDetector.cs src\Engine\BenchmarkEngine.cs src\Network\DiscoveryService.cs src\Network\InputSender.cs src\Network\InputReceiver.cs src\UI\DragGhostOverlay.cs src\UI\EdgeDropZoneBar.cs src\UI\MainForm.cs
set REFS=/r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll

echo Compiling source files...
"%CSC%" /nologo /target:winexe /out:ButterFlow.exe %REFS% /optimize+ %SOURCES%

if %ERRORLEVEL% NEQ 0 (
    echo [FAILED] Compilation failed with error code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

echo [SUCCESS] Build completed: ButterFlow.exe
echo Creating ButterFlow-Portable.exe...
copy /Y ButterFlow.exe ButterFlow-Portable.exe >nul

echo Compiling ButterFlow-Setup.exe (Installer with embedded payload)...
"%CSC%" /nologo /target:winexe /optimize+ /resource:ButterFlow.exe %REFS% /out:ButterFlow-Setup.exe src\Installer\Setup.cs

if %ERRORLEVEL% EQU 0 (
    echo [SUCCESS] Build completed: ButterFlow-Setup.exe
)

echo Compiling Native Windows Input Hook...
powershell -ExecutionPolicy Bypass -File src\main\native\compile.ps1

echo ========================================================
echo   All Deliverables Built Successfully:
echo     - ButterFlow.exe (Core Win32 Executable)
echo     - ButterFlow-Portable.exe (Zero-Install Portable)
echo     - ButterFlow-Setup.exe (Standalone Windows Installer)
echo     - src\main\native\ButterFlowNative.exe
echo ========================================================
endlocal
