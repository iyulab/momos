#!/usr/bin/env bash
set -euo pipefail

# Deploys momos-host by running database migrations as a separate step before
# traffic switches to the new revision. Since PostgreSQL (unlike SQLite-over-Azure-Files)
# tolerates multiple revisions connecting concurrently, migrations run once before
# the rolling deployment starts, then Container Apps' standard zero-downtime rollout
# takes over.

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
  local IMAGE="${1:?사용법: deploy-host.sh <image-ref>}"
  local DB_CONNECTION="${MOMOS_HOST_DB_CONNECTION:?MOMOS_HOST_DB_CONNECTION 환경변수(마이그레이션 대상 Postgres 연결 문자열)가 필요합니다.}"
  local DB_SECRET_NAME="${MOMOS_HOST_DB_SECRET_NAME:-momos-db-connection}"
  local KNOWLEDGE_SECRET_NAME="${MOMOS_HOST_KNOWLEDGE_SECRET_NAME:-momos-knowledge-connection}"

  echo "1/4 이미지 참조를 digest로 고정 (가변 태그면 지금 시점의 digest로 resolve)"
  IMAGE="$(resolve_image_digest "$IMAGE")"
  echo "  digest: $IMAGE"

  echo "2/4 트래픽 전환 전 마이그레이션 실행 — PostgreSQL은 다중 리비전이 동시에 연결돼도"
  echo "     문제가 없으므로(SQLite의 SMB 파일 락과 달리) 이 단계를 신규 리비전 기동과 분리해도 안전하다"
  dotnet ef database update \
    --project "$(dirname "${BASH_SOURCE[0]}")/../src/Momos.Host" \
    --connection "$DB_CONNECTION"

  echo "3/4 새 이미지로 표준 롤링 배포하며 동일 호출에서 DB 연결 문자열도 함께 갱신"
  echo "     (이미지와 환경변수를 별도 호출로 나누면 그 사이 리비전이 잘못된 연결 설정으로 트래픽을 받는다)"
  az containerapp update \
    --name "$APP" \
    --resource-group "$RG" \
    --image "$IMAGE" \
    --set-env-vars \
      "ConnectionStrings__MomosDb=secretref:${DB_SECRET_NAME}" \
      "Momos__Host__Knowledge__ConnectionString=secretref:${KNOWLEDGE_SECRET_NAME}" \
    >/dev/null

  echo "4/4 완료: $APP 이(가) $IMAGE (으)로 배포되었습니다."
}

if [[ "${BASH_SOURCE[0]:-$0}" == "${0}" ]]; then
  main "$@"
fi
