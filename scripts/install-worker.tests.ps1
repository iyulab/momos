$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'install-worker.ps1') -TestMode

$failures = @()
function Assert-Equal($expected, $actual, $label) {
    if ($expected -ne $actual) { $script:failures += "FAIL: $label (기대값 '$expected', 실제 '$actual')" }
}
function Assert-True($condition, $label) {
    if (-not $condition) { $script:failures += "FAIL: $label" }
}

# Test-Checksum: 정상/손상 파일 각각 검증
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $filePath = Join-Path $tmp 'file.bin'
    Set-Content -Path $filePath -Value 'hello' -NoNewline
    $hash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash
    $checksumPath = Join-Path $tmp 'file.bin.sha256'
    Set-Content -Path $checksumPath -Value "$hash  file.bin" -NoNewline

    Assert-True (Test-Checksum -FilePath $filePath -ChecksumFilePath $checksumPath) 'Test-Checksum: 올바른 체크섬'

    Set-Content -Path $filePath -Value 'corrupted' -NoNewline
    Assert-True (-not (Test-Checksum -FilePath $filePath -ChecksumFilePath $checksumPath)) 'Test-Checksum: 손상된 파일 거부'

    # Write-WorkerConfig: JSON 구조 검증
    Write-WorkerConfig -Dir $tmp -BaseUrl 'https://host.example' -ApiKey 'hostkey' `
        -GpuStackEndpoint 'https://gpustack.example' -GpuStackApiKey 'llmkey' -GpuStackModel 'qwen3.8-27b'
    $configPath = Join-Path $tmp 'appsettings.Production.json'
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    Assert-Equal 'https://host.example' $config.Momos.Worker.Host.BaseUrl 'Write-WorkerConfig: BaseUrl'
    Assert-Equal 'qwen3.8-27b' $config.Momos.Llm.GpuStack.Model 'Write-WorkerConfig: Model'
}
finally {
    Remove-Item -Recurse -Force $tmp
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Host "OK: install-worker.ps1 단위 테스트 통과"
