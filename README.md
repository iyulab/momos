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

초기 설계 단계. 아직 실행 가능한 첫 슬라이스(Walking Skeleton)가 없다.

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
