# Changelog

이 파일은 **`Momos.Worker` 릴리스**(`worker-v*` 태그)의 변경 사항을 기록한다. 형식은
[Keep a Changelog](https://keepachangelog.com/ko/1.1.0/)를 따르고, 버전은
[Semantic Versioning](https://semver.org/lang/ko/)을 따른다.

`Momos.Host`에는 버전이 없다 — `main`에서 컨테이너로 상시 배포되며 태그·릴리스를 만들지 않는다.
Host의 변화는 이 파일에 버전 절로 기록하지 않고, Worker가 알아야 하는 경우(와이어 프로토콜
버전 변경 등)에만 그 Worker 릴리스 절에 함께 적는다.

momos는 `0.x` 단계다. 공개 계약은 아직 고정되지 않았고, 더 나은 설계가 나오면 미루지 않고
채택한다 — 그래서 호환되지 않는 변경도 마이너 릴리스에 나올 수 있으며, 그 사실은 이 파일에 적힌다.

## [0.1.0]

첫 릴리스.

### Added

- Host를 poll해 대기 중인 검사 신청서를 가져오고(`claim-next`), 대상 리포를 체크아웃해 샌드박스에서
  명령을 실행하며, 재현한 결함을 근거(명령 출력)와 함께 결과서로 제출하는 Worker. 재현한 결함이 없으면
  지적 0건으로 완료한다.
- 에이전트 루프가 호출한 도구와 그 결과를 트레이스로 Host에 제출한다. 실행이 실패해도 그때까지의
  트레이스는 실패 보고와 함께 전달된다.
- Worker→Host 요청 인증(`Authorization: Bearer`) — Host와 같은 API 키가 없으면 부팅 시 실패한다.
- LLM 프로바이더 설정 검증 — 필수 값이 비어 있으면 부팅 시 실패한다.
- 설치 스크립트(`scripts/install-worker.sh`, `scripts/install-worker.ps1`) — 릴리스 바이너리를 체크섬으로
  검증해 내려받고 설정 값을 기록하며, Worker를 OS 서비스로 등록한다(Windows 서비스는 기본 등록,
  Linux systemd 유닛은 `MOMOS_WORKER_INSTALL_SERVICE=1`로 옵트인).
- 자체 업데이트(`Momos:Worker:SelfUpdate:Enabled`, 기본값 `false`) — 새 버전을 스테이징한 뒤 종료하고,
  재시작은 프로세스를 감시하는 서비스 관리자에게 맡긴다.
- Windows x64·Linux x64용 자체 포함 단일 파일 바이너리와 SHA-256 체크섬.
