#!/usr/bin/env bash
set -euo pipefail

# Redeploys momos-host with a hard cutover instead of Container Apps' default
# zero-downtime revision overlap: the previous revision is fully stopped before the
# new one starts, so no two revisions ever hold the SQLite-over-Azure-Files database
# open at once. Trades a short deploy-time outage for that guarantee — see the
# Worker/Host update strategy design this script implements.

RG="${MOMOS_HOST_RESOURCE_GROUP:-momos}"
APP="${MOMOS_HOST_APP_NAME:-momos-host}"
IMAGE="${1:?사용법: redeploy-host.sh <image-ref>}"

echo "1/5 리비전 모드를 multiple로 확인/전환 (이미 multiple이면 no-op)"
az containerapp revision set-mode --name "$APP" --resource-group "$RG" --mode multiple >/dev/null

echo "2/5 배포 전 active 리비전 목록 확보"
old_revisions="$(az containerapp revision list --name "$APP" --resource-group "$RG" \
  --query "[?properties.active].name" -o tsv)"
echo "  기존 active 리비전: ${old_revisions:-(없음)}"

if [ -n "$old_revisions" ]; then
  echo "3/5 기존 리비전을 비활성화 — SQLite 파일을 새 리비전과 동시에 물지 않도록 먼저 완전히 내린다"
  while IFS= read -r rev; do
    [ -z "$rev" ] && continue
    az containerapp revision deactivate --revision "$rev" --resource-group "$RG" >/dev/null
    echo "  비활성화됨: $rev"
  done <<< "$old_revisions"
else
  echo "3/5 기존 active 리비전 없음 — 스킵(첫 배포)"
fi

echo "4/5 새 이미지로 리비전 생성: $IMAGE"
az containerapp update --name "$APP" --resource-group "$RG" --image "$IMAGE" >/dev/null

new_revisions="$(az containerapp revision list --name "$APP" --resource-group "$RG" \
  --query "[?properties.active].name" -o tsv)"
new_revision="$(comm -13 <(echo "$old_revisions" | sort) <(echo "$new_revisions" | sort) | head -n1)"
if [ -z "$new_revision" ]; then
  echo "새로 생긴 active 리비전을 찾지 못했습니다 — az containerapp revision list로 수동 확인 필요" >&2
  exit 1
fi
echo "  새 리비전: $new_revision"

echo "5/5 트래픽을 새 리비전 100%로 고정"
az containerapp ingress traffic set --name "$APP" --resource-group "$RG" \
  --revision-weight "${new_revision}=100" >/dev/null

echo "완료: $new_revision(이)가 트래픽 100%를 받고 있습니다."
