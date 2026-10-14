# ButterFlow Native Input Hook Compiler
$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $cscPath)) {
    $cscPath = (Get-Command csc.exe -ErrorAction SilentlyContinue).Source
}

if (-not $cscPath) {
    Write-Error "Microsoft Visual C# Compiler (csc.exe) not found."
    exit 1
}

$source = Join-Path $PSScriptRoot "ButterFlowInputHook.cs"
$output = Join-Path $PSScriptRoot "ButterFlowNative.exe"

Write-Host "Compiling ButterFlow Native Hook using $cscPath..."
& $cscPath /nologo /target:exe /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:$output $source

if ($LASTEXITCODE -eq 0 -and (Test-Path $output)) {
    Write-Host "Success: $output compiled successfully." -ForegroundColor Green
} else {
    Write-Error "Failed to compile ButterFlowNative.exe"
    exit 1
}
