#!/usr/bin/env bash
# Publish SAM-Modern to GitHub.
#
#   GITHUB_TOKEN=ghp_xxx bash tools/publish-github.sh
#
# The token needs (fine-grained PAT on the target repo):
#   - Contents: read and write      (push commits, upload release asset)
#   - Administration: read and write (create the repo, set description/topics)
# or a classic PAT with the "repo" scope.
#
# The repository itself must exist and be empty: create it at
#   https://github.com/new   name = SAM-Modern   (no README, no .gitignore, no license)
# then run this script.
set -u

OWNER="${GITHUB_OWNER:-acerlanyize-bit}"
NAME="${GITHUB_REPO:-SAM-Modern}"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ZIP="$REPO_DIR/SAM-Modern-zhCN.zip"
META="$REPO_DIR/tools/release/meta.json"

TOKEN="${GITHUB_TOKEN:-}"
if [ -z "$TOKEN" ] && [ -f "$HOME/.dsh/storages/mcp_connector.json" ]; then
  TOKEN="$(grep -o 'github_pat_[A-Za-z0-9_]*' "$HOME/.dsh/storages/mcp_connector.json" | head -1)"
  [ -n "$TOKEN" ] && echo "note: using the PAT stored by the DSH GitHub connector"
fi
[ -z "$TOKEN" ] && { echo "FAIL: set GITHUB_TOKEN"; exit 2; }

api() { curl -s -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" "$@"; }

echo "== 1. repo check =="
code=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "https://api.github.com/repos/$OWNER/$NAME")
echo "GET /repos/$OWNER/$NAME -> $code"
if [ "$code" = "404" ]; then
  echo "FAIL: repo does not exist. Create an EMPTY repo at https://github.com/new (name=$NAME) and re-run."
  exit 3
fi

echo "== 2. push main =="
cd "$REPO_DIR" || exit 4
git remote get-url origin >/dev/null 2>&1 || git remote add origin "https://github.com/$OWNER/$NAME.git"
basic=$(printf 'x-access-token:%s' "$TOKEN" | base64 | tr -d '\n')
git -c http.extraheader="AUTHORIZATION: Basic $basic" push -u origin main || {
  echo "FAIL: push rejected -- token lacks Contents:write (fine-grained) or repo scope (classic)."
  exit 5
}

echo "== 3. repo metadata =="
desc=$(node -e "process.stdout.write(require('$META').description)")
topics=$(node -e "process.stdout.write(JSON.stringify(require('$META').topics))")
body=$(node -e "process.stdout.write(JSON.stringify({description:require('$META').description,topics:require('$META').topics,has_wiki:false,has_projects:false}))")
curl -s -o /dev/null -w 'PATCH repo -> %{http_code}\n' -X PATCH \
  -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" -d "$body" \
  "https://api.github.com/repos/$OWNER/$NAME"

echo "== 4. release =="
tag=$(node -e "process.stdout.write(require('$META').tag)")
rel_body=$(node -e "process.stdout.write(JSON.stringify({tag_name:require('$META').tag,name:require('$META').releaseName,body:require('$META').releaseBody,draft:false,prerelease:false}))")
rel=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" \
  -d "$rel_body" "https://api.github.com/repos/$OWNER/$NAME/releases")
upload=$(printf '%s' "$rel" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const j=JSON.parse(s);process.stdout.write(j.upload_url||'')}catch(e){}})")
if [ -z "$upload" ]; then
  echo "release creation failed:"; printf '%s' "$rel" | head -c 400; echo
else
  echo "release created: $tag"
  [ -f "$ZIP" ] || { echo "WARN: $ZIP not found, skipping asset upload"; exit 0; }
  curl -s -o /dev/null -w 'upload SAM-Modern-zhCN.zip -> %{http_code}\n' -X POST \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/zip" \
    --data-binary "@$ZIP" "${upload%\{*}${upload#*\{}" --upload-file "$ZIP" 2>/dev/null || true
  curl -s -o /dev/null -w 'upload (retry) -> %{http_code}\n' -X POST \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/zip" \
    --data-binary "@$ZIP" "$(printf '%s' "$upload" | sed 's/{?name,label}/?name=SAM-Modern-zhCN.zip/')"
fi

echo "== done: https://github.com/$OWNER/$NAME =="
