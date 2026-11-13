#!/usr/bin/env bash
set -euo pipefail

repair_updater=false
prerequisites_only=false
requested_version=""
# The optional URL shortener. Empty means "ask, or leave it alone"; a domain
# means install it for that domain without asking.
shortener_domain=""
shortener_requested=false
while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --with-shortener)
      if [[ "$#" -lt 2 ]]; then
        echo "--with-shortener requires the short domain, such as go.example.org."
        exit 1
      fi
      shortener_domain="$2"
      shortener_requested=true
      shift 2
      ;;
    --repair-updater)
      repair_updater=true
      shift
      ;;
    --prerequisites-only)
      prerequisites_only=true
      shift
      ;;
    --version)
      if [[ "$#" -lt 2 ]]; then
        echo "--version requires a release tag such as v0.40.18."
        exit 1
      fi
      requested_version="$2"
      shift 2
      ;;
    *)
      echo "Unknown option: $1"
      echo "Usage: install-latest.sh [--repair-updater] [--prerequisites-only] [--version vX.Y.Z] [--with-shortener DOMAIN]"
      exit 1
      ;;
  esac
done

case "$(uname -m)" in
  x86_64|amd64) runtime="linux-x64" ;;
  aarch64|arm64) runtime="linux-arm64" ;;
  *) echo "Unsupported CPU architecture: $(uname -m)"; exit 1 ;;
esac

if (( EUID != 0 )) && ! command -v sudo >/dev/null 2>&1; then
  echo "This installer needs root or sudo. Install sudo and rerun it." >&2
  exit 1
fi

run_root() {
  if [[ "${EUID}" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

if [[ "${EUID}" -ne 0 ]]; then
  sudo -v
fi

if [[ ! -f /etc/os-release ]]; then
  echo "LessonCue supports Ubuntu and Debian hosts; /etc/os-release is missing." >&2
  exit 1
fi
# shellcheck disable=SC1091
source /etc/os-release
case "${ID:-}" in
  ubuntu|debian) ;;
  *)
    echo "LessonCue's Linux installer supports Ubuntu and Debian; detected '${ID:-unknown}'." >&2
    exit 1
    ;;
esac
if ! command -v apt-get >/dev/null 2>&1 || ! command -v apt-cache >/dev/null 2>&1; then
  echo "The Ubuntu/Debian apt package manager is required." >&2
  exit 1
fi

apt_package_available() {
  apt-cache show "$1" >/dev/null 2>&1
}

add_first_package_if_available() {
  local candidate
  for candidate in "$@"; do
    if apt_package_available "${candidate}"; then
      packages+=("${candidate}")
      return 0
    fi
  done
}

install_docker_repository() {
  local codename="${VERSION_CODENAME:-}"
  local architecture
  if [[ -z "${codename}" ]] && command -v lsb_release >/dev/null 2>&1; then
    codename="$(lsb_release -cs)"
  fi
  if [[ -z "${codename}" ]] || ! command -v dpkg >/dev/null 2>&1; then
    echo "Could not determine the Docker apt repository for this host." >&2
    exit 1
  fi
  architecture="$(dpkg --print-architecture)"
  run_root install -m 0755 -d /etc/apt/keyrings
  curl -fsSL "https://download.docker.com/linux/${ID}/gpg" | \
    run_root gpg --dearmor --yes -o /etc/apt/keyrings/docker.gpg
  run_root chmod a+r /etc/apt/keyrings/docker.gpg
  printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/%s %s stable\n' \
    "${architecture}" "${ID}" "${codename}" | \
    run_root tee /etc/apt/sources.list.d/docker.list >/dev/null
  run_root apt-get update
}

install_prerequisites() {
  local docker_present=false
  local compose_present=false
  local docker_repo_required=false
  local docker_compose_package=""
  local -a packages=(curl ca-certificates git gnupg openssl)

  # Refresh package metadata before probing optional package names. On a
  # fresh host the cache may be empty or stale, which would otherwise make a
  # package that is available on the distribution look missing.
  run_root apt-get update

  if [[ "${repair_updater}" != true ]]; then
    packages+=(ffmpeg libreoffice-impress libreoffice-writer libreoffice-calc libreoffice-draw poppler-utils avahi-daemon avahi-utils libnss-mdns libicu-dev zlib1g util-linux)
    # Debian splits runuser into util-linux-extra on some releases. The
    # packaged installer uses runuser to validate the media worker, so install
    # the split package whenever the host publishes it.
    add_first_package_if_available util-linux-extra
    # FFmpeg's codec set is supplied by the distribution build. Install the
    # optional runtime/codec packages when this Debian/Ubuntu release names
    # them, so WebP, Ogg/Theora, HEIF/AVIF, JPEG XL, and legacy Office samples
    # can be exercised without hand-repairing the host afterward.
    # Package names for codec ABI versions vary between Ubuntu/Debian
    # releases. Pick the first name the host actually publishes instead of
    # making an otherwise optional codec block the whole installer.
    add_first_package_if_available libavcodec-extra libavcodec-extra62 libavcodec-extra61 libavcodec-extra60 libavcodec-extra59
    add_first_package_if_available libwebp7 libwebp6
    add_first_package_if_available libtheora0
    add_first_package_if_available libvpx9 libvpx8 libvpx7
    add_first_package_if_available libopus0
    add_first_package_if_available libvorbis0a
    add_first_package_if_available libheif2 libheif1
    add_first_package_if_available libavif16 libavif15
    add_first_package_if_available libjxl0 libjxl0.7
    add_first_package_if_available libopenjp2-8 libopenjp2-7
    add_first_package_if_available fonts-liberation
  fi

  command -v docker >/dev/null 2>&1 && docker_present=true
  if [[ "${docker_present}" == true ]] && docker compose version >/dev/null 2>&1; then
    compose_present=true
  fi

  # Use distribution packages where available. Debian releases that do not
  # ship the v2 Compose plugin fall through to Docker's signed repository.
  if [[ "${compose_present}" != true ]]; then
    for candidate in docker-compose-v2 docker-compose-plugin; do
      if apt_package_available "${candidate}"; then
        docker_compose_package="${candidate}"
        break
      fi
    done
  fi
  if [[ "${docker_present}" != true && -z "${docker_compose_package}" ]]; then
    docker_repo_required=true
  elif [[ "${docker_present}" != true ]]; then
    packages+=(docker.io)
  elif [[ "${compose_present}" != true && -z "${docker_compose_package}" ]]; then
    docker_repo_required=true
  fi
  if [[ -n "${docker_compose_package}" ]]; then
    packages+=("${docker_compose_package}")
  fi

  run_root apt-get install -y "${packages[@]}"

  if [[ "${docker_repo_required}" == true ]]; then
    install_docker_repository
    if [[ "${docker_present}" != true ]]; then
      run_root apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
    else
      run_root apt-get install -y docker-compose-plugin
    fi
  fi

  if ! command -v docker >/dev/null 2>&1; then
    echo "Docker installation completed without a docker executable." >&2
    exit 1
  fi
  run_root systemctl enable --now docker
  if ! run_root docker compose version >/dev/null 2>&1; then
    echo "Docker Compose v2 is unavailable after prerequisite installation." >&2
    exit 1
  fi
  echo "Installed prerequisites: curl, git, Docker Engine, Docker Compose, FFmpeg/FFprobe, Poppler, LibreOffice, and available optional media codecs."
}

install_prerequisites

if [[ "${prerequisites_only}" == true ]]; then
  echo "LessonCue prerequisites are installed."
  exit 0
fi

if [[ "${repair_updater}" != true && "${runtime}" == "linux-x64" ]]; then
  if apt-cache show intel-media-va-driver >/dev/null 2>&1; then
    run_root apt-get install -y intel-media-va-driver
  elif apt-cache show intel-media-va-driver-non-free >/dev/null 2>&1; then
    run_root apt-get install -y intel-media-va-driver-non-free
  fi
  if apt-cache show i965-va-driver >/dev/null 2>&1; then
    run_root apt-get install -y i965-va-driver
  fi
fi

if [[ -n "${requested_version}" ]]; then
  version="${requested_version}"
  if [[ "${version}" != v* ]]; then version="v${version}"; fi
else
  version_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' 'https://github.com/ProbablyModded/lessoncue/releases/latest')"
  version="${version_url##*/}"
fi
if [[ ! "${version}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z]+)*$ ]]; then
  echo "LessonCue rejected the requested release because '${version}' is not a valid version tag."
  exit 1
fi
# ---------------------------------------------------------------- shortener
#
# Optional, and never assumed. An installation that says no here is untouched,
# and one that says yes gets the container stack alongside the native server.
# The answer is remembered so an update can keep it running.
SHORTENER_MARKER="/var/lib/lessoncue/config/shortener-domain"
SHORTENER_DIR="/opt/lessoncue-shortener"
SHORTENER_DATA="/var/lib/lessoncue/shortener"

install_shortener_if_wanted() {
  local server_ip="$1"
  local domain="${shortener_domain}"

  # Already set up: honour the recorded answer rather than asking again.
  if [[ -z "${domain}" && -s "${SHORTENER_MARKER}" ]]; then
    domain="$(cat "${SHORTENER_MARKER}")"
  elif [[ -z "${domain}" && "${shortener_requested}" != true ]]; then
    if [[ ! -t 0 ]]; then
      return 0
    fi
    echo
    echo "Install the optional self-hosted URL shortener?"
    echo "It gives short links on a domain of your own, and short codes for joining games."
    read -r -p "Short domain (blank to skip): " domain || true
    domain="$(echo "${domain}" | tr -d '[:space:]')"
  fi

  [[ -z "${domain}" ]] && return 0

  if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
    echo "The URL shortener needs Docker with the Compose plugin, which is not installed."
    echo "Install it, then run: sudo ${SHORTENER_DIR}/install.sh ${domain}"
    # Remember the answer anyway, so a later update can finish the job.
    install -d -m 755 "$(dirname "${SHORTENER_MARKER}")"
    printf '%s\n' "${domain}" > "${SHORTENER_MARKER}"
    chmod 600 "${SHORTENER_MARKER}"
    return 0
  fi

  echo
  echo "Installing the URL shortener for ${domain}"

  # Fetched and checked against the same signed SHA256SUMS as the server
  # itself. An unverified download here would undo the point of signing them.
  local bundle="LessonCue-URL-Shortener.tar.gz"
  if ! curl -fL "${release_root}/${bundle}" -o "${workdir}/${bundle}" 2>/dev/null; then
    echo "This release does not carry the URL shortener bundle. Skipping."
    return 0
  fi
  if ! grep -F "  ${bundle}" "${workdir}/SHA256SUMS" > "${workdir}/shortener.sha256"; then
    echo "The URL shortener bundle is not listed in the signed checksums. Skipping."
    return 0
  fi
  if ! ( cd "${workdir}" && sha256sum -c shortener.sha256 >/dev/null ); then
    echo "The URL shortener bundle did not match its signed checksum. Skipping."
    return 0
  fi

  install -d -m 755 "${SHORTENER_DIR}"
  if ! tar -xzf "${workdir}/${bundle}" -C "${SHORTENER_DIR}" --strip-components=1; then
    echo "The URL shortener bundle could not be unpacked. Skipping."
    return 0
  fi
  chmod +x "${SHORTENER_DIR}"/*.sh 2>/dev/null || true

  # Remembered so the updater keeps it running without asking again.
  install -d -m 755 "$(dirname "${SHORTENER_MARKER}")"
  printf '%s\n' "${domain}" > "${SHORTENER_MARKER}"
  chmod 600 "${SHORTENER_MARKER}"

  # Data under the service's own directory, and the integration key where
  # LessonCue actually reads it rather than beside the compose file.
  install -d -m 750 "${SHORTENER_DATA}"
  # Passed through so the bare short domain forwards from the first start,
  # rather than needing a container restart after setting it in LessonCue.
  if [[ "${SHORT_DOMAIN_ROOT_REDIRECT+x}" == x ]]; then
    shortener_install=(SHORTENER_DATA_DIR="${SHORTENER_DATA}" LESSONCUE_DATA_PATH="/var/lib/lessoncue"
      SHORT_DOMAIN_ROOT_REDIRECT="${SHORT_DOMAIN_ROOT_REDIRECT}")
  else
    shortener_install=(SHORTENER_DATA_DIR="${SHORTENER_DATA}" LESSONCUE_DATA_PATH="/var/lib/lessoncue")
  fi
  if ( cd "${SHORTENER_DIR}" && env "${shortener_install[@]}" ./install.sh "${domain}" ); then
    echo
    echo "Add these two routes to the Cloudflare Tunnel already serving LessonCue:"
    echo "    ${domain}          ->  http://${server_ip}:8081"
    echo "    short.${domain}    ->  http://${server_ip}:8082"
    echo "Then open Settings -> Integrations - URL shortener in LessonCue."
  else
    echo "The URL shortener did not finish installing. LessonCue itself is unaffected."
  fi
}

workdir="$(mktemp -d)"
trap 'rm -rf "${workdir}"' EXIT

echo "Downloading LessonCue ${version} for ${runtime}..."
asset="LessonCue-Server-${runtime}.tar.gz"
release_root="https://github.com/ProbablyModded/lessoncue/releases/download/${version}"
curl -fL "${release_root}/${asset}" -o "${workdir}/${asset}"
curl -fL "${release_root}/SHA256SUMS" -o "${workdir}/SHA256SUMS"
curl -fL "${release_root}/SHA256SUMS.sig" -o "${workdir}/SHA256SUMS.sig"
cat > "${workdir}/release-signing-public.pem" <<'LESSONCUE_RELEASE_PUBLIC_KEY'
-----BEGIN PUBLIC KEY-----
MCowBQYDK2VwAyEAbIUbCwXYdmzGOMbcmae+1fQdBoNm7im865p2Xpi6ON8=
-----END PUBLIC KEY-----
LESSONCUE_RELEASE_PUBLIC_KEY
if ! openssl pkeyutl -verify -pubin \
  -inkey "${workdir}/release-signing-public.pem" \
  -rawin -in "${workdir}/SHA256SUMS" \
  -sigfile "${workdir}/SHA256SUMS.sig"; then
  echo "LessonCue rejected this release because its Ed25519 signature is invalid."
  exit 1
fi
if ! grep -F "  ${asset}" "${workdir}/SHA256SUMS" > "${workdir}/asset.sha256"; then
  echo "The signed release manifest does not contain ${asset}."
  exit 1
fi
(cd "${workdir}" && sha256sum -c asset.sha256)
mv "${workdir}/${asset}" "${workdir}/lessoncue.tar.gz"
tar -xzf "${workdir}/lessoncue.tar.gz" -C "${workdir}"

if [[ "${repair_updater}" == true ]]; then
  for required in \
    repair-updater.sh \
    lessoncue-update; do
    if [[ ! -x "${workdir}/${required}" ]]; then
      echo "The signed LessonCue release is missing ${required}."
      exit 1
    fi
  done
  run_root "${workdir}/repair-updater.sh" "${version}"
  exit 0
fi

run_root "${workdir}/install.sh"

http_port="$(run_root cat /var/lib/lessoncue/config/http-port 2>/dev/null || printf '80')"
if [[ "${http_port}" == "80" ]]; then port_suffix=""; else port_suffix=":${http_port}"; fi
normalize_local_hostname() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  value="$(printf '%s' "${value}" | tr '[:upper:]' '[:lower:]')"
  if [[ "${value}" == *.local ]]; then value="${value:0:${#value}-6}"; fi
  printf '%s' "${value}"
}
local_hostname="$(normalize_local_hostname "$(run_root cat /var/lib/lessoncue/config/local-hostname 2>/dev/null || printf 'lessoncue')")"
if [[ ! "${local_hostname}" =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]]; then local_hostname=lessoncue; fi
for attempt in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:${http_port}/health/ready" >/dev/null; then
    server_ip="$(hostname -I | awk '{print $1}')"
    echo
    echo "LessonCue is ready."
    echo "Open http://${local_hostname}.local${port_suffix} in a browser on the same network."
    echo "Numeric fallback: http://${server_ip}${port_suffix}"
    install_shortener_if_wanted "${server_ip}"
    exit 0
  fi
  sleep 1
done

echo "LessonCue did not become healthy within 30 seconds."
echo "Run: sudo journalctl -u lessoncue -n 100 --no-pager"
exit 1
