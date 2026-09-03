#!/usr/bin/env bash
set -euo pipefail

# Redeploys momos-host with a hard cutover instead of Container Apps' default
# zero-downtime revision overlap: the previous revision is fully stopped before the
# new one starts, so no two revisions ever hold the SQLite-over-Azure-Files database
# open at once. Trades a short deploy-time outage for that guarantee, which the platform's
# own overlapping rollout cannot give when the two revisions share one database file.

# 아래 세 개는 az cli 호출과 분리한 순수 문자열 파싱이라 단위 테스트 가능하다.
is_digest_reference() {
  case "$1" in
    *@sha256:*) return 0 ;;
    *) return 1 ;;
  esac
}

registry_host_of() {
  echo "${1%%/*}"
}

repo_and_tag_of() {
  echo "${1#*/}"
}

# `:latest` 같은 가변 태그는 나중에 같은 리비전을 재활성화할 때 그 시점의 `:latest`가 가리키는
# 걸 다시 pull한다 — "리비전 이름 = 실행 코드"라는 감사 전제가 깨진다(실측:
# 하루 사이 같은 리비전 이름이 서로 다른 이미지를 두 번 실행한 사례). 태그 참조를 배포 직전
# 실제 digest로 고정해서, 호출자가 태그를 넘기든 digest를 넘기든 항상 digest로만 배포되게 한다.
resolve_image_digest() {
  local image_ref="$1"
  if is_digest_reference "$image_ref"; then
    echo "$image_ref"
    return 0
  fi

  local registry_host acr_name repo_tag repo digest
  registry_host="$(registry_host_of "$image_ref")"
  acr_name="${registry_host%%.*}"
  repo_tag="$(repo_and_tag_of "$image_ref")"
  repo="${repo_tag%%:*}"

  digest="$(az acr repository show --name "$acr_name" --image "$repo_tag" --query digest -o tsv)"
  if [ -z "$digest" ]; then
    echo "이미지 '$image_ref'의 digest를 조회하지 못했습니다 (레지스트리=$acr_name, repo:tag=$repo_tag)" >&2
    return 1
  fi
  echo "${registry_host}/${repo}@${digest}"
}

main() {
  local RG="${MOMOS_HOST_RESOURCE_GROUP:-momos}"
  local APP="${MOMOS_HOST_APP_NAME:-momos-host}"
  local IMAGE="${1:?사용법: redeploy-host.sh <image-ref>}"

  # 리비전이 실제로 0 replica가 될 때까지 기다리는 상한(초). 넘기면 배포를 중단한다 —
  # 못 내려간 리비전을 남겨둔 채 새 리비전을 띄우는 것이 이 스크립트가 막으려는 바로 그 상황이다.
  local DRAIN_TIMEOUT_SECONDS="${MOMOS_HOST_DRAIN_TIMEOUT_SECONDS:-120}"

  echo "1/7 이미지 참조를 digest로 고정 (가변 태그면 지금 시점의 digest로 resolve)"
  IMAGE="$(resolve_image_digest "$IMAGE")"
  echo "  digest: $IMAGE"

  echo "2/7 리비전 모드를 multiple로 확인/전환 (이미 multiple이면 no-op)"
  az containerapp revision set-mode --name "$APP" --resource-group "$RG" --mode multiple >/dev/null

  echo "3/7 배포 전 active 리비전 목록 확보"
  local old_revisions
  old_revisions="$(az containerapp revision list --name "$APP" --resource-group "$RG" \
    --query "[?properties.active].name" -o tsv)"
  echo "  기존 active 리비전: ${old_revisions:-(없음)}"

  if [ -n "$old_revisions" ]; then
    echo "4/7 기존 리비전을 비활성화 — SQLite 파일을 새 리비전과 동시에 물지 않도록 먼저 완전히 내린다"
    while IFS= read -r rev; do
      [ -z "$rev" ] && continue
      az containerapp revision deactivate --name "$APP" --revision "$rev" --resource-group "$RG" </dev/null >/dev/null
      echo "  비활성화됨: $rev"
    done <<< "$old_revisions"

    # `revision deactivate`는 컨트롤 플레인이 요청을 접수한 시점에 반환한다 — 컨테이너는 그 뒤로도
    # SIGTERM + 유예시간만큼 더 살아 있다. 그 구간에 새 리비전을 띄우면 두 리비전이 같은 SQLite
    # 파일을 동시에 물게 되어, 이 스크립트가 존재하는 이유 자체가 사라진다. 그래서 접수가 아니라
    # replica 수가 실제로 0이 된 것을 확인한 뒤에만 다음 단계로 넘어간다.
    echo "5/7 비활성화한 리비전의 replica가 실제로 0이 될 때까지 대기 (리비전별 상한 ${DRAIN_TIMEOUT_SECONDS}초)"
    while IFS= read -r rev; do
      [ -z "$rev" ] && continue
      # 리비전마다 새로 계산 — 루프 밖에서 한 번만 계산하면 여러 리비전이 한 예산을 나눠 쓰게 되어,
      # 앞선 리비전이 느리게 내려가면 뒤 리비전은 자기 몫의 유예 없이 조기 타임아웃될 수 있다.
      local drain_deadline
      drain_deadline=$(( $(date +%s) + DRAIN_TIMEOUT_SECONDS ))
      while :; do
        # 완전히 내려간 리비전은 0을 주거나 아예 값을 주지 않는다(tsv에서 null은 빈 문자열,
        # 파이썬 None이 그대로 나오는 경우도 있다) — 셋 다 "내려갔다"로 취급한다.
        # 조회 자체가 실패하면 "내려갔다"로 넘기지 않고 계속 재시도한다 — 확인하지 못한 것을
        # 확인한 것으로 치는 순간 이 대기 단계는 아무것도 보장하지 못한다.
        local replicas
        if replicas="$(az containerapp revision show --name "$APP" --revision "$rev" --resource-group "$RG" \
          --query "properties.replicas" -o tsv </dev/null 2>/dev/null)"; then
          case "$replicas" in
            ""|0|None) break ;;
          esac
        else
          replicas="(조회 실패)"
        fi

        if [ "$(date +%s)" -ge "$drain_deadline" ]; then
          echo "리비전 $rev 이(가) 제한시간 안에 내려가지 않았습니다 (replicas=$replicas)." >&2
          echo "지금 새 리비전을 띄우면 SQLite 파일을 동시에 물게 되므로 배포를 중단합니다 — 수동 확인 후 다시 실행하세요." >&2
          exit 1
        fi
        sleep 5
      done
      echo "  드레인 완료: $rev"
    done <<< "$old_revisions"
  else
    echo "4/7 기존 active 리비전 없음 — 스킵(첫 배포)"
    echo "5/7 드레인 대기 없음 — 스킵(내릴 리비전이 없음)"
  fi

  echo "6/7 새 이미지로 리비전 생성: $IMAGE"
  az containerapp update --name "$APP" --resource-group "$RG" --image "$IMAGE" >/dev/null

  local new_revisions new_revision
  new_revisions="$(az containerapp revision list --name "$APP" --resource-group "$RG" \
    --query "[?properties.active].name" -o tsv)"
  new_revision="$(comm -13 <(echo "$old_revisions" | sort) <(echo "$new_revisions" | sort) | head -n1)"
  if [ -z "$new_revision" ]; then
    echo "새로 생긴 active 리비전을 찾지 못했습니다 — az containerapp revision list로 수동 확인 필요" >&2
    exit 1
  fi
  echo "  새 리비전: $new_revision"

  echo "7/7 트래픽을 새 리비전 100%로 고정"
  az containerapp ingress traffic set --name "$APP" --resource-group "$RG" \
    --revision-weight "${new_revision}=100" >/dev/null

  echo "완료: $new_revision(이)가 트래픽 100%를 받고 있습니다."
}

if [[ "${BASH_SOURCE[0]:-$0}" == "${0}" ]]; then
  main "$@"
fi
