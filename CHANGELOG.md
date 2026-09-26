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
- 분석 요청 처리 — `claim-next`가 넘겨준 요청의 종류가 분석이면 에이전트 루프 대신 결정적 추출기(LLM 호출 없음)를
  돌린다. 체크아웃한 커밋의 .NET 프로젝트 파일에서 프로젝트(구성 요소)와 프로젝트 참조(관계)를 읽어, 파일 근거가 붙은
  진술로 이뤄진 프로젝트 모델을 `POST /analysis-requests/{id}/model`로 제출한다. 추출이 실패하면 검사와 같은
  실패 보고 경로(`fail`)로 보고한다.
- 검사 에이전트가 프로젝트 모델을 인지한다 — 기존 지식 조회가 모델 진술과 개발자 교정도 돌려주며, 에이전트는 교정을
  의도된 설계로 보고 동작을 판단한다. 모델 진술은 맥락일 뿐 지적의 근거가 되지 않는다.
- 와이어 프로토콜 3 — `claim-next` 응답에 요청 종류(`kind`)가 더해졌다. 이 Host는 프로토콜 2 Worker에게 일을
  넘기지 않고 업데이트가 필요하다는 신호만 돌려준다(프로토콜 2 Worker는 이 Host와 호환되지 않는다).
- 에이전트 루프가 호출한 도구와 그 결과를 트레이스로 Host에 제출한다. 실행이 실패해도 그때까지의
  트레이스는 실패 보고와 함께 전달된다.
- 비공개 리포 체크아웃 — 운영자가 `Momos:Worker:Checkout:CredentialedRepositories`에 허용한 HTTPS 리포(또는 소유자 범위)면
  Worker 실행 계정의 git credential helper에 그 호스트의 자격증명을 묻고(비대화식), clone 명령 하나에만 환경변수로 넘긴다.
  허용 목록은 기본으로 비어 있다. 접근 권한 때문에 clone이 실패하면 실패 사유에 바꿀 설정(또는 자격증명이 거부됐다는 사실)을
  덧붙인다. 자격증명은 체크아웃에 남지 않고 이후 명령에 보이지 않으며, 다른
  호스트·평문 HTTP로는 건네지지 않는다. 서비스 계정에 자격증명이 필요하다(README 「비공개 리포」).
- Worker→Host 요청 인증(`Authorization: Bearer`) — Host와 같은 API 키가 없으면 부팅 시 실패한다.
- LLM 프로바이더 설정 검증 — 필수 값이 비어 있으면 부팅 시 실패한다.
- 설치 스크립트(`scripts/install-worker.sh`, `scripts/install-worker.ps1`) — 릴리스 바이너리를 체크섬으로
  검증해 내려받고 설정 값을 기록하며, Worker를 OS 서비스로 등록한다(Windows 서비스는 기본 등록,
  Linux systemd 유닛은 `MOMOS_WORKER_INSTALL_SERVICE=1`로 옵트인).
- 자체 업데이트(`Momos:Worker:SelfUpdate:Enabled`, 기본값 `false`) — 새 버전을 스테이징한 뒤 종료하고,
  재시작은 프로세스를 감시하는 서비스 관리자에게 맡긴다.
- Windows x64·Linux x64용 자체 포함 단일 파일 바이너리와 SHA-256 체크섬.
- 실행이 Worker 종료가 아닌 이유로 취소되면(예: HTTP 요청 시간 초과) 실패로 보고한다 — 신청서가 `Running`에 머물다
  회수·재시도되며 같은 시간 초과를 이유 없이 반복하지 않는다.
- HTTP 클라이언트의 요청 단위 로그는 기본 `Warning` 수준이다 — 유휴 상태의 `claim-next` 폴링이 로그를 채우지 않는다.
  필요하면 `Logging:LogLevel:System.Net.Http.HttpClient`로 되돌린다.
