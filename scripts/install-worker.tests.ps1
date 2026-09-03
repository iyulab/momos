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

$secure = ConvertTo-SecureString -String 'test-secret' -AsPlainText -Force
$plain = ConvertFrom-SecureStringToPlainText -SecureString $secure
Assert-Equal 'test-secret' $plain 'ConvertFrom-SecureStringToPlainText: round-trip'

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

    # Invoke-WorkerConfiguration: 이미 설정 파일이 있으면 건드리지 않아야 한다 (idempotent)
    $existingContent = '{"existing":"config"}'
    Set-Content -Path $configPath -Value $existingContent -NoNewline
    Invoke-WorkerConfiguration -InstallDir $tmp
    Assert-Equal $existingContent (Get-Content -Path $configPath -Raw) 'Invoke-WorkerConfiguration: 기존 설정 파일을 덮어씀'

    # Invoke-WorkerConfiguration: 비대화형 환경변수로 채워야 한다
    Remove-Item -Path $configPath -Force
    $env:MOMOS_WORKER_HOST_BASEURL = 'https://host.example'
    $env:MOMOS_WORKER_HOST_APIKEY = 'hostkey'
    $env:MOMOS_LLM_GPUSTACK_ENDPOINT = 'https://gpustack.example'
    $env:MOMOS_LLM_GPUSTACK_APIKEY = 'llmkey'
    $env:MOMOS_LLM_GPUSTACK_MODEL = 'model'
    try {
        Invoke-WorkerConfiguration -InstallDir $tmp
        $envConfig = Get-Content $configPath -Raw | ConvertFrom-Json
        Assert-Equal 'https://host.example' $envConfig.Momos.Worker.Host.BaseUrl 'Invoke-WorkerConfiguration: 환경변수 BaseUrl 반영'
    }
    finally {
        Remove-Item Env:\MOMOS_WORKER_HOST_BASEURL, Env:\MOMOS_WORKER_HOST_APIKEY, Env:\MOMOS_LLM_GPUSTACK_ENDPOINT, Env:\MOMOS_LLM_GPUSTACK_APIKEY, Env:\MOMOS_LLM_GPUSTACK_MODEL -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item -Recurse -Force $tmp
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Host "OK: install-worker.ps1 단위 테스트 통과"

# Junction 레이아웃 검증
$tmp7 = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid())
New-Item -ItemType Directory -Force -Path (Join-Path $tmp7 'installs\0.1.0') | Out-Null
Set-Content -Path (Join-Path $tmp7 'installs\0.1.0\Momos.Worker.exe') -Value 'binary'
$currentLink = Join-Path $tmp7 'current'
New-Item -ItemType Junction -Path $currentLink -Target (Join-Path $tmp7 'installs\0.1.0') | Out-Null
if (-not (Test-Path (Join-Path $currentLink 'Momos.Worker.exe'))) { throw 'current junction을 통해 바이너리에 접근 불가' }
Remove-Item -Recurse -Force $tmp7
Write-Host 'OK: install-worker.ps1 레이아웃 테스트 통과'

# Get-ServiceFailureActionsArg: sc.exe가 기대하는 정확한 토큰 형식
$defaultActions = Get-ServiceFailureActionsArg
if ($defaultActions -ne 'restart/5000/restart/5000/restart/30000') {
    throw "Get-ServiceFailureActionsArg 기본값이 예상과 다름: $defaultActions"
}
$customActions = Get-ServiceFailureActionsArg -DelaysMs @(1000, 2000)
if ($customActions -ne 'restart/1000/restart/2000') {
    throw "Get-ServiceFailureActionsArg 커스텀 지연이 예상과 다름: $customActions"
}
Write-Host 'OK: install-worker.ps1 서비스 실패 액션 테스트 통과'
