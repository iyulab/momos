#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/deploy-host.sh"

fail() { echo "FAIL: $1" >&2; exit 1; }

# is_digest_reference: digest 참조와 태그 참조를 구분해야 한다
is_digest_reference "iyulabimages.azurecr.io/momos-host@sha256:abc123" \
  || fail "is_digest_reference: digest 참조를 인식하지 못함"
if is_digest_reference "iyulabimages.azurecr.io/momos-host:latest"; then
  fail "is_digest_reference: 태그 참조를 digest로 오인식"
fi

# registry_host_of / repo_and_tag_of: 첫 '/' 기준으로만 나눠야 한다(repo 이름에 ':'가 없는 전제)
result="$(registry_host_of "iyulabimages.azurecr.io/momos-host:latest")"
[ "$result" = "iyulabimages.azurecr.io" ] || fail "registry_host_of: 기대값과 다름: $result"

result="$(repo_and_tag_of "iyulabimages.azurecr.io/momos-host:latest")"
[ "$result" = "momos-host:latest" ] || fail "repo_and_tag_of: 기대값과 다름: $result"

# resolve_image_digest: 이미 digest 참조면 az cli를 호출하지 않고 그대로 반환해야 한다
# (az가 PATH에 없어도 이 경로는 통과해야 함 — 순수 early-return 검증)
result="$(resolve_image_digest "iyulabimages.azurecr.io/momos-host@sha256:already-pinned")"
[ "$result" = "iyulabimages.azurecr.io/momos-host@sha256:already-pinned" ] \
  || fail "resolve_image_digest: 이미 digest인 참조를 그대로 통과시키지 못함: $result"

echo "OK: deploy-host.sh 단위 테스트 통과"
