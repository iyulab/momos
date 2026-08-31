# Momos

정적 분석이 잡지 못하는 실행 환경의 문제(UX 흐름, 실행 중 결함)를 자율 에이전트가 직접 상호작용하며 찾아내는 검사 도구.

## 대상

CI/CD 파이프라인 안에서, 개별 프로젝트 개발자가 릴리스 전 셀프 QC로 사용한다.

## 무엇이 아닌가

- 코드를 자동으로 고치는 도구가 아니다 — 발견·보고까지만 한다.
- 공식 인증기관(GS인증, CC인증 등) 역할을 대행하지 않는다.
- 요구사항의 사업적 타당성을 판단하지 않는다.
- 자체 이슈 트래커가 아니다 — 후속 조치는 [docket](https://github.com/iyulab/docket) 또는 GitHub Issues로 넘어간다.

## 현재 상태

Walking Skeleton 구현 진행 중. `.NET` solution(`Momos.Host`/`Momos.Worker`)이 존재하며, 둘은 별도 프로세스로 배포된다 — Worker가 Host를 poll해 대기 중인 검사 신청서를 가져와 실행하고 결과서를 제출한다. Host의 프로젝트 등록·검사 신청/결과 조회 API가 구현되어 있다: `POST /projects`, `GET /projects/{id}`, `POST /projects/{id}/inspection-requests`, `GET /inspection-requests/{id}`, `GET /inspection-requests/{id}/report`(자세한 내용은 [통합 계약](docs/integration-contract.md) 참고). 신청서는 제출 후 실제로 Worker에 의해 실행된다 — 대상 리포를 체크아웃하고, 샌드박스 환경에서 명령을 실행하며, 실제로 재현한 결함이 있으면 근거(명령 출력)와 함께 보고하고, 없으면 정직하게 지적 0건으로 완료한다. 실제 대상 리포에 대한 엔드투엔드 실행(Host+Worker+LLM+실행 샌드박스)이 검증됐다 — 남은 것은 지적 품질을 평가할 비교 방법론을 다듬는 것이다.

## 설정

`Momos.Host`는 `ConnectionStrings:MomosDb`(SQLite 파일 경로) 하나만 있으면 바로 뜬다 — 기본값이 커밋돼 있어 별도 설정 없이 동작한다.

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

Worker→Host 통신도 인증이 **필수**다. Host는 `Momos:Host:WorkerAuth:ApiKey`, Worker는 그와 동일한 값을 `Momos:Worker:Host:ApiKey`에 채워야 하며, 둘 다 부팅 시 `OptionsValidationException`으로 검증한다. Worker는 이 값을 매 요청 `Authorization: Bearer {ApiKey}` 헤더로 보내고, Host는 `claim-next`/`report`/`fail` 엔드포인트에서만 이를 검사한다(프로젝트 등록·조회 등 나머지 API는 별개 통합 표면이라 대상이 아니다). GPUStack 설정과 마찬가지로 커밋하지 말고 환경 변수(`Momos__Host__WorkerAuth__ApiKey`, `Momos__Worker__Host__ApiKey`)로 주입한다.

## Worker 설치

사내(또는 파일럿) 머신에 `Momos.Worker`를 설치할 때는 [Releases](https://github.com/iyulab/momos/releases)에 올라오는 self-contained 바이너리와 설치 스크립트를 쓴다 — .NET 런타임을 미리 설치할 필요가 없다.

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

설치 후 실행은 직접 하거나(`<설치경로>/Momos.Worker`, Windows는 `Momos.Worker.exe`) 운영 환경에 맞는 방식(Windows Service, systemd 등)으로 상주시킨다 — 이 스크립트는 그 등록까지는 하지 않는다. 재실행하면 기존 설정 파일은 그대로 둔 채 바이너리만 최신으로 교체한다. Windows에서 설치한 사용자와 다른 계정(예: LocalSystem)으로 서비스를 등록하면 그 계정에 `appsettings.Production.json` 읽기 권한을 추가해야 한다 — 설치 스크립트가 현재 사용자 전용으로 ACL을 좁혀두기 때문이다.

새 버전은 `worker-v*` 형태의 태그(예: `worker-v0.1.0`)로 릴리스된다 — Host(컨테이너 배포)와는 독립된 버전 계열이다.

## 문서

- [범위](docs/scope.md) — In / Out
- [아키텍처](docs/architecture.md) — 시스템 경계, 데이터 흐름
- [통합 계약](docs/integration-contract.md) — 외부 시스템이 Momos를 호출하는 방법
- [용어집](docs/glossary.md)

정체성·원칙·개선 프로토콜은 [CLAUDE.md](CLAUDE.md)/[AGENTS.md](AGENTS.md) 참고 — 이 리포에서 작업하는
사람과 AI 에이전트 모두에게 적용되는 1페이지 요약이다.

## 라이선스

[AGPL-3.0](LICENSE)
