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

Walking Skeleton 구현 진행 중. `.NET` solution(`Momos.Host`/`Momos.Worker`)이 존재하며, 둘은 별도 프로세스로 배포된다 — Worker가 Host를 poll해 대기 중인 검사 신청서를 가져와 실행하고 결과서를 제출한다. Host의 프로젝트 등록·검사 신청/결과 조회 API가 구현되어 있다: `POST /projects`, `GET /projects/{id}`, `POST /projects/{id}/inspection-requests`, `GET /inspection-requests/{id}`, `GET /inspection-requests/{id}/report`(자세한 내용은 [통합 계약](docs/integration-contract.md) 참고). 신청서는 제출 후 실제로 Worker에 의해 실행된다 — 대상 리포를 체크아웃하고, 샌드박스 환경에서 명령을 실행하며, 실제로 재현한 결함이 있으면 근거(명령 출력)와 함께 보고하고, 없으면 정직하게 지적 0건으로 완료한다. 아직 실제 파일럿 대상으로 돌려 결과 품질을 확인하는 단계는 남아 있다.

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

## 문서

- [헌법](docs/constitution.md) — 정체성, 원칙, 개선 프로토콜
- [개요](docs/overview.md) — 문제, 대상, 역할
- [범위](docs/scope.md) — In / Out
- [아키텍처](docs/architecture.md) — 시스템 경계, 데이터 흐름
- [위협 모델](docs/threat-model.md) — Computer Use 위협 분류, 대응 원칙
- [핵심 개념](docs/concepts.md)
- [용어집](docs/glossary.md)
- [UX 원칙](docs/ux-principles.md)
- [통합 계약](docs/integration-contract.md) — 외부 시스템이 Momos를 호출하는 방법
- [확장 지점](docs/extending.md)

## 라이선스

[AGPL-3.0](LICENSE)
