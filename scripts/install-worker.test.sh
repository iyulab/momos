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

echo "OK: install-worker.sh 단위 테스트 통과"
