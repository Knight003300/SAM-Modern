// fetch-ext.mjs - download the offline build toolchain into tools/ext (one time).
//
// The repo does not vendor these NuGet packages (tools/ext is ~220 MB). Run this
// once after cloning, then "node tools/build.mjs ..." works fully offline.
//
//   node tools/fetch-ext.mjs
//
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const EXT = path.join(ROOT, 'tools', 'ext');
const CACHE = path.join(EXT, '.download');

const PACKAGES = [
  { dir: 'refasm',   id: 'Microsoft.NETFramework.ReferenceAssemblies.net48', version: '1.0.3' },
  { dir: 'roslyn',   id: 'Microsoft.Net.Compilers.Toolset',                  version: '4.8.0' },
  { dir: 'webview2', id: 'Microsoft.Web.WebView2',                           version: '1.0.2903.40' },
];

function present(p) {
  return fs.existsSync(p) && fs.readdirSync(p).length > 0;
}

function unzip(zip, dest) {
  // Windows 10 17063+ ships bsdtar which understands zip archives.
  let res = spawnSync('tar', ['-xf', zip, '-C', dest], { stdio: 'inherit' });
  if (res.status === 0) return true;
  const tmp = zip.replace(/\.nupkg$/, '.zip');
  fs.copyFileSync(zip, tmp);
  res = spawnSync('powershell', [
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
    "Expand-Archive -LiteralPath '" + tmp.replace(/'/g, "''") + "' -DestinationPath '" + dest.replace(/'/g, "''") + "' -Force",
  ], { stdio: 'inherit' });
  fs.rmSync(tmp, { force: true });
  return res.status === 0;
}

async function download(id, version) {
  fs.mkdirSync(CACHE, { recursive: true });
  const file = path.join(CACHE, id + '.' + version + '.nupkg');
  if (fs.existsSync(file) && fs.statSync(file).size > 1024) {
    console.log('cached ' + id + ' ' + version);
    return file;
  }
  const url = 'https://www.nuget.org/api/v2/package/' + encodeURIComponent(id) + '/' + version;
  console.log('download ' + url);
  const res = await fetch(url, { redirect: 'follow' });
  if (!res.ok) throw new Error('HTTP ' + res.status + ' for ' + url);
  const buf = Buffer.from(await res.arrayBuffer());
  fs.writeFileSync(file, buf);
  console.log('  ' + (buf.length / 1048576).toFixed(1) + ' MB');
  return file;
}

let missing = 0;
for (const p of PACKAGES) {
  const dest = path.join(EXT, p.dir);
  if (present(dest)) {
    console.log('ok      tools/ext/' + p.dir + ' (already present)');
    continue;
  }
  missing++;
  fs.mkdirSync(dest, { recursive: true });
  const file = await download(p.id, p.version);
  if (!unzip(file, dest)) throw new Error('failed to extract ' + file);
  console.log('ok      tools/ext/' + p.dir);
}

if (missing === 0) console.log('toolchain complete - run: node tools/build.mjs api && node tools/build.mjs ui');
else console.log('done. run: node tools/build.mjs api && node tools/build.mjs icon && node tools/build.mjs ui');
