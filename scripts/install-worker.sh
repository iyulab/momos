#!/usr/bin/env bash
set -euo pipefail

REPO="iyulab/momos"
INSTALL_DIR="${MOMOS_WORKER_INSTALL_DIR:-$HOME/.local/share/momos-worker}"
VERSION="${MOMOS_WORKER_VERSION:-latest}"

detect_platform() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  if [ "$os" != "Linux" ]; then
    echo "지원하지 않는 OS입니다: $os (linux-x64만 지원 — Windows는 install-worker.ps1을 사용하세요)" >&2
    return 1
  fi
  if [ "$arch" != "x86_64" ]; then
    echo "지원하지 않는 아키텍처입니다: $arch (x86_64만 지원)" >&2
    return 1
  fi
  echo "linux-x64"
}

resolve_release_tag() {
  local version="$1"
  if [ "$version" != "latest" ]; then
    echo "worker-v${version}"
    return 0
  fi
  local tag
  tag="$(curl -fsSL "https://api.github.com/repos/${REPO}/releases" \
    | grep -o '"tag_name": *"worker-v[^"]*"' \
    | head -n1 \
    | sed -E 's/.*"(worker-v[^"]*)".*/\1/' || true)"
  if [ -z "$tag" ]; then
    echo "worker-v* 태그를 가진 릴리스를 찾을 수 없습니다 (repo=$REPO)" >&2
    return 1
  fi
  echo "$tag"
}

# stdin으로 GitHub Release API 응답(JSON)을 받아, 지정한 자산 이름의 다운로드 URL을 출력한다.
extract_asset_url() {
  local asset_name="$1"
  grep -o "\"browser_download_url\": *\"[^\"]*${asset_name}\"" \
    | head -n1 \
    | sed -E 's/.*"(https:[^"]+)"/\1/' || true
}

resolve_asset_url() {
  local tag="$1" asset_name="$2"
  local url
  url="$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/tags/${tag}" | extract_asset_url "$asset_name")"
  if [ -z "$url" ]; then
    echo "릴리스 '${tag}'에서 자산 '${asset_name}'을 찾을 수 없습니다" >&2
    return 1
  fi
  echo "$url"
}

verify_checksum() {
  local file="$1" checksum_file="$2"
  local expected actual
  expected="$(cut -d' ' -f1 "$checksum_file")"
  actual="$(sha256sum "$file" | cut -d' ' -f1)"
  [ "$expected" = "$actual" ]
}

json_escape() {
  local s="$1"
  s="${s//\\/\\\\}"
  s="${s//\"/\\\"}"
  printf '%s' "$s"
}

write_config() {
  local dir="$1" base_url="$2" api_key="$3" gpustack_endpoint="$4" gpustack_api_key="$5" gpustack_model="$6"
  base_url="$(json_escape "$base_url")"
  api_key="$(json_escape "$api_key")"
  gpustack_endpoint="$(json_escape "$gpustack_endpoint")"
  gpustack_api_key="$(json_escape "$gpustack_api_key")"
  gpustack_model="$(json_escape "$gpustack_model")"
  ( umask 077
    cat > "$dir/appsettings.Production.json" <<JSON
{
  "Momos": {
    "Worker": {
      "Host": {
        "BaseUrl": "${base_url}",
        "ApiKey": "${api_key}"
      }
    },
    "Llm": {
      "GpuStack": {
        "Endpoint": "${gpustack_endpoint}",
        "ApiKey": "${gpustack_api_key}",
        "Model": "${gpustack_model}"
      }
    }
  }
}
JSON
  )
  chmod 600 "$dir/appsettings.Production.json"
}

configure_worker() {
  local install_dir="$1"
  local config_path="$install_dir/appsettings.Production.json"

  if [ -f "$config_path" ]; then
    echo "기존 설정 파일을 유지합니다: $config_path"
    return 0
  fi

  local base_url="${MOMOS_WORKER_HOST_BASEURL:-}"
  local api_key="${MOMOS_WORKER_HOST_APIKEY:-}"
  local gpustack_endpoint="${MOMOS_LLM_GPUSTACK_ENDPOINT:-}"
  local gpustack_api_key="${MOMOS_LLM_GPUSTACK_APIKEY:-}"
  local gpustack_model="${MOMOS_LLM_GPUSTACK_MODEL:-}"

  if [ -z "$base_url" ] && { [ -t 0 ] && exec 3<&0 || exec 3</dev/tty; } 2>/dev/null; then
    read -rp "Momos Host BaseUrl: " base_url <&3
    read -rsp "Momos Worker API Key: " api_key <&3; echo
    read -rp "GPUStack Endpoint: " gpustack_endpoint <&3
    read -rsp "GPUStack API Key: " gpustack_api_key <&3; echo
    read -rp "GPUStack Model: " gpustack_model <&3
    exec 3<&-
  fi

  write_config "$install_dir" "$base_url" "$api_key" "$gpustack_endpoint" "$gpustack_api_key" "$gpustack_model"

  if [ -z "$base_url" ] || [ -z "$api_key" ] || [ -z "$gpustack_endpoint" ] || [ -z "$gpustack_api_key" ] || [ -z "$gpustack_model" ]; then
    echo "일부 설정값이 비어 있습니다 — $config_path 를 직접 채운 뒤 실행하세요."
  fi
}

# "Restart=always" 등 systemd 유닛 텍스트 자체 — 파일에 쓰는 부분과 분리해서 순수 문자열로
# 테스트할 수 있게 했다.
render_systemd_unit() {
  local install_dir="$1" run_user="$2"
  cat <<UNIT
[Unit]
Description=Momos Worker
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${run_user}
ExecStart=${install_dir}/current/Momos.Worker
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
}

# Windows 쪽(install-worker.ps1)은 기본값이 "등록함"(옵트아웃)인데 여기는 기본값이 "건너뜀"(옵트인)이다
# — 비대칭은 의도적이다. sudo는 tty/비밀번호 프롬프트가 없는 환경(curl | bash로 파이프된 무인 설치)에서
# 조용히 멈추거나 set -e 아래서 스크립트 전체를 죽일 수 있는데, 이 스크립트가 이미 그 계열의 결함(tty
# 감지 회귀)을 한 번 겪었다 — 관리자 PowerShell은 이미 "사용자가 의식적으로 권한 상승을 각오한" 맥락인
# Windows New-Service와 달리, 여기서는 그 전제를 세울 수 없어 명시적 옵트인으로 막는다.
install_worker_service() {
  local install_dir="$1"
  local unit_path="/etc/systemd/system/momos-worker.service"

  if [ "${MOMOS_WORKER_INSTALL_SERVICE:-}" != "1" ]; then
    echo "systemd 서비스 등록을 건너뜁니다(MOMOS_WORKER_INSTALL_SERVICE=1로 opt-in) — 실행: $install_dir/current/Momos.Worker"
    return 0
  fi

  if [ "$(id -u)" -ne 0 ] && ! command -v sudo >/dev/null 2>&1; then
    echo "root 권한도 sudo도 없어 systemd 서비스 등록을 건너뜁니다 — 위 유닛 내용을 직접 $unit_path 에 만드세요." >&2
    return 0
  fi

  local sudo_cmd=()
  [ "$(id -u)" -ne 0 ] && sudo_cmd=(sudo)

  render_systemd_unit "$install_dir" "$(id -un)" | "${sudo_cmd[@]}" tee "$unit_path" > /dev/null
  "${sudo_cmd[@]}" systemctl daemon-reload
  "${sudo_cmd[@]}" systemctl enable momos-worker.service
  if ! systemctl is-active --quiet momos-worker.service; then
    "${sudo_cmd[@]}" systemctl start momos-worker.service
  fi
  echo "서비스 실행 중: momos-worker.service"
}

fetch_and_extract() {
  local rid="$1" tag="$2" install_dir="$3"
  local asset_name="momos-worker-${rid}.tar.gz"
  local tarball_url checksum_url tmp_dir version version_dir

  tarball_url="$(resolve_asset_url "$tag" "$asset_name")"
  checksum_url="$(resolve_asset_url "$tag" "${asset_name}.sha256")"

  tmp_dir="$(mktemp -d)"

  echo "다운로드 중: $tarball_url"
  curl -fsSL -o "$tmp_dir/archive.tar.gz" "$tarball_url"
  curl -fsSL -o "$tmp_dir/archive.tar.gz.sha256" "$checksum_url"

  if ! verify_checksum "$tmp_dir/archive.tar.gz" "$tmp_dir/archive.tar.gz.sha256"; then
    echo "체크섬이 일치하지 않습니다 — 설치를 중단합니다." >&2
    rm -rf "$tmp_dir"
    return 1
  fi

  # worker-v0.1.0 -> 0.1.0
  version="${tag#worker-v}"
  version_dir="$install_dir/installs/$version"
  mkdir -p "$version_dir"
  tar -xzf "$tmp_dir/archive.tar.gz" -C "$version_dir"
  rm -rf "$tmp_dir"

  ln -sfn "$version_dir" "$install_dir/current"
}

main() {
  local rid tag
  rid="$(detect_platform)"
  tag="$(resolve_release_tag "$VERSION")"
  fetch_and_extract "$rid" "$tag" "$INSTALL_DIR"
  configure_worker "$INSTALL_DIR"
  install_worker_service "$INSTALL_DIR"

  echo "설치 완료: $INSTALL_DIR"
}

if [[ "${BASH_SOURCE[0]:-$0}" == "${0}" ]]; then
  main "$@"
fi
