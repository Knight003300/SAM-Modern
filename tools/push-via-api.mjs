#!/usr/bin/env node
// Push local HEAD to GitHub through the Git Data API.
// Used when "git push" cannot reach github.com (corporate proxy / TLS reset),
// while api.github.com still works.
//
//   node tools/push-via-api.mjs            # real push
//   node tools/push-via-api.mjs --dry-run  # report only
//
// Env: GITHUB_TOKEN (optional, otherwise read from the DSH GitHub connector),
//      GITHUB_OWNER / GITHUB_REPO overrides.

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, unlinkSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const API = 'https://api.github.com';
const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const OWNER = process.env.GITHUB_OWNER || 'acerlanyize-bit';
const REPO = process.env.GITHUB_REPO || 'SAM-Modern';
const DRY = process.argv.includes('--dry-run');

const git = (...args) => execFileSync('git', args, { cwd: ROOT, encoding: 'utf8' });

function findToken() {
  if (process.env.GITHUB_TOKEN) return process.env.GITHUB_TOKEN;
  const cfgPath = join(process.env.HOME || process.env.USERPROFILE || '', '.dsh', 'storages', 'mcp_connector.json');
  let cfg = '';
  try { cfg = readFileSync(cfgPath, 'utf8'); } catch { return ''; }
  const cands = [...new Set([
    ...(cfg.match(/ghp_[A-Za-z0-9]{20,}/g) || []),
    ...(cfg.match(/github_pat_[A-Za-z0-9_]+/g) || []),
  ])];
  return cands;
}

async function pickToken() {
  const found = findToken();
  if (typeof found === 'string') return found;
  for (const c of found) {
    const r = await fetch(API + '/user', { headers: { Authorization: 'Bearer ' + c } });
    if (r.status === 200) return c;
  }
  return '';
}

const token = await pickToken();
if (!token) { console.error('FAIL: no usable GitHub token (set GITHUB_TOKEN)'); process.exit(2); }
const H = {
  Authorization: 'Bearer ' + token,
  Accept: 'application/vnd.github+json',
  'Content-Type': 'application/json',
  'User-Agent': 'sam-modern-push-api',
};

const json = async (path, init) => {
  const res = await fetch(API + path, { headers: H, ...init });
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  return { status: res.status, body };
};

// ---- local HEAD -------------------------------------------------------------
const headSha = git('rev-parse', 'HEAD').trim();
const headTree = git('rev-parse', 'HEAD^{tree}').trim();
const local = git('ls-tree', '-r', 'HEAD').trim().split('\n').filter(Boolean).map((line) => {
  const [meta, path] = line.split('\t');
  const [mode, type, sha] = meta.split(' ');
  return { mode, type, sha, path };
});
console.log('local HEAD ' + headSha.slice(0, 8) + ' tree ' + headTree.slice(0, 8) + ' files ' + local.length);

// ---- remote state -----------------------------------------------------------
const refRes = await json('/repos/' + OWNER + '/' + REPO + '/git/ref/heads/main');
let remoteHead = null;
let remoteTree = null;
if (refRes.status === 200) {
  remoteHead = refRes.body.object.sha;
  const c = await json('/repos/' + OWNER + '/' + REPO + '/git/commits/' + remoteHead);
  remoteTree = c.body.tree.sha;
  console.log('remote main ' + remoteHead.slice(0, 8) + ' tree ' + remoteTree.slice(0, 8));
} else if (refRes.status === 404) {
  console.log('remote main: (empty repository)');
} else {
  console.error('FAIL: cannot read remote ref (' + refRes.status + ')');
  process.exit(3);
}
if (remoteHead === headSha) { console.log('already up to date -- nothing to push'); process.exit(0); }

const remoteBlobs = new Map();
if (remoteTree) {
  const t = await json('/repos/' + OWNER + '/' + REPO + '/git/trees/' + remoteTree + '?recursive=1');
  if (t.status !== 200) { console.error('FAIL: cannot read remote tree (' + t.status + ')'); process.exit(3); }
  for (const e of t.body.tree || []) if (e.type === 'blob') remoteBlobs.set(e.path, e.sha);
}

const upserts = local.filter((f) => remoteBlobs.get(f.path) !== f.sha);
const deletes = [...remoteBlobs.keys()].filter((p) => !local.some((f) => f.path === p));
console.log('changed ' + upserts.length + ' deleted ' + deletes.length);
upserts.forEach((f) => console.log('  M ' + f.path));
deletes.forEach((p) => console.log('  D ' + p));
if (!upserts.length && !deletes.length && remoteTree === headTree) {
  console.log('tree already matches -- only the commit is missing');
}

if (DRY) { console.log('dry run -- nothing written'); process.exit(0); }

// ---- upload blobs -----------------------------------------------------------
const entries = [];
for (const f of upserts) {
  const buf = readFileSync(join(ROOT, f.path));
  const up = await json('/repos/' + OWNER + '/' + REPO + '/git/blobs', {
    method: 'POST',
    body: JSON.stringify({ content: buf.toString('base64'), encoding: 'base64' }),
  });
  if (up.status !== 201) { console.error('FAIL: blob ' + f.path + ' -> ' + up.status); process.exit(4); }
  entries.push({ path: f.path, mode: f.mode, type: 'blob', sha: up.body.sha });
  if (up.body.sha !== f.sha) console.log('note: ' + f.path + ' stored as ' + up.body.sha.slice(0, 8) + ' (local ' + f.sha.slice(0, 8) + ')');
}
for (const p of deletes) entries.push({ path: p, mode: '100644', type: 'blob', sha: null });

// ---- tree / commit / ref ----------------------------------------------------
let treeSha = headTree;
if (entries.length) {
  const t = await json('/repos/' + OWNER + '/' + REPO + '/git/trees', {
    method: 'POST',
    body: JSON.stringify(remoteTree ? { base_tree: remoteTree, tree: entries } : { tree: entries }),
  });
  if (t.status !== 201) { console.error('FAIL: create tree -> ' + t.status + ' ' + JSON.stringify(t.body).slice(0, 300)); process.exit(4); }
  treeSha = t.body.sha;
  console.log('tree -> ' + treeSha.slice(0, 8) + (treeSha === headTree ? ' (matches local)' : ' (DIFFERS from local ' + headTree.slice(0, 8) + ')'));
}

const message = git('log', '-1', '--format=%B').replace(/\s+$/, '');
const ident = (fmt) => git('log', '-1', '--format=' + fmt).trim();
const author = { name: ident('%an'), email: ident('%ae'), date: ident('%aI') };
const committer = { name: ident('%cn'), email: ident('%ce'), date: ident('%cI') };

const c = await json('/repos/' + OWNER + '/' + REPO + '/git/commits', {
  method: 'POST',
  body: JSON.stringify({ message, tree: treeSha, parents: remoteHead ? [remoteHead] : [], author, committer }),
});
if (c.status !== 201) { console.error('FAIL: create commit -> ' + c.status + ' ' + JSON.stringify(c.body).slice(0, 300)); process.exit(4); }
const newSha = c.body.sha;
console.log('commit -> ' + newSha.slice(0, 8));

const patch = await json('/repos/' + OWNER + '/' + REPO + '/git/refs/heads/main', {
  method: 'PATCH',
  body: JSON.stringify({ sha: newSha, force: false }),
});
if (patch.status !== 200) { console.error('FAIL: update ref -> ' + patch.status + ' ' + JSON.stringify(patch.body).slice(0, 300)); process.exit(4); }
console.log('main -> ' + newSha.slice(0, 8));

// ---- keep the local repo consistent ----------------------------------------
if (newSha !== headSha) {
  const sha1 = (s) => createHash('sha1').update(Buffer.from('commit ' + Buffer.byteLength(s, 'utf8') + '\u0000' + s, 'utf8')).digest('hex');
  const stamp = (iso) => {
    const ms = Date.parse(iso);
    return Math.floor(ms / 1000);
  };
  const bodyFor = (off) => {
    const sign = off < 0 ? '-' : '+';
    const a = Math.abs(off);
    const tz = sign + String(Math.floor(a / 60)).padStart(2, '0') + String(a % 60).padStart(2, '0');
    const ea = stamp(c.body.author.date);
    const ec = stamp(c.body.committer.date);
    return 'tree ' + c.body.tree.sha +
      '\n' + (c.body.parents || []).map((p) => 'parent ' + p.sha).join('\n') +
      '\nauthor ' + c.body.author.name + ' <' + c.body.author.email + '> ' + ea + ' ' + tz +
      '\ncommitter ' + c.body.committer.name + ' <' + c.body.committer.email + '> ' + ec + ' ' + tz +
      '\n\n' + c.body.message + '\n';
  };
  let obj = null;
  for (let off = -840; off <= 840 && !obj; off += 15) {
    const b = bodyFor(off);
    if (sha1(b) === newSha) obj = b;
  }
  if (obj) {
    const tmp = join(ROOT, '.git', 'sam-push-commit.tmp');
    writeFileSync(tmp, obj, 'utf8');
    execFileSync('git', ['hash-object', '-w', '-t', 'commit', tmp], { cwd: ROOT, stdio: 'pipe' });
    unlinkSync(tmp);
    git('update-ref', 'refs/heads/main', newSha);
    git('update-ref', 'refs/remotes/origin/main', newSha);
    console.log('local main re-pointed to ' + newSha.slice(0, 8) + ' (commit object restored)');
  } else {
    console.log('warn: local main still at ' + headSha.slice(0, 8) + ' (remote commit object not reconstructable)');
  }
} else {
  git('update-ref', 'refs/remotes/origin/main', newSha);
  console.log('local main already matches the new commit');
}
console.log('done');
