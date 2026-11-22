#!/usr/bin/env bash
# Publish Kozynax.WebApi as a self-contained single-file Linux release and zip it.
#
# This is the OPTIONAL Web API build. It flips the WebApiSingleFile flag in the csproj, which adds
# the linux-x64 RID, switches to a self-contained publish and embeds every managed assembly into one
# executable (verified: runs without any runtime installed). The SDL/WinForms builds are unaffected.
#
# Usage: make-linux-singlefile.sh <version> <output-dir>
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${1:?version}"
OUT_DIR="${2:?output dir}"
mkdir -p "${OUT_DIR}"
OUT_DIR="$(cd "${OUT_DIR}" && pwd)"
APP="src/Kozynax.WebApi/Kozynax.WebApi.csproj"
ARCH="linux-x64"
BASENAME="Kozynax-${VERSION}-linux-x64-webapi"

STAGING="$(mktemp -d)"
trap 'rm -rf "${STAGING}"' EXIT
PUBLISH_DIR="${STAGING}/publish"

mkdir -p "${OUT_DIR}"

echo ">> Publishing single-file Web API (${ARCH}) -> ${PUBLISH_DIR}"
dotnet publish "${APP}" -c Release -p:Version="${VERSION}" \
  -p:WebApiSingleFile=true \
  -o "${PUBLISH_DIR}" | grep -Ev 'CA2022|IL3000' || true

# make-zip.sh strips pdbs, packs roms/ into ROMS.PAK and drops empty sidecar dirs.
bash "${ROOT}/packaging/make-zip.sh" \
  "${PUBLISH_DIR}" \
  "${BASENAME}" \
  "${OUT_DIR}"

echo ">> Artifact: ${OUT_DIR}/${BASENAME}.zip"
ls -la "${OUT_DIR}/${BASENAME}.zip"
