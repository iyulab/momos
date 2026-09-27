상태: 이 문서는 현재 구현을 기술한다.

# 통합 계약

외부 시스템(CI/CD 파이프라인, 다른 도구)이 Momos를 호출할 때 지켜야 할 계약이다.

## 원칙

- 통합은 API를 통해서만 이뤄진다. Momos는 대상 리포에 어떤 파일도 요구하지 않는다(비침습성).
- 프로젝트 선언(목적·비전·범위)은 Host의 `Projects` 엔티티에 API로 등록한다.
- 검사는 신청서 제출로 트리거되며, 결과는 검사 결과서로 돌아온다.
- 후속 조치가 필요한 지적은 Momos 내부에서 추적되지 않는다 — 호출하는 쪽이 docket 또는 GitHub Issues 등 자신의 이슈 관리 도구로 가져가야 한다.

## 엔드포인트

| Method | Path | 설명 |
|---|---|---|
| `POST` | `/projects` | 프로젝트를 등록한다(이름·목적·비전·범위 등 선언 정보 포함). |
| `GET` | `/projects/{id}` | 등록된 프로젝트를 조회한다. |
| `POST` | `/projects/{id}/inspection-requests` | 해당 프로젝트에 대한 검사를 신청한다. |
| `GET` | `/inspection-requests/{id}` | 검사 신청서와 그 현재 상태(대기/실행 중/완료/실패)를 조회한다. |
| `GET` | `/inspection-requests/{id}/report` | 검사 신청서에 대한 검사 결과서를 조회한다. |
| `POST` | `/projects/{id}/analysis-requests` | 프로젝트 모델을 만들 분석을 요청한다(`commitRef` 선택). |
| `GET` | `/analysis-requests/{id}` | 분석 요청과 그 현재 상태를 조회한다. |
| `GET` | `/projects/{id}/model` | 프로젝트의 최신 모델(구성 요소·관계·패턴·결정·의도·흐름·불변식·목차·분석 범위·진술)을 조회한다. |
| `GET` | `/projects/{id}/model/report` | 최신 모델의 딥 리포트를 마크다운 문서 트리로 조회한다. |
| `POST` | `/projects/{id}/model/claims/{claimKey}/corrections` | 최신 모델의 한 진술에 개발자 판정을 기록한다. |
| `POST` | `/projects/{id}/knowledge/documents` | 프로젝트 지식에 문서(요구사항·설계 메모 등)를 등록한다. 검사 에이전트가 조회하는 맥락이 된다. |
| `POST` | `/projects/{id}/knowledge/query` | 프로젝트 지식을 의미 검색한다. |
| `GET` | `/health` | Host 프로세스가 요청을 받는지 확인한다(`{"status":"ok"}`). |

Development 환경에서만 OpenAPI 문서(`/openapi/v1.json`)가 노출된다 — 계약의 일부가 아니며, 이 문서가 정본이다.

검사 신청서 제출은 실행 노드(Worker)에 의해 실제로 처리된다 — 신청서는 대기 → 실행 중 → 완료(또는 실패)로 전환되며, 완료되면 결과서 조회가 실제 내용을 반환한다. Worker는 대상 리포를 체크아웃하고 실행 샌드박스 안에서 명령을 실제로 실행하며 재현한 결함만 보고한다 — 아무것도 재현하지 못하면 결과서는 정직하게 지적 0건으로 완료된다(근거 없는 지적을 내놓지 않는다는 원칙에 따른 결과이지, 검사 도구가 미연결된 상태의 placeholder가 아니다). 결과서는 실제로 무엇을 실행했는지 보여주는 `toolCalls` 트레이스도 함께 담아, 호출하는 쪽이 "정말 아무것도 발견하지 못함"과 "탐색이 코드에 도달조차 못함"을 구분할 수 있게 한다.

JSON 필드 이름은 camelCase이고, enum 값은 PascalCase 문자열로 오간다(예: `"Pending"`, `"Analysis"`, `"Fact"`,
`"Corrected"`). 검사 신청서와 분석 요청의 응답은 같은 모양이며 `kind` 필드(`Inspection` 또는 `Analysis`)로
구별된다.

## 요청 본문

필드가 전부 선택인 요청은 본문 없이 보내도 된다(모든 필드를 생략한 것과 같다).

- **프로젝트 등록** — `POST /projects`:
  - 필수: `name`, `purpose`, `vision`, `scope`(비어 있으면 `400`).
  - 선택: `repositoryUrl`(검사가 체크아웃할 리포 — 분석 요청과 `commitRef`에 필요), `deploymentUrl`(배포된 앱 주소 — 현재
    Worker는 아직 사용하지 않는다). 비공개 리포는 그 요청을 처리하는 Worker의 운영자가 허용한 경우에만 체크아웃된다(README
    「비공개 리포」) — 허용되지 않았으면 요청은 체크아웃 단계에서 실패하고 `failureReason`에 git의 오류가 담긴다.
  - 선택, 셋이 함께: `appInstallerUri`, `appInstallPlatform`, `appInstallLaunchCommand`(하나라도 있으면 셋 다 있어야 하며
    아니면 `400`), 그리고 `appInstallArgs`. 설치형 앱 자료이며 현재 Worker는 아직 사용하지 않는다.
  - 성공하면 `201 Created`와 프로젝트(`id` 포함), `Location: /projects/{id}`.
- **검사 신청** — `POST /projects/{id}/inspection-requests`: `focus`(선택, 에이전트에게 줄 중점 — 예: "로그인 흐름"),
  `commitRef`(선택, 체크아웃할 커밋·브랜치·태그 — 프로젝트에 `repositoryUrl`이 없으면 `400`). 프로젝트가 없으면 `404`.
- **분석 요청** — `POST /projects/{id}/analysis-requests`: `commitRef`(선택). 응답은 아래 「프로젝트 모델」 참고.
- **지식 문서 등록** — `POST /projects/{id}/knowledge/documents`: `{title, content}`. 성공하면 `201 Created`와
  `{documentId}` — 문서 하나를 다시 읽는 경로는 없으므로 `Location`은 없다. 프로젝트가 없으면 `404`.
- **지식 검색** — `POST /projects/{id}/knowledge/query`: `{query, maxResults}`(`maxResults` 기본 5). `200`과
  `{snippets: [{content, score}]}`. 프로젝트가 없으면 `404`.

## 프로젝트 모델

분석 요청은 검사 신청서와 같은 큐에서 Worker가 가져가 처리한다. 분석은 LLM을 호출하지 않는 결정적 추출이다 —
현재는 대상 커밋의 .NET 프로젝트 파일(`*.csproj`)에서 프로젝트(구성 요소)와 프로젝트 참조(관계)를 추출하고, 각각을
코드 근거가 붙은 `Fact` 진술로 뒷받침한다. 구성 요소마다 대상 커밋에서 거슬러 올라간 git 이력으로 `History` 진술을
더한다 — 프로젝트 디렉터리 아래를 바꾼 커밋 수와 가장 최근 변경의 날짜·제목, 그리고 커밋 메시지가 구성 요소 이름을
(대소문자를 가려, 더 긴 이름의 일부가 아닌 온전한 이름으로) 언급한 커밋. 근거는 가장 최근 커밋 최대 5개의 `Commit`
sha다. 인용한 제목은 커밋 자신의 문장일 뿐 변경 이유에 대한 Momos의 추정이 아니다. 분석은 무엇을 읽었고 무엇을
읽지 않았는지도 분석 범위(`coverage`)로 기록한다 — 읽지 못한 프로젝트 파일, 읽지 않는 소스 코드, .NET이 아닌 빌드.
모델에는 흐름(`flows`)·불변식(`invariants`)·목차(`outline`)를 담을 자리가 있지만 결정적 추출기는 이것들을 만들지
않는다(빈 목록). 증분 분석, 패턴 식별, PR·이슈 이력 추출, 평가(`Assessment`) 진술 생성은 아직 구현되지 않았다.

- **분석 요청** — `POST /projects/{id}/analysis-requests`는 `201 Created`와 요청(`kind: "Analysis"`)을 돌려준다.
  프로젝트가 없으면 `404`, 프로젝트에 `repositoryUrl`이 없으면 추출할 대상이 없으므로 `400`이다. 상태는 검사 신청서와
  같이 `Pending` → `Running` → `Completed`(또는 `Failed`, 이유는 `failureReason`)로 전환된다.
  `GET /analysis-requests/{id}`는 검사 신청서의 id에는 `404`를 돌려준다.
- **모델 버전** — 분석이 완료될 때마다 새 모델 버전(`modelVersion`, 1부터 증가)이 만들어진다. 같은 커밋을 다시
  분석해도 새 버전이다. `GET /projects/{id}/model`은 최신 버전을 돌려주고, 아직 모델이 없으면 `404`다.
- **진술** — 각 진술은 안정된 `key`, 등급 `tier`(`Fact`·`History`·`Assessment`), `statement`, 근거 목록
  `evidence`(`kind`: `Code`·`Commit`·`PullRequest`·`Issue`·`Finding`·`Claim`), `confidence`(`High`·`Medium`·`Low`), 생성 방식 `origin`(`Deterministic`·`Synthesized` — 제출에 필수),
  판정 상태 `status`(`Proposed`·`Confirmed`·`Disputed`·`Corrected`)와 `correction`·`correctedAt`을 갖는다. 근거 없는
  진술은 받아들여지지 않는다. 근거는 종류마다 그것을 찾아갈 식별자를 가져야 한다 — `Code`는 `path`, `Commit`은 `sha`,
  `PullRequest`·`Issue`는 `url`, `Finding`은 `inspectionRequestId`, `Claim`은 같은 모델 안의 `claimKey`. 구성 요소·패턴·결정·
  의도의 id는 모델 안에서 유일해야 하고, 관계와 패턴이 가리키는 구성 요소는 모델 안에 있어야 한다. 다른 진술을 `Claim` 근거로 인용하는 진술의 `confidence`는 인용한 진술 중 가장 낮은 것을 넘을 수 없다. 재현 결함(`Finding`) 근거가 있어도 이 상한은 그대로 적용된다. 진술끼리 순환 인용할 수 없다. 이유(`rationale`)를 적은 결정은 `History` 등급 진술을 하나 이상 가져야 하며, 이유가 비어 있을 수 없다. 어긋나면 `400`이다. 결정(decision)의 이유가 기록되지 않았으면 `rationale`은 지어낸 값이 아니라
  `unrecorded`다.
- **흐름·불변식·목차·분석 범위** — 흐름은 순서 있는 단계(`steps: [{componentId?, claimKey}]`), 불변식은 `statement`·자유
  분류 `kind`·대상 구성 요소 `appliesTo`를 갖고, 둘 다 진술을 하나 이상 가져야 한다. 흐름 단계와 불변식 대상은 모델 안을
  가리켜야 한다. 목차의 장(`outline: [{id, path, title, purpose, ownerSummaryClaims, blocks}]`)은 리포트 한 페이지다 —
  `path`는 리포트 루트의 소문자 파일 이름(`[a-z0-9][a-z0-9-]*.md`, 64자 이내, `index.md`·`unknowns.md` 제외)이고 장마다
  달라야 하며, `title`은 80자·`purpose`는 200자 이내다(제목과 목적은 주장이 아니다 — 주장은 진술로). 블록
  (`{kind, ref}`, `kind`: `Claim`·`Component`·`Pattern`·`Decision`·`Intent`·`Flow`·`Invariant`)은 모델 안의 그 종류를
  가리켜야 한다. 장은 진술이 없어도 된다(리포트가 근거를 찾지 못했다고 쓴다). 분석 범위(`coverage`)는 읽은 영역
  (`analyzed: [{area, detail}]`)·읽지 않은 영역(`notAnalyzed: [{area, reason}]`)·분석 중 폐기한 제안의 사유별 수
  (`rejected: [{reason, count}]`, 수는 1 이상)·생성기(`generator: {model, promptVersion}`, 결정적 분석이면 `null`)다.
  분석 범위를 기록하기 전에 만들어진 모델 버전은 `coverage`가 `null`이다. 어긋나면 `400`이다.
- **딥 리포트** — `GET /projects/{id}/model/report`는 `{modelVersion, baseCommit, documents: [{path, content}]}`를
  돌려준다. `documents`는 마크다운 문서 트리다 — `index.md`가 요약이고(미지 항목 수와 개발자가 판정한 진술 수 — 검증된 지표가
  아니다 — 를 싣는다), `unknowns.md`는 항상 있다(분석하지 않은 영역, 이유가 기록되지 않은 결정과 아직 판정되지 않은
  낮은 신뢰도 해석을 질문으로 — 답하는 길과 함께 —, 반박된 진술, 근거 없는 장, 분석 중 폐기한 것). 모델에 목차가
  있으면 장마다 루트에 한 페이지(`<path>`)가 생기고 `index.md`는 장 목록이 된다. 목차가 없으면 `index.md`가 구조 도식·
  구성 요소 목록 등을 담는다. `components/*.md`와 `claims/*.md`가 구성 요소·진술 하나씩을 담으며, 문서 사이의 링크는
  모두 상대 경로다. 줄 끝은
  LF이고 출력은 플랫폼과 무관하게 같다. 파일 이름은 id가 소문자·숫자·`.`·`_`·`-`로만 된 짧은 값이면 그대로 쓰고,
  그렇지 않으면 읽을 수 있는 슬러그에 원래 id의 짧은 해시를 붙인다. 문서 하나만 원하면 `index.md`를 읽는다. 프로젝트나
  모델이 없으면 `404`다. 리포트 본문은 영어로 렌더링된다. 진술 페이지의 제목은 진술 문장이다(80자가 넘으면 줄여 쓰고
  본문에는 전문). 진술·교정문 같은 자유 텍스트는 마크다운으로 넣되 raw HTML은 문자 그대로 보인다 — 코드 스팬 밖의
  `&`·`<`·`>`는 이스케이프된다.
- **교정** — `POST /projects/{id}/model/claims/{claimKey}/corrections`의 본문은 `{status, correction}`이다. `status`는
  `Confirmed`·`Disputed`·`Corrected` 중 하나이고, `Proposed`면 `400`, `Corrected`인데 `correction`이 비어 있으면
  `400`이다. `claimKey`가 최신 모델에 없으면 `404`이며, 성공하면 갱신된 진술을 돌려준다. 원래 진술은 고쳐지거나
  지워지지 않고 판정이 그 옆에 기록된다. 판정은 재분석된 진술의 문장이 이전과 똑같을 때만 다음 모델 버전으로
  이어지고, 문장이 달라지면 `Proposed`로 다시 시작한다.
- **검사와의 관계** — 모델 진술(교정 포함)은 프로젝트 지식으로 색인되어, 검사 에이전트가 기존 지식 조회로 읽는다.
  교정이 있으면 에이전트는 그것을 의도된 설계로 보고 동작을 판단한다. 모델 진술은 검사의 맥락일 뿐 지적의 근거가
  아니다 — 지적은 여전히 재현한 명령 출력을 근거로 가진다.

### Worker 계약

Worker 전용 엔드포인트는 `Authorization: Bearer` 공유 키로 보호된다(없거나 틀리면 `401`).

- Worker는 `POST /inspection-requests/claim-next`로 대기 중인 요청 하나를 가져간다(본문 `{protocolVersion, workerVersion}`).
  응답은 항상 `200`이고 `{request, updateRequired, recommendedWorkerVersion}`이다 — 가져갈 것이 없거나 Worker의 프로토콜이
  지원되지 않으면 `request`는 `null`이며 둘은 `updateRequired`로 구별된다. 가져간 요청은 `Running`이 되고,
  `Momos:Host:InspectionClaim:ReclaimTimeout`(기본 30분) 안에 결과가 오지 않으면 다음 `claim-next`가 다시 가져갈 수 있다.
- 검사를 마친 Worker는 결과서를 `POST /inspection-requests/{id}/report`로 제출한다(`{findings, toolCalls}`).

- 분석을 마친 Worker는 모델을 `POST /analysis-requests/{id}/model`로 제출한다. 목록 필드(`components`·`relations`·
  `patterns`·`decisions`·`intents`·`flows`·`invariants`·`outline`·`claims`)나 `coverage` 객체(그 안의 세 목록 포함)를
  빠뜨리거나 `baseCommit`이 비어 있으면 `400`이고(보고할 것이 없으면 빈 목록을 보낸다), 진술의 `tier`·`confidence`·
  `origin`이나 블록의 `kind`를 빠뜨려도 `400`이다. 근거 규칙을 어긴 진술도 `400`이다. 요청이 `Running`이 아니거나 검사 신청서의 id면 `409`,
  성공하면 `201 Created`로 요청이 `Completed`가 된다. 반대로 분석 요청의 id로 검사 결과서를 제출하면 `409`다.
- 분석이 실패하면 검사와 같은 `POST /inspection-requests/{id}/fail`로 보고한다.
- 와이어 프로토콜 버전은 **4**다. 프로토콜 4는 모델 제출에 `flows`·`invariants`·`outline`·`coverage`를 필수로
  더했다 — 그것을 보내지 않는 프로토콜 3 Worker는 분석을 다 마친 뒤 제출에서야 거부되므로, 이 Host는 프로토콜 4
  미만 Worker에게 일을 넘기지 않고 업데이트가 필요하다는 신호(`updateRequired: true`)만 돌려준다. (프로토콜 3은
  `claim-next` 응답에 요청 종류 `kind`를 더했었다.)

## 안정성 약속

Momos는 아직 0.x 단계이며, 공개 API의 하위 호환성을 아직 약속하지 않는다. 호환성보다 안정성·단순함을 우선한다(`CLAUDE.md`/`AGENTS.md`의 트레이드오프 서열 참고).
