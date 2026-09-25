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
| `GET` | `/projects/{id}/model` | 프로젝트의 최신 모델(구성 요소·관계·진술)을 조회한다. |
| `GET` | `/projects/{id}/model/report` | 최신 모델의 딥 리포트를 마크다운 문서 트리로 조회한다. |
| `POST` | `/projects/{id}/model/claims/{claimKey}/corrections` | 최신 모델의 한 진술에 개발자 판정을 기록한다. |

검사 신청서 제출은 실행 노드(Worker)에 의해 실제로 처리된다 — 신청서는 대기 → 실행 중 → 완료(또는 실패)로 전환되며, 완료되면 결과서 조회가 실제 내용을 반환한다. Worker는 대상 리포를 체크아웃하고 실행 샌드박스 안에서 명령을 실제로 실행하며 재현한 결함만 보고한다 — 아무것도 재현하지 못하면 결과서는 정직하게 지적 0건으로 완료된다(근거 없는 지적을 내놓지 않는다는 원칙에 따른 결과이지, 검사 도구가 미연결된 상태의 placeholder가 아니다). 결과서는 실제로 무엇을 실행했는지 보여주는 `toolCalls` 트레이스도 함께 담아, 호출하는 쪽이 "정말 아무것도 발견하지 못함"과 "탐색이 코드에 도달조차 못함"을 구분할 수 있게 한다.

JSON 필드 이름은 camelCase이고, enum 값은 PascalCase 문자열로 오간다(예: `"Pending"`, `"Analysis"`, `"Fact"`,
`"Corrected"`). 검사 신청서와 분석 요청의 응답은 같은 모양이며 `kind` 필드(`Inspection` 또는 `Analysis`)로
구별된다.

## 프로젝트 모델

분석 요청은 검사 신청서와 같은 큐에서 Worker가 가져가 처리한다. 분석은 LLM을 호출하지 않는 결정적 추출이다 —
현재는 대상 커밋의 .NET 프로젝트 파일(`*.csproj`)에서 프로젝트(구성 요소)와 프로젝트 참조(관계)를 추출하고, 각각을
코드 근거가 붙은 `Fact` 진술로 뒷받침한다. 증분 분석, 패턴 식별, 이력(커밋·PR·이슈) 추출, 평가(`Assessment`) 진술
생성은 아직 구현되지 않았다.

- **분석 요청** — `POST /projects/{id}/analysis-requests`는 `201 Created`와 요청(`kind: "Analysis"`)을 돌려준다.
  프로젝트가 없으면 `404`, 프로젝트에 `repositoryUrl`이 없으면 추출할 대상이 없으므로 `400`이다. 상태는 검사 신청서와
  같이 `Pending` → `Running` → `Completed`(또는 `Failed`, 이유는 `failureReason`)로 전환된다.
  `GET /analysis-requests/{id}`는 검사 신청서의 id에는 `404`를 돌려준다.
- **모델 버전** — 분석이 완료될 때마다 새 모델 버전(`modelVersion`, 1부터 증가)이 만들어진다. 같은 커밋을 다시
  분석해도 새 버전이다. `GET /projects/{id}/model`은 최신 버전을 돌려주고, 아직 모델이 없으면 `404`다.
- **진술** — 각 진술은 안정된 `key`, 등급 `tier`(`Fact`·`History`·`Assessment`), `statement`, 근거 목록
  `evidence`(`kind`: `Code`·`Commit`·`PullRequest`·`Issue`·`Finding`·`Claim`), `confidence`(`High`·`Medium`·`Low`),
  판정 상태 `status`(`Proposed`·`Confirmed`·`Disputed`·`Corrected`)와 `correction`·`correctedAt`을 갖는다. 근거 없는
  진술은 받아들여지지 않는다. 근거는 종류마다 그것을 찾아갈 식별자를 가져야 한다 — `Code`는 `path`, `Commit`은 `sha`,
  `PullRequest`·`Issue`는 `url`, `Finding`은 `inspectionRequestId`, `Claim`은 같은 모델 안의 `claimKey`. 구성 요소·패턴·결정·
  의도의 id는 모델 안에서 유일해야 하고, 관계와 패턴이 가리키는 구성 요소는 모델 안에 있어야 한다. 어긋나면 `400`이다. 결정(decision)의 이유가 기록되지 않았으면 `rationale`은 지어낸 값이 아니라
  `unrecorded`다.
- **딥 리포트** — `GET /projects/{id}/model/report`는 `{modelVersion, baseCommit, documents: [{path, content}]}`를
  돌려준다. `documents`는 마크다운 문서 트리다 — `index.md`가 요약(구조 도식·구성 요소 목록 등)이고,
  `components/*.md`와 `claims/*.md`가 구성 요소·진술 하나씩을 담으며, 문서 사이의 링크는 모두 상대 경로다. 줄 끝은
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

- 분석을 마친 Worker는 모델을 `POST /analysis-requests/{id}/model`로 제출한다. 목록 필드(`components`·`relations`·
  `patterns`·`decisions`·`intents`·`claims`)를 빠뜨리거나 `baseCommit`이 비어 있으면 `400`이고(보고할 것이 없으면
  빈 목록을 보낸다), 근거 규칙을 어긴 진술도 `400`이다. 요청이 `Running`이 아니거나 검사 신청서의 id면 `409`,
  성공하면 `201 Created`로 요청이 `Completed`가 된다. 반대로 분석 요청의 id로 검사 결과서를 제출하면 `409`다.
- 분석이 실패하면 검사와 같은 `POST /inspection-requests/{id}/fail`로 보고한다.
- 와이어 프로토콜 버전은 **3**이다. 프로토콜 3은 `claim-next` 응답에 요청 종류(`kind`)를 더했다 — 그것을 모르는
  프로토콜 2 Worker는 분석 요청을 검사로 실행하게 되므로, 이 Host는 프로토콜 2 Worker에게 일을 넘기지 않고
  업데이트가 필요하다는 신호(`updateRequired: true`)만 돌려준다.

## 안정성 약속

Momos는 아직 0.x 단계이며, 공개 API의 하위 호환성을 아직 약속하지 않는다. 호환성보다 안정성·단순함을 우선한다(`CLAUDE.md`/`AGENTS.md`의 트레이드오프 서열 참고).
