# Momos — AI 진입점

이 문서는 헌법(`docs/constitution.md`)의 1페이지 요약이다. 작업 전 반드시 `docs/constitution.md` 전문을 확인할 것.

## 정체성 (한 문장)
정적 분석이 못 잡는 실행 환경에서 자율적으로 상호작용하며 UX·보안 결함을 찾아내는 에이전트.

## 비타협 원칙
**근거 기반 엄밀함** — 근거 없는 지적은 내놓지 않는다. 확신이 없으면 침묵한다.

## 트레이드오프 서열
안정성 > 가독성/단순함 > 개발속도 > 호환성 > 성능
(0.x 단계 — 호환성을 지키려고 이 서열을 뒤집지 않는다)

## 비목표
- 코드 자동 수정 금지
- 실제 인증 절차 대행 금지
- 사업적 타당성 판단 금지
- 자체 이슈 트래킹 미제공 (docket/GitHub Issues로 위임)

## 개선 프로토콜
| 등급 | 범위 |
|---|---|
| 자율 수행 | 버그 수정, 테스트, 내부 리팩터링, 문서 정리 |
| 제안 후 승인 | 공개 API 변경, 의존성 추가, 데이터 스키마 변경 |
| 반드시 논의 | 원칙 위반, 비목표 침범, Type-1 재결정, **Computer Use 권한/격리 설정 변경(예외 없음)** |

## 기술 원칙
- 구현 스택은 .NET 단일 스택(Host/Worker 공통).
- 에이전트/도구 오케스트레이션은 MCP(Model Context Protocol) 기반. 자체 프로토콜 발명 금지.
- Momos는 타겟 리포에 어떤 파일도 요구하지 않고 작동한다(비침습성). 선언 정보는 Momos 자신(Projects 엔티티)에 있다.
- 대부분의 액션은 API로 동작한다. UI(`momos-console`)는 그 API의 클라이언트일 뿐이다.

## 문서 맵
- `docs/constitution.md` — 전문
- `docs/overview.md`, `docs/scope.md`, `docs/architecture.md`, `docs/concepts.md`, `docs/glossary.md`, `docs/ux-principles.md`
- `docs/threat-model.md` — Computer Use 위협 분류와 v1 대응 원칙
- `docs/integration-contract.md`, `docs/extending.md` — 외부 통합
- 비공개 작업 기록(결정 원장·백로그·ADR)은 이 리포 밖의 엄브렐러 워크스페이스에 있다. 이 리포만 클론했다면 접근할 필요 없는 내부 추적 자료다.
