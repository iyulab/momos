# Momos

정적 분석이 잡지 못하는 실행 환경의 문제(UX 흐름, 실행 중 결함)를 자율 에이전트가 직접 상호작용하며 찾아내고, 그 검사의 바탕이 되는 프로젝트 설계 이해를 근거 등급이 붙은 모델로 개발자에게 돌려주는 도구.

## 대상

CI/CD 파이프라인 안에서, 개별 프로젝트 개발자가 릴리스 전 셀프 QC로 사용한다.

## 무엇이 아닌가

- 코드를 자동으로 고치는 도구가 아니다 — 발견·보고까지만 한다.
- 공식 인증기관(GS인증, CC인증 등) 역할을 대행하지 않는다.
- 요구사항의 사업적 타당성을 판단하지 않는다.
- 자체 이슈 트래커가 아니다 — 후속 조치는 [docket](https://github.com/iyulab/docket) 또는 GitHub Issues로 넘어간다.

## 현재 상태

Walking Skeleton 구현 진행 중. `.NET` solution(`Momos.Host`/`Momos.Worker`)이 존재하며, 둘은 별도 프로세스로 배포된다 — Worker가 Host를 poll해 대기 중인 검사 신청서를 가져와 실행하고 결과서를 제출한다. Host의 프로젝트 등록·검사 신청/결과 조회 API가 구현되어 있다: `POST /projects`, `GET /projects/{id}`, `POST /projects/{id}/inspection-requests`, `GET /inspection-requests/{id}`, `GET /inspection-requests/{id}/report`(자세한 내용은 [통합 계약](docs/integration-contract.md) 참고). 신청서는 제출 후 실제로 Worker에 의해 실행된다 — 대상 리포를 체크아웃하고, 샌드박스 환경에서 명령을 실행하며, 실제로 재현한 결함이 있으면 근거(명령 출력)와 함께 보고하고, 없으면 정직하게 지적 0건으로 완료한다. 실제 대상 리포에 대한 엔드투엔드 실행(Host+Worker+LLM+실행 샌드박스)이 검증됐다 — 남은 것은 지적 품질을 평가할 비교 방법론을 다듬는 것이다.

프로젝트 설계 이해 모델도 첫 단계가 구현되어 있다: `POST /projects/{id}/analysis-requests`로 분석을 요청하면 Worker가 대상 리포의 .NET 프로젝트 파일에서 구조(프로젝트)와 프로젝트 참조 관계를, git 이력에서 구성 요소별 변경 이력(커밋 수·최근 변경·이름을 언급한 커밋)을 결정적으로 추출하고, 그 위에서 언어 모델 패스가 매뉴얼의 장 구성과 장별 진술·요소(흐름·불변식·패턴·결정·의도)를 제안한다 — 분석한 커밋에서 근거가 확인된 제안만 모델에 들어가고(`Synthesized`로 표시), 나머지는 기각 건수로 기록된다. 모델은 `GET /projects/{id}/model`로, 개발자용 딥 리포트(마크다운 문서 트리)는 `GET /projects/{id}/model/report`로 조회하고, 각 진술은 `POST /projects/{id}/model/claims/{claimKey}/corrections`로 확인·반박·교정할 수 있다. 모델 진술과 교정은 검사 에이전트가 지식 조회로 읽는 맥락이 된다. 증분 분석은 아직 구현되지 않았다 — 자세한 내용은 [통합 계약](docs/integration-contract.md#프로젝트-모델) 참고.

## 설정

`Momos.Host`는 `ConnectionStrings:MomosDb`(PostgreSQL 연결 문자열) 하나만 있으면 바로 뜬다 — `localhost`를 가리키는 Development 기본값이 `appsettings.Development.json`에 커밋돼 있어(아래 "로컬 데이터베이스" 참고) 로컬에 해당 컨테이너만 띄우면 별도 설정 없이 동작한다. Production 환경에서는 이 기본값이 적용되지 않으므로 반드시 환경 변수/시크릿으로 채워야 한다.

`Momos.Worker`는 에이전트 루프가 쓸 LLM 프로바이더 설정이 **필수**다. 현재 지원 프로바이더는 GPUStack(OpenAI 호환 self-hosted 엔드포인트)이며, 아래 세 값을 채우지 않으면 부팅 시 `OptionsValidationException`으로 즉시 실패한다:

```json
{
  "Momos": {
    "Llm": {
      "GpuStack": {
        "Endpoint": "<GPUStack 엔드포인트 URL>",
        "ApiKey": "<GPUStack API 키>",
        "Model": "<모델 이름, 예: qwen3.8-27b>"
      }
    }
  }
}
```

`appsettings.Development.json`에 커밋하지 말고 `dotnet user-secrets` 또는 환경 변수(`Momos__Llm__GpuStack__Endpoint` 등)로 주입한다.

분석 요청도 이 LLM을 쓴다 — 결정적 추출 뒤에 언어 모델 패스(장 구성을 정하는 개관 1회 + 장마다 1회)가 돈다. 한도는 `Momos:Worker:Analysis`에서 바꾼다: `Synthesis`(기본 `true`, `false`면 결정적 모델만 제출 — LLM 비용 없음), `Model`(비우면 위 GPUStack 모델), `MaxChapters`(기본 8), `MaxCommandOutputChars`(에이전트가 명령 하나에서 돌려받는 출력 글자 수, 기본 8000 — 넘으면 잘리고 좁혀 읽으라는 안내가 붙는다), `MaxContextTokens`(모델의 컨텍스트 창 토큰 수, 기본 32000 — 자체 호스팅 모델은 에이전트 라이브러리의 카탈로그에 없어 직접 알려 줘야 한다), `ProtectedToolRounds`(원문 그대로 두는 최근 도구 호출 라운드 수, 기본 4 — 그보다 오래된 도구 출력은 모델 호출마다 짧은 자리표시로 가려진다), `ReadsBeforeNudge`(제안 없이 연달아 읽기만 한 호출 수, 기본 6 — 여기에 닿으면 읽은 것부터 제안하라는 안내가 도구 출력에 붙는다. 예산으로 끊긴 패스는 제안된 것만 남기 때문이다), `MaxOverviewTokens`(개관 패스 하나의 토큰, 기본 400000 — 리포 전체를 훑으므로 장보다 크게 두며 총 한도에 포함된다), `MaxChapterTokens`(장 패스 하나의 토큰, 기본 500000), `MaxTotalTokens`(분석 하나의 토큰, 기본 4400000 — 개관 + 장 수 × 장당 한도와 같아 패스별 한도가 총량에 먼저 막히지 않는다), `MaxDuration`(기본 `00:45:00` — clone·추출 2분 + 개관 3분 + 8장 × 5분). 시간은 남은 장들이 나눠 쓴다 — 장마다 남은 시간의 제 몫만 쓸 수 있어 앞 장이 뒤 장의 시간을 다 가져가지 못한다. 토큰이나 시간 예산이 떨어진 장은 이미 검증된 내용을 부분 장으로 남기고(분석 범위에 「partial」로 기록), 모델 호출이 실패한 장은 통째로 버린다. 개관도 같다 — 예산으로 끊겨도 이미 검증된 장 계획은 유지하고 장 패스를 진행하며(「partial」로 기록), 모델 호출이 실패하거나 장을 하나도 계획하지 못했으면 결정적 모델만 남는다. 장 계획이 `MaxChapters`에 차면 에이전트에게 더 제안하지 말고 개관을 마치라고 알린다. 패스마다 시작과 끝에 로그 한 줄씩(시간·토큰 몫, 걸린 시간, 쓴 토큰, 도구별 호출 수와 실패 수, 기각 수, 제안 재촉 횟수)을 남긴다. 장 패스가 제안했지만 장에 배치하지 않은 진술·요소는 그 장이 커밋될 때 제안 순서대로 장 끝에 배치된다(예산으로 끊긴 부분 장도 같다). 어느 쪽이든 분석은 완료되며, 부분이거나 잃은 장과 이유는 분석 범위에 남는다. ⚠ `MaxDuration`은 Host의 `Momos:Host:InspectionClaim:ReclaimTimeout`(기본 60분)보다 15분 이상 짧게 둔다 — 그보다 오래 걸리는 요청은 Host가 버려진 것으로 보고 다른 Worker에 다시 넘긴다. 반대로 `ReclaimTimeout`은 `MaxDuration`에 15분 여유를 더한 값 이상으로 둔다(가장 긴 요청이 분석이므로). 두 프로세스는 서로의 설정을 읽지 못해 이 결합은 설정으로만 지켜진다. 기본값의 근거: 장 패스 하나가 시간과 토큰 몫에 동시에 닿은 지점(약 145초·25만 토큰)을 측정했고, 기본값은 장마다 그 약 2배를 준다 — 그래서 토큰 한도를 올린 만큼 분석 한 번의 LLM 비용도 오른다(총한도 200만 → 440만 토큰). 실측 전 추정이며 다음 실측으로 보정한다.

Worker→Host 통신도 인증이 **필수**다. Host는 `Momos:Host:WorkerAuth:ApiKey`, Worker는 그와 동일한 값을 `Momos:Worker:Host:ApiKey`에 채워야 하며, 둘 다 부팅 시 `OptionsValidationException`으로 검증한다. Worker는 이 값을 매 요청 `Authorization: Bearer {ApiKey}` 헤더로 보내고, Host는 `claim-next`/`report`/`fail`과 프로젝트 모델 제출(`POST /analysis-requests/{id}/model`) 엔드포인트에서만 이를 검사한다(프로젝트 등록·조회 등 나머지 API는 별개 통합 표면이라 대상이 아니다). GPUStack 설정과 마찬가지로 커밋하지 말고 환경 변수(`Momos__Host__WorkerAuth__ApiKey`, `Momos__Worker__Host__ApiKey`)로 주입한다.

Host는 결과지 언어의 기본값을 `Momos:Host:Reporting:DefaultLanguage`(기본 `en`, 지원 `en`·`ko`, 환경 변수 `Momos__Host__Reporting__DefaultLanguage`)로 정한다 — 검진 요청과 프로젝트에 언어가 없을 때 쓰이며, 지원하지 않는 값이면 기동 시 검증에서 실패한다.

Host는 프로젝트 지식 레이어(등록 문서·과거 지적사항·프로젝트 모델 진술을 색인해 Worker의 에이전트 루프가 검색하는 RAG)도 갖고 있다. `Momos:Host:Knowledge:ConnectionString`(PostgreSQL, pgvector 확장 필요)이 필수이며, Host는 기동할 때 이 저장소를 초기화한다 — DB에 닿지 않거나, pgvector가 없거나, 테이블이 다른 임베딩 크기로 만들어져 있으면 그 이유와 설정 이름을 담아 **기동이 실패한다**. 임베딩은 `Momos:Host:Knowledge:EmbeddingEndpoint`/`EmbeddingApiKey`/`EmbeddingModel`(기본 `qwen3-embedding-0.6b`)/`EmbeddingDimension`(기본 `1024`)을 GPUStack 값으로 채우면 실제 의미 기반 검색이 동작한다. `EmbeddingEndpoint`는 Development 밖에서 필수다 — 비워 두면 의미 없는 벡터를 반환하는 인메모리 임베더가 쓰이므로, Development에서는 경고만 남기고 그 밖의 환경에서는 기동이 실패한다. `EmbeddingApiKey`만 있고 엔드포인트가 없는 설정도 기동 시 거부된다.

### 로컬 데이터베이스

`Momos.Host`는 PostgreSQL을 사용한다 — 로컬 개발 시 다음처럼 컨테이너 하나로 기동할 수 있다:

```bash
docker run -d --name momos-pg-dev -e POSTGRES_PASSWORD=devpw -e POSTGRES_DB=momos -p 5432:5432 postgres:16-alpine
```

지식 레이어(`Momos:Host:Knowledge:ConnectionString`)는 pgvector 확장이 필요해 별도 이미지가 필요하다:

```bash
docker run -d --name momos-pg-knowledge-dev -e POSTGRES_PASSWORD=devpw -e POSTGRES_DB=momos_knowledge -p 5433:5432 pgvector/pgvector:pg16
```

테스트 스위트는 `Testcontainers.PostgreSql`로 컨테이너를 직접 관리하므로 위 수동 기동은 앱을 직접 `dotnet run`으로 띄울 때만 필요하다(Docker 데몬은 테스트에도 필요).

컨테이너를 띄운 뒤에는 마이그레이션을 수동으로 적용해야 한다 — 마이그레이션은 앱 시작 시점에 실행되지 않으므로, 갓 띄운 PostgreSQL은 스키마가 없는 빈 데이터베이스다:

```bash
dotnet tool restore && dotnet ef database update --project src/Momos.Host
```

## Worker 설치

> **첫 릴리스는 아직 게시되지 않았다.** 이 절이 안내하는 설치 스크립트는 GitHub Releases에서
> 바이너리를 내려받으므로, 릴리스가 하나도 없는 지금 실행하면 내려받을 대상을 찾지 못하고 실패한다.
> 그때까지는 아래 「소스에서 빌드」를 쓴다.

릴리스가 게시된 뒤에는 [Releases](https://github.com/iyulab/momos/releases)에 올라오는 self-contained 바이너리와 설치 스크립트를 쓴다 — .NET 런타임을 미리 설치할 필요가 없다.

### 소스에서 빌드 (릴리스 게시 전)

```bash
git clone https://github.com/iyulab/momos.git
cd momos
dotnet publish src/Momos.Worker -c Release -o <설치할 경로>
```

이 경로는 릴리스 바이너리와 달리 self-contained가 아니라 빌드에 .NET SDK가, 실행에 같은 세대의 .NET 런타임이 필요하다(`global.json`이 고정한 SDK 버전을 따른다).

아래 설치 스크립트가 물어보는 값들은 이 경우 Worker의 설정 파일이나 같은 이름의 환경변수로 직접 넣는다.

### 설치 스크립트 (릴리스 게시 후)

**Linux (x86_64):**
```bash
curl -fsSL https://raw.githubusercontent.com/iyulab/momos/main/scripts/install-worker.sh | bash
```

**Windows (PowerShell 7+):**
```powershell
iwr https://raw.githubusercontent.com/iyulab/momos/main/scripts/install-worker.ps1 | iex
```

설치 중 다음 다섯 값을 물어본다(대화형 셸이 아니면 아래 환경변수로 미리 채워둘 수 있다):

| 값 | 대화형 프롬프트 | 비대화형 환경변수 |
|---|---|---|
| Worker→Host BaseUrl | `Momos Host BaseUrl` | `MOMOS_WORKER_HOST_BASEURL` |
| Worker→Host API Key | `Momos Worker API Key` | `MOMOS_WORKER_HOST_APIKEY` |
| GPUStack Endpoint | `GPUStack Endpoint` | `MOMOS_LLM_GPUSTACK_ENDPOINT` |
| GPUStack API Key | `GPUStack API Key` | `MOMOS_LLM_GPUSTACK_APIKEY` |
| GPUStack Model | `GPUStack Model` | `MOMOS_LLM_GPUSTACK_MODEL` |

값은 설치 디렉터리의 `appsettings.Production.json`에 기록된다(소유자 전용 권한). 기본 설치 경로는 Linux `~/.local/share/momos-worker`, Windows `%LOCALAPPDATA%\MomosWorker`이며 `MOMOS_WORKER_INSTALL_DIR` 환경변수로 바꿀 수 있다. 특정 버전을 설치하려면 `MOMOS_WORKER_VERSION=0.1.0`처럼 지정한다(기본은 최신 릴리스).

바이너리는 설치 경로의 `installs/<버전>/` 아래에 풀리고, `current`가 그 디렉터리를 가리킨다(Linux는 심볼릭 링크, Windows는 junction). 실행 파일은 `<설치경로>/current/Momos.Worker`(Windows는 `Momos.Worker.exe`)다. 재실행하면 기존 설정 파일은 그대로 둔 채 바이너리만 최신으로 교체한다.

스크립트는 Worker를 OS 서비스로도 등록한다 — 아래 자체 업데이트가 동작하려면 종료된 프로세스를 다시 띄워 줄 감시자가 필요하기 때문이다. 두 플랫폼의 기본값은 의도적으로 다르다:

- **Windows** — 기본으로 등록한다(끄려면 `MOMOS_WORKER_SKIP_SERVICE_INSTALL=1`). 관리자 권한 PowerShell에서만 등록되며, 권한이 없으면 등록을 건너뛰고 설치는 계속된다. 서비스 이름은 `MomosWorker`, 실행 계정은 LocalSystem, 시작 유형은 자동이고, 비정상 종료 시 5초·5초·30초 간격으로 재시작한다. 설정 파일은 설치한 사용자 전용 권한으로 기록되므로 스크립트가 LocalSystem에 읽기 권한을 따로 부여한다 — 서비스 계정을 다른 계정으로 바꾸면 그 계정에도 같은 권한을 줘야 한다.
- **Linux** — 기본으로는 등록하지 않는다(`MOMOS_WORKER_INSTALL_SERVICE=1`로 옵트인). `curl | bash` 같은 무인 설치에서 `sudo` 프롬프트가 설치를 멈추게 하지 않기 위해서다. 옵트인하면 root 또는 `sudo`로 `/etc/systemd/system/momos-worker.service`(`Restart=always`, 설치한 사용자로 실행)를 만들고 활성화·시작한다.

### 비공개 리포

Worker는 자격증명을 따로 저장하지 않는다. 대상 리포는 샌드박스 안에서 clone되는데, 운영자가 허용한 리포면 Worker가
**자기가 실행되는 계정의 git 설정**(`git credential fill` — 그 계정에 구성된 credential helper)에 그 호스트의 자격증명을
묻고, 답이 있으면 clone 명령 하나에만 환경변수로 넘긴다. 허용 목록은 기본으로 비어 있다 — 리포 URL은 프로젝트를 등록한
쪽이 정하므로, 목록 없이 자격증명을 넘기면 프로젝트를 등록할 수 있는 누구든 Worker 계정이 읽을 수 있는 리포를 읽게 만들 수
있기 때문이다:

```json
{
  "Momos": {
    "Worker": {
      "Checkout": {
        "CredentialedRepositories": [ "https://github.com/acme/", "https://github.com/other/app" ]
      }
    }
  }
}
```

항목은 HTTPS URL이다 — `/`로 끝나면 그 소유자 아래 전부, 아니면 그 리포 하나(`.git` 유무 무관). 형식이 틀린 항목이 있으면
부팅 시 실패한다. 목록에 없는 리포는 자격증명 없이 clone한다.
자격증명은 체크아웃의 git 설정이나 원격 URL에 남지 않고, clone이 끝난 뒤 같은 세션에서 실행되는 명령에는 보이지 않으며,
URL의 호스트가 아닌 곳(리다이렉트 등)이나 평문 HTTP로는 건네지지 않는다. 조회는 비대화식이라 저장된 자격증명이 없으면
프롬프트를 기다리지 않고 익명으로 clone한다.

서비스로 실행할 때는 **그 서비스 계정**에 자격증명이 있어야 한다 — Windows 서비스의 기본 계정 LocalSystem에는 설치한
사용자의 자격증명(Git Credential Manager 등 사용자별 저장소)이 보이지 않는다. 비공개 리포를 검사하려면 서비스 계정을
자격증명을 가진 계정으로 바꾸거나, 그 계정에 읽기 전용 토큰을 구성한다. 자격증명의 권한 범위가 곧 Worker가 읽을 수 있는
범위이므로, 검사할 리포만 읽을 수 있는 토큰을 권한다. SSH URL과 git 서브모듈·LFS 객체는 지원하지 않는다.

새 버전은 `worker-v*` 형태의 태그(예: `worker-v0.1.0`)로 릴리스된다 — Host(컨테이너 배포)와는 독립된 버전 계열이다.

설치 스크립트로 최신 바이너리를 받는 것과, Worker가 새 버전을 스스로 내려받아 교체하는 것은 별개다 — 후자는 `Momos:Worker:SelfUpdate:Enabled`(기본값 `false`)로 켠다. 꺼져 있으면 Worker는 업데이트가 있음을 로그에 남길 뿐 아무것도 하지 않는다. 켜면 업데이트를 스테이징한 뒤 스스로 종료하는데, 종료 후 다시 뜨는 것은 전적으로 그 프로세스를 감시하는 쪽(Windows Service, systemd 등, 위 "Worker 설치" 참고)의 재시작 몫이다 — 그런 감시가 없는 머신에서 켜두면 첫 업데이트 힌트에 Worker가 영영 내려간다. 그래서 이 값은 Host 쪽 설정으로 원격 전환할 수 없고, 서비스 등록이 끝난 뒤 운영자가 그 머신에서 직접 켜는 배포 결정으로 남아 있다.

## 문서

- [범위](docs/scope.md) — In / Out
- [아키텍처](docs/architecture.md) — 시스템 경계, 데이터 흐름
- [통합 계약](docs/integration-contract.md) — 외부 시스템이 Momos를 호출하는 방법
- [용어집](docs/glossary.md)

정체성·원칙·개선 프로토콜은 [CLAUDE.md](CLAUDE.md)/[AGENTS.md](AGENTS.md) 참고 — 이 리포에서 작업하는
사람과 AI 에이전트 모두에게 적용되는 1페이지 요약이다.

## 라이선스

[AGPL-3.0](LICENSE)
