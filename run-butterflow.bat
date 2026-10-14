@echo off
title ButterFlow Launcher
echo ====================================================
echo             BUTTERFLOW DESKTOP KVM ENGINE
echo ====================================================

:: Check if ButterFlowNative.exe exists, if not compile it
if not exist "src\main\native\ButterFlowNative.exe" (
    echo [Launcher] Compiling native Windows input hook...
    powershell -ExecutionPolicy Bypass -File "src\main\native\compile.ps1"
    if errorlevel 1 (
        echo [Launcher] Native compilation failed.
        pause
        exit /b 1
    )
)

echo [Launcher] Starting ButterFlow Application...
npm start
