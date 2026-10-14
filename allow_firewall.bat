@echo off
:: Batch script to allow ButterFlow through Windows Defender Firewall
echo ========================================================
echo   Configuring Windows Firewall for ButterFlow
echo ========================================================

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [NOTE] Requesting Administrator Privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c %~fn0' -Verb RunAs"
    exit /b
)

echo Allowing ButterFlow ports through Windows Firewall...

netsh advfirewall firewall delete rule name="ButterFlow TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="ButterFlow UDP" >nul 2>&1

netsh advfirewall firewall add rule name="ButterFlow TCP" dir=in action=allow protocol=TCP localport=38440,38441,42043
netsh advfirewall firewall add rule name="ButterFlow UDP" dir=in action=allow protocol=UDP localport=38445,42042,42044

echo.
echo [SUCCESS] Windows Defender Firewall rules added successfully!
echo ButterFlow can now connect seamlessly without network blocks.
echo.
pause
