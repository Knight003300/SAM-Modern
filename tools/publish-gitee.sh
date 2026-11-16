#!/usr/bin/env bash
# Publish SAM-Modern to Gitee (idempotent mirror of tools/publish-github.sh).
#
#   GITEE_TOKEN=xxxx bash tools/publish-gitee.sh
#   (with no GITEE_TOKEN it reuses the token stored by the DSH Gitee connector)
#
# Gitee PAT needs the "projects" scope.
set -u

OWNER="${GITEE_OWNER:-arasy}"
NAME="${GITEE_REPO:-SAM-Modern}"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ZIP="$REPO_DIR/SAM-Modern-zhCN.zip"
META="$REPO_DIR/tools/release/meta.json"
API="https://gitee.com/api/v5"

[ -f "$META" ] || { echo "FAIL: missing $META"; exit 1; }

TOKEN="${GITEE_TOKEN:-}"
if [ -z "$TOKEN" ] && [ -f "$HOME/.dsh/storages/mcp_connector.json" ]; then
  TOKEN=$(node -e "let f=process.env.HOME+'/.dsh/storages/mcp_connector.json',c;try{c=JSON.parse(require('fs').readFileSync(f,'utf8'))}catch(e){process.exit(0)};const t=((c.tables||{}).connections||{})['gitee-gitee']||{};process.stdout.write((t.auth&&t.auth.bearerToken)||'')")
  [ -n "$TOKEN" ] && echo "note: using the Gitee PAT stored by the DSH Gitee connector"
fi
[ -z "$TOKEN" ] && { echo "FAIL: set GITEE_TOKEN"; exit 2; }
Q="?access_token=$TOKEN"

# meta_field <key>  -> prints the value (objects/arrays as JSON)
meta_field() {
  node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const m=JSON.parse(s);const v=m[process.argv[1]];process.stdout.write(typeof v==='string'?v:JSON.stringify(v))})" "$1" < "$META"
}

echo "== 1. repo check =="
code=$(curl -sL -o /dev/null -w '%{http_code}' "$API/repos/$OWNER/$NAME$Q")
echo "GET /repos/$OWNER/$NAME -> $code"
if [ "$code" = "404" ]; then
  echo "repo missing, creating..."
  body=$(node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const m=JSON.parse(s);process.stdout.write(JSON.stringify({name:m.name||process.argv[1],path:process.argv[1],description:m.description,private:false,auto_init:false,has_issues:true,has_wiki:false}))})" "$NAME" < "$META")
  ccode=$(curl -sL -o /tmp/gitee_create.json -w '%{http_code}' -X POST -H "Content-Type: application/json; charset=utf-8" -d "$body" "$API/user/repos$Q")
  echo "POST /user/repos -> $ccode"
  if [ "$ccode" != "201" ]; then head -c 400 /tmp/gitee_create.json; echo; echo "FAIL: cannot create repository"; exit 3; fi
  echo "repo created: $OWNER/$NAME"
elif [ "$code" != "200" ]; then
  echo "FAIL: cannot read repository (http $code)"; exit 3
fi

echo "== 2. push main =="
cd "$REPO_DIR" || exit 4
git remote get-url gitee >/dev/null 2>&1 || git remote add gitee "https://gitee.com/$OWNER/$NAME.git"
# Gitee over https wants user:password; the PAT is the password and the login the user
push_url="https://$OWNER:$TOKEN@gitee.com/$OWNER/$NAME.git"
GIT_TERMINAL_PROMPT=0 git push "$push_url" main --tags || { echo "FAIL: push rejected (Gitee token lacks projects scope?)"; exit 5; }
# keep the saved remote free of the credential
git remote set-url gitee "https://gitee.com/$OWNER/$NAME.git"

echo "== 3. repo metadata =="
# Gitee requires "name" on PATCH; topics/tags are not exposed by API v5.
patch_body=$(node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const m=JSON.parse(s);process.stdout.write(JSON.stringify({name:process.argv[1],description:m.description,homepage:m.homepage||'https://github.com/Knight003300/SAM-Modern',has_issues:true,has_wiki:false}))})" "$NAME" < "$META")
curl -sL -o /tmp/gitee_patch.json -w 'PATCH repo -> %{http_code}\n' -X PATCH \
  -H "Content-Type: application/json; charset=utf-8" -d "$patch_body" "$API/repos/$OWNER/$NAME$Q"

echo "== 4. release =="
tag=$(meta_field tag)
rel_body=$(node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const m=JSON.parse(s);process.stdout.write(JSON.stringify({tag_name:m.tag,name:m.releaseName,body:m.releaseBody,target_commitish:'main',prerelease:false}))})" < "$META")
rel=$(curl -sL -X POST -H "Content-Type: application/json; charset=utf-8" -d "$rel_body" "$API/repos/$OWNER/$NAME/releases$Q")
rel_id=$(printf '%s' "$rel" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{process.stdout.write(String(JSON.parse(s).id||''))}catch(e){}})")
if [ -z "$rel_id" ]; then
  echo "release create raw:"; printf '%s' "$rel" | head -c 300; echo
  echo "(if it already exists we reuse it)"
  rel_id=$(curl -sL "$API/repos/$OWNER/$NAME/releases/tags/$tag$Q" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{process.stdout.write(String(JSON.parse(s).id||''))}catch(e){}})")
  [ -n "$rel_id" ] && echo "release already exists: $tag (id $rel_id)"
fi
if [ -n "$rel_id" ] && [ -f "$ZIP" ]; then
  zip_win="$ZIP"
  command -v cygpath >/dev/null 2>&1 && zip_win="$(cygpath -w "$ZIP")"
  asset_name=SAM-Modern-zhCN.zip

  # same-name attachments cannot be replaced: drop every old copy first.
  # note: the release JSON exposes only name/url for assets (no id), so the
  # ids must come from the dedicated attach_files listing endpoint.
  old_ids=$(curl -sL "$API/repos/$OWNER/$NAME/releases/$rel_id/attach_files$Q" \
    | ASSET_NAME="$asset_name" node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const r=JSON.parse(s);const n=process.env.ASSET_NAME;r.filter(a=>a.name===n&&a.id).forEach(a=>console.log(a.id))}catch(e){}})")
  for id in $old_ids; do
    curl -sL -o /dev/null -w "delete old asset $id -> %{http_code}\n" -X DELETE \
      "$API/repos/$OWNER/$NAME/releases/$rel_id/attach_files/$id$Q"
  done

  curl -sL -o /tmp/gitee_asset.json -w "upload $asset_name -> %{http_code}\n" -X POST \
    -F "file=@$zip_win" "$API/repos/$OWNER/$NAME/releases/$rel_id/attach_files$Q"
  grep -o '"name": *"[^"]*\|"size": *[0-9]*\|browser_download_url": *"[^"]*' /tmp/gitee_asset.json | head -3
fi

echo "== done: https://gitee.com/$OWNER/$NAME =="
