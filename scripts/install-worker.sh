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

fetch_and_extract() {
  local rid="$1" tag="$2" install_dir="$3"
  local asset_name="momos-worker-${rid}.tar.gz"
  local tarball_url checksum_url tmp_dir

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

  mkdir -p "$install_dir"
  tar -xzf "$tmp_dir/archive.tar.gz" -C "$install_dir"
  rm -rf "$tmp_dir"
}

main() {
  local rid tag
  rid="$(detect_platform)"
  tag="$(resolve_release_tag "$VERSION")"
  fetch_and_extract "$rid" "$tag" "$INSTALL_DIR"

  echo "설치 완료: $INSTALL_DIR"
  echo "실행: $INSTALL_DIR/Momos.Worker"
}

if [[ "${BASH_SOURCE[0]:-$0}" == "${0}" ]]; then
  main "$@"
fi
