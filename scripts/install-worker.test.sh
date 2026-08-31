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
perm="$(stat -c '%a' "$tmp2/appsettings.Production.json" 2>/dev/null || stat -f '%A' "$tmp2/appsettings.Production.json")"
[ "$perm" = "600" ] || fail "write_config: 권한이 600이 아님 (실제: $perm)"
rm -rf "$tmp2"

echo "OK: install-worker.sh 설정 생성 테스트 통과"
