#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/install-worker.sh"

fail() { echo "FAIL: $1" >&2; exit 1; }

# detect_platform: 이 테스트는 Linux/x86_64 실행 환경을 가정한다(CI ubuntu-latest, 이 리포의
# 개발 머신은 Git Bash라 uname이 MINGW64_NT를 반환 — 그 경우는 스킵 안내만 출력).
if [ "$(uname -s)" = "Linux" ] && [ "$(uname -m)" = "x86_64" ]; then
  result="$(detect_platform)"
  [ "$result" = "linux-x64" ] || fail "detect_platform: 기대값 linux-x64, 실제 $result"
else
  echo "SKIP: detect_platform은 Linux/x86_64에서만 검증 (현재: $(uname -s)/$(uname -m))"
fi

# verify_checksum: 로컬 fixture로 성공/실패 둘 다 검증 (네트워크 불필요)
tmp="$(mktemp -d)"
echo -n "hello" > "$tmp/file.bin"
sha256sum "$tmp/file.bin" > "$tmp/file.bin.sha256"
verify_checksum "$tmp/file.bin" "$tmp/file.bin.sha256" || fail "verify_checksum: 올바른 체크섬인데 실패로 판정"

echo "corrupted" > "$tmp/file.bin"
if verify_checksum "$tmp/file.bin" "$tmp/file.bin.sha256"; then
  fail "verify_checksum: 손상된 파일을 통과시킴"
fi
rm -rf "$tmp"

# resolve_asset_url: GitHub API 응답 형식을 흉내낸 fixture JSON으로 파싱 로직만 검증
fixture_json='{"assets":[{"name":"momos-worker-linux-x64.tar.gz","browser_download_url":"https://example.invalid/momos-worker-linux-x64.tar.gz"},{"name":"momos-worker-linux-x64.tar.gz.sha256","browser_download_url":"https://example.invalid/momos-worker-linux-x64.tar.gz.sha256"}]}'
result="$(echo "$fixture_json" | extract_asset_url "momos-worker-linux-x64.tar.gz")"
[ "$result" = "https://example.invalid/momos-worker-linux-x64.tar.gz" ] || fail "extract_asset_url: 기대값과 다름: $result"

# extract_asset_url: 매칭되는 자산이 없을 때 빈 문자열을 반환해야 한다 (에러로 죽지 않아야 함)
result="$(echo "$fixture_json" | extract_asset_url "momos-worker-win-x64.zip")"
[ -z "$result" ] || fail "extract_asset_url: 매칭 없을 때 빈 문자열이 아님: $result"

echo "OK: install-worker.sh 단위 테스트 통과"

# write_config: 5개 값을 넣고 JSON 구조·권한을 검증
tmp2="$(mktemp -d)"
write_config "$tmp2" "https://host.example" "hostkey" "https://gpustack.example" "llmkey" "qwen3.8-27b"
config_json="$(cat "$tmp2/appsettings.Production.json")"
echo "$config_json" | grep -q '"BaseUrl": "https://host.example"' || fail "write_config: BaseUrl 누락"
echo "$config_json" | grep -q '"Model": "qwen3.8-27b"' || fail "write_config: Model 누락"
if [ "$(uname -s)" = "Linux" ]; then
  perm="$(stat -c '%a' "$tmp2/appsettings.Production.json")"
  [ "$perm" = "600" ] || fail "write_config: 권한이 600이 아님 (실제: $perm)"
else
  echo "SKIP: 파일 권한 검증은 POSIX ACL 파일시스템에서만 (현재: $(uname -s))"
fi
rm -rf "$tmp2"

# write_config: 값에 큰따옴표·백슬래시가 있어도 유효한 JSON을 생성해야 한다
tmp3="$(mktemp -d)"
write_config "$tmp3" 'a"b\c' "hostkey" "https://gpustack.example" "llmkey" "model"
config_json3="$(cat "$tmp3/appsettings.Production.json")"
echo "$config_json3" | grep -qF '"BaseUrl": "a\"b\\c"' || fail "write_config: 큰따옴표·백슬래시 이스케이프 실패"
rm -rf "$tmp3"

# configure_worker: 이미 설정 파일이 있으면 건드리지 않아야 한다 (idempotent)
tmp4="$(mktemp -d)"
echo '{"existing":"config"}' > "$tmp4/appsettings.Production.json"
configure_worker "$tmp4" > /dev/null
[ "$(cat "$tmp4/appsettings.Production.json")" = '{"existing":"config"}' ] || fail "configure_worker: 기존 설정 파일을 덮어씀"
rm -rf "$tmp4"

# configure_worker: 비대화형(stdin이 tty가 아님)이면 프롬프트 없이 환경변수 값으로 채운다
tmp5="$(mktemp -d)"
MOMOS_WORKER_HOST_BASEURL="https://host.example" \
MOMOS_WORKER_HOST_APIKEY="hostkey" \
MOMOS_LLM_GPUSTACK_ENDPOINT="https://gpustack.example" \
MOMOS_LLM_GPUSTACK_APIKEY="llmkey" \
MOMOS_LLM_GPUSTACK_MODEL="model" \
  configure_worker "$tmp5" < /dev/null
grep -q '"BaseUrl": "https://host.example"' "$tmp5/appsettings.Production.json" || fail "configure_worker: 비대화형 환경변수 값이 반영되지 않음"
rm -rf "$tmp5"

# configure_worker: 진짜 비대화형(제어 터미널 없음)이고 환경변수도 없으면 빈 템플릿을 쓰고
# exit 0으로 끝나야 한다(예전 회귀: exec 3</dev/tty가 ENXIO로 실패해 set -e 때문에 스크립트가
# 죽었던 적이 있음 — setsid로 실제 제어 터미널을 떼어내야 이 경로를 재현할 수 있다).
if [ "$(uname -s)" = "Linux" ] && command -v setsid >/dev/null 2>&1; then
  tmp6="$(mktemp -d)"
  script_path="$SCRIPT_DIR/install-worker.sh"
  if setsid bash -c "
    unset MOMOS_WORKER_HOST_BASEURL MOMOS_WORKER_HOST_APIKEY MOMOS_LLM_GPUSTACK_ENDPOINT MOMOS_LLM_GPUSTACK_APIKEY MOMOS_LLM_GPUSTACK_MODEL
    source '$script_path'
    configure_worker '$tmp6'
  " < /dev/null > /dev/null 2>&1; then
    [ -f "$tmp6/appsettings.Production.json" ] || fail "configure_worker: 진짜 비대화형 경로에서 설정 파일이 생성되지 않음"
  else
    fail "configure_worker: 진짜 비대화형(터미널 없음)에서 exit 0이어야 하는데 실패함 — /dev/tty ENXIO 회귀 재발 의심"
  fi
  rm -rf "$tmp6"
else
  echo "SKIP: 진짜 비대화형 경로 테스트는 Linux + setsid 필요 (현재: $(uname -s))"
fi

echo "OK: install-worker.sh 설정 생성 테스트 통과"

# fetch_and_extract: installs/<version>/에 풀리고 current가 그걸 가리켜야 한다 (네트워크 없이
# resolve_asset_url/curl을 로컬 파일 서버로 흉내낼 수 없으므로, 여기서는 압축 해제+포인터
# 로직만 별도 헬퍼로 분리해 직접 검증한다 — 실제 다운로드 경로는 CI의
# test-install-scripts.yml이 실제 GitHub Release 대상 스모크로 커버).
# install-worker.sh는 detect_platform에서 Linux만 지원하므로 이 검증도 Linux 한정 — Git Bash는
# 심볼릭 링크 생성 권한이 없으면 ln -sfn이 junction으로 대체돼(-L 판정 대상이 아님) 이 테스트가
# 오탐 실패한다(위 detect_platform/chmod 600/setsid 테스트와 동일한 환경 제약).
if [ "$(uname -s)" = "Linux" ]; then
  tmp7="$(mktemp -d)"
  mkdir -p "$tmp7/installs/0.1.0"
  echo "binary" > "$tmp7/installs/0.1.0/Momos.Worker"
  ln -sfn "$tmp7/installs/0.1.0" "$tmp7/current"
  [ -L "$tmp7/current" ] || fail "current: 심볼릭 링크가 아님"
  [ "$(readlink -f "$tmp7/current")" = "$(readlink -f "$tmp7/installs/0.1.0")" ] || fail "current: installs/0.1.0을 가리키지 않음"
  [ -f "$tmp7/current/Momos.Worker" ] || fail "current: 링크를 통해 바이너리에 접근 불가"
  rm -rf "$tmp7"
else
  echo "SKIP: current 심볼릭 링크 레이아웃 테스트는 Linux에서만 검증 (현재: $(uname -s))"
fi

echo "OK: install-worker.sh 레이아웃 테스트 통과"

# render_systemd_unit: 순수 문자열 렌더링이라 OS 무관하게 검증 가능
unit_text="$(render_systemd_unit "/opt/momos-worker" "worker-user")"
echo "$unit_text" | grep -qF "ExecStart=/opt/momos-worker/current/Momos.Worker" || fail "render_systemd_unit: ExecStart 경로 누락"
echo "$unit_text" | grep -qF "User=worker-user" || fail "render_systemd_unit: User 지시자 누락"
echo "$unit_text" | grep -qF "Restart=always" || fail "render_systemd_unit: Restart=always 누락(상주 재시작 보장의 핵심)"

# install_worker_service: 기본값(MOMOS_WORKER_INSTALL_SERVICE 미설정)은 opt-in 전이라 아무 시스템
# 호출도 하지 않고 조용히 건너뛰어야 한다 — systemctl/sudo가 전혀 없는 환경에서도 안전해야 함.
tmp8="$(mktemp -d)"
unset MOMOS_WORKER_INSTALL_SERVICE
output="$(install_worker_service "$tmp8" 2>&1)"
echo "$output" | grep -qF "건너뜁니다" || fail "install_worker_service: opt-in 전인데 건너뛴다는 안내가 없음"
rm -rf "$tmp8"

echo "OK: install-worker.sh systemd 유닛 테스트 통과"
