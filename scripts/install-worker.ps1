[CmdletBinding()]
param(
    [switch]$TestMode,
    [switch]$SkipServiceInstall
)

$ErrorActionPreference = 'Stop'

$Repo = 'iyulab/momos'
$Rid = 'win-x64'

function ConvertFrom-SecureStringToPlainText {
    param([System.Security.SecureString]$SecureString)
    [System.Net.NetworkCredential]::new('', $SecureString).Password
}

function Resolve-ReleaseTag {
    param([string]$Version)
    if ($Version -ne 'latest') { return "worker-v$Version" }
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases"
    $match = $releases | Where-Object { $_.tag_name -like 'worker-v*' } | Select-Object -First 1
    if (-not $match) { throw "worker-v* 태그를 가진 릴리스를 찾을 수 없습니다 (repo=$Repo)" }
    return $match.tag_name
}

function Resolve-AssetUrl {
    param([string]$Tag, [string]$AssetName)
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag"
    $asset = $release.assets | Where-Object { $_.name -eq $AssetName }
    if (-not $asset) { throw "릴리스 '$Tag'에서 자산 '$AssetName'을 찾을 수 없습니다" }
    return $asset.browser_download_url
}

function Test-Checksum {
    param([string]$FilePath, [string]$ChecksumFilePath)
    $expected = (Get-Content $ChecksumFilePath -Raw).Trim().Split(' ')[0].ToLower()
    $actual = (Get-FileHash -Path $FilePath -Algorithm SHA256).Hash.ToLower()
    return $expected -eq $actual
}

function Write-WorkerConfig {
    param(
        [string]$Dir, [string]$BaseUrl, [string]$ApiKey,
        [string]$GpuStackEndpoint, [string]$GpuStackApiKey, [string]$GpuStackModel
    )
    $config = [ordered]@{
        Momos = [ordered]@{
            Worker = [ordered]@{ Host = [ordered]@{ BaseUrl = $BaseUrl; ApiKey = $ApiKey } }
            Llm    = [ordered]@{ GpuStack = [ordered]@{ Endpoint = $GpuStackEndpoint; ApiKey = $GpuStackApiKey; Model = $GpuStackModel } }
        }
    }
    $path = Join-Path $Dir 'appsettings.Production.json'
    $config | ConvertTo-Json -Depth 5 | Set-Content -Path $path -Encoding utf8

    # 현재 사용자만 접근 가능하도록 상속을 끊고 전용 권한 부여 (Linux 쪽 chmod 600에 상응)
    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($true, $false)
    $currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($currentIdentity, 'FullControl', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -Path $path -AclObject $acl
}

function Invoke-WorkerConfiguration {
    param([string]$InstallDir)
    $configPath = Join-Path $InstallDir 'appsettings.Production.json'
    if (Test-Path $configPath) {
        Write-Host "기존 설정 파일을 유지합니다: $configPath"
        return
    }

    $baseUrl = $env:MOMOS_WORKER_HOST_BASEURL
    $apiKey = $env:MOMOS_WORKER_HOST_APIKEY
    $gpuEndpoint = $env:MOMOS_LLM_GPUSTACK_ENDPOINT
    $gpuApiKey = $env:MOMOS_LLM_GPUSTACK_APIKEY
    $gpuModel = $env:MOMOS_LLM_GPUSTACK_MODEL

    if ([Environment]::UserInteractive -and -not $baseUrl) {
        $baseUrl = Read-Host 'Momos Host BaseUrl'
        $apiKey = ConvertFrom-SecureStringToPlainText (Read-Host 'Momos Worker API Key' -AsSecureString)
        $gpuEndpoint = Read-Host 'GPUStack Endpoint'
        $gpuApiKey = ConvertFrom-SecureStringToPlainText (Read-Host 'GPUStack API Key' -AsSecureString)
        $gpuModel = Read-Host 'GPUStack Model'
    }

    Write-WorkerConfig -Dir $InstallDir -BaseUrl $baseUrl -ApiKey $apiKey `
        -GpuStackEndpoint $gpuEndpoint -GpuStackApiKey $gpuApiKey -GpuStackModel $gpuModel

    if (-not $baseUrl -or -not $apiKey -or -not $gpuEndpoint -or -not $gpuApiKey -or -not $gpuModel) {
        Write-Host "일부 설정값이 비어 있습니다 — $configPath 를 직접 채운 뒤 실행하세요."
    }
}

function Test-IsElevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# "restart/<ms>/restart/<ms>/restart/<ms>" — the exact token `sc.exe failure ... actions=` expects.
# Escalating delays (not a flat retry) give a transient failure (a lock that clears in seconds) a
# fast recovery while still backing off if the process is failing repeatedly on start.
function Get-ServiceFailureActionsArg {
    param([int[]]$DelaysMs = @(5000, 5000, 30000))
    ($DelaysMs | ForEach-Object { "restart/$_" }) -join '/'
}

function Install-WorkerService {
    param([string]$InstallDir, [string]$ServiceName = 'MomosWorker')

    if (-not (Test-IsElevated)) {
        Write-Host "관리자 권한이 없어 Windows 서비스 등록을 건너뜁니다 — 관리자 PowerShell에서 이 스크립트를 다시 실행하면 상시 서비스로 등록됩니다."
        return
    }

    # 'current'는 self-update가 재배치하는 심볼릭 링크/junction이다 — 서비스는 이 안정 경로를
    # 가리키므로, self-update가 버전을 바꿔도 서비스 등록 자체를 다시 할 필요가 없다(다음 재시작부터
    # 새 버전이 뜬다). WorkerSelfUpdater.UpdateAsync가 Environment.Exit로 프로세스를 끝내면 아래
    # sc.exe failure가 건 복구 액션이 SCM에서 재시작을 트리거한다 — Environment.Exit이 SCM에는
    # "정상 종료"가 아니라 실제 종료로 보고된다는 것을 이 머신에서 별도 테스트 서비스로 직접
    # 검증했다(문서화된 사례는 호스트가 가로채는 미처리 예외에 대한 것이라 이 경로와는 다름).
    $exePath = Join-Path $InstallDir 'current\Momos.Worker.exe'

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "서비스가 이미 등록돼 있습니다: $ServiceName (재등록하지 않음 — 'current' 링크만 갱신하면 다음 재시작부터 새 버전이 실행됨)"
    }
    else {
        New-Service -Name $ServiceName -BinaryPathName $exePath -DisplayName 'Momos Worker' `
            -Description 'Momos inspection Worker — Momos Host의 검사 신청을 pull 방식으로 실행한다.' `
            -StartupType Automatic | Out-Null
        Write-Host "서비스 등록 완료: $ServiceName"
    }

    # reset= 86400: 24시간 동안 추가 실패가 없으면 실패 카운트를 리셋 — 오래전 실패 하나 때문에
    # 다음 실패가 곧바로 "3번째 실패" 취급을 받아 백오프가 필요 이상으로 길어지는 걸 막는다.
    & sc.exe failure $ServiceName reset= 86400 actions= (Get-ServiceFailureActionsArg) | Out-Null

    if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
        Start-Service -Name $ServiceName
    }
    Write-Host "서비스 실행 중: $ServiceName"
}

function Install-MomosWorker {
    $installDir = if ($env:MOMOS_WORKER_INSTALL_DIR) { $env:MOMOS_WORKER_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'MomosWorker' }
    $version = if ($env:MOMOS_WORKER_VERSION) { $env:MOMOS_WORKER_VERSION } else { 'latest' }

    $tag = Resolve-ReleaseTag -Version $version
    $assetName = "momos-worker-$Rid.zip"
    $zipUrl = Resolve-AssetUrl -Tag $tag -AssetName $assetName
    $checksumUrl = Resolve-AssetUrl -Tag $tag -AssetName "$assetName.sha256"

    $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmpDir | Out-Null
    try {
        $zipPath = Join-Path $tmpDir 'archive.zip'
        $checksumPath = Join-Path $tmpDir 'archive.zip.sha256'
        Write-Host "다운로드 중: $zipUrl"
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
        Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath

        if (-not (Test-Checksum -FilePath $zipPath -ChecksumFilePath $checksumPath)) {
            throw '체크섬이 일치하지 않습니다 — 설치를 중단합니다.'
        }

        $versionString = $tag -replace '^worker-v', ''
        $versionDir = Join-Path (Join-Path $installDir 'installs') $versionString
        New-Item -ItemType Directory -Force -Path $versionDir | Out-Null
        Expand-Archive -Path $zipPath -DestinationPath $versionDir -Force

        $currentLink = Join-Path $installDir 'current'
        if (Test-Path $currentLink) { Remove-Item $currentLink -Force }
        New-Item -ItemType Junction -Path $currentLink -Target $versionDir | Out-Null
    }
    finally {
        Remove-Item -Recurse -Force $tmpDir
    }

    Invoke-WorkerConfiguration -InstallDir $installDir

    if ($SkipServiceInstall -or $env:MOMOS_WORKER_SKIP_SERVICE_INSTALL) {
        Write-Host "서비스 등록을 건너뜁니다(-SkipServiceInstall / MOMOS_WORKER_SKIP_SERVICE_INSTALL) — 실행: $installDir\current\Momos.Worker.exe"
    }
    else {
        Install-WorkerService -InstallDir $installDir
    }

    Write-Host "설치 완료: $installDir"
}

if (-not $TestMode) {
    Install-MomosWorker
}
