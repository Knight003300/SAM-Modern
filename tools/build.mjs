import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// Repo root = parent of tools/, so the script works from any working directory.
const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const EXT = path.join(ROOT, 'tools', 'ext');
const REF = path.join(EXT, 'refasm', 'build', '.NETFramework', 'v4.8');
const CSC = path.join(EXT, 'roslyn', 'tasks', 'net472', 'csc.exe');
const WV2 = path.join(EXT, 'webview2', 'lib', 'net462');
const WV2LOADER = path.join(EXT, 'webview2', 'runtimes', 'win-x86', 'native', 'WebView2Loader.dll');
const SRCROOT = path.join(ROOT, 'vendor', 'samsrc', 'SteamAchievementManager-master');

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (e.name !== 'obj' && e.name !== 'bin') walk(p, out); }
    else if (e.name.endsWith('.cs')) out.push(p);
  }
  return out;
}

const standardRefs = ['mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Data.dll','System.Runtime.Serialization.dll']
  .map(n => path.join(REF, n));

export function build(opts) {
  const { out, target, sources, refs = [], resources = [], libDirs = [], defines = [], unsafeCode = true } = opts;
  fs.mkdirSync(path.dirname(out), { recursive: true });
  const args = ['/nologo', '/target:' + target, '/platform:x86', '/langversion:9.0', '/nostdlib+',
    '/optimize-', '/debug:portable', '/deterministic+', '/nowarn:1591,0618,0169,0414,0649,1701,1702,0436', '/out:' + out];
  if (unsafeCode) args.push('/unsafe+');
  if (defines.length) args.push('/define:' + defines.join(';'));
  if (opts.icon) args.push('/win32icon:' + opts.icon);
  for (const d of libDirs) args.push('/lib:' + d);
  for (const r of refs) args.push('/reference:' + r);
  for (const r of resources) args.push('/resource:' + r);
  args.push(...sources);
  const res = spawnSync(CSC, args, { stdio: 'inherit' });
  if (res.status !== 0) throw new Error('csc failed for ' + out);
  return out;
}

// Sign a freshly built exe with the local self-signed code-signing cert.
// Without this every rebuild produces an unsigned binary and SmartScreen warns again.
function signExe(exe) {
  const script = path.join(ROOT, 'tools', 'sign-one.ps1');
  if (!fs.existsSync(script) || !fs.existsSync(exe)) return;
  const res = spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, exe], { stdio: 'inherit' });
  if (res.status !== 0) console.log('warning: signing failed for ' + exe);
}

function copyWv2(dir) {
  for (const n of ['Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll']) {
    const from = n === 'WebView2Loader.dll' ? WV2LOADER : path.join(WV2, n);
    fs.copyFileSync(from, path.join(dir, n));
  }
}

const wv2Refs = [path.join(WV2, 'Microsoft.Web.WebView2.Core.dll'), path.join(WV2, 'Microsoft.Web.WebView2.WinForms.dll')];
const what = process.argv[2];

if (what === 'api') {
  const out = path.join(ROOT, 'build', 'SAM.API.dll');
  build({ out, target: 'library', sources: walk(path.join(SRCROOT, 'SAM.API')), refs: standardRefs });
  console.log('built ' + out);
} else if (what === 'probe') {
  const out = path.join(ROOT, 'tools', '_scratch', 'build-test', 'Wv2Probe.exe');
  build({ out, target: 'exe', sources: [path.join(ROOT, 'tools', '_scratch', 'build-test', 'Wv2Probe.cs')], refs: [...standardRefs, ...wv2Refs] });
  copyWv2(path.dirname(out));
  console.log('built ' + out);
} else if (what === 'icon') {
  const out = path.join(ROOT, 'tools', 'icongen.exe');
  build({
    out,
    target: 'exe',
    sources: [path.join(ROOT, 'tools', 'icongen', 'IconGen.cs')],
    refs: [path.join(REF, 'mscorlib.dll'), path.join(REF, 'System.dll'), path.join(REF, 'System.Drawing.dll')],
    unsafeCode: false,
  });
  console.log('built ' + out);
} else if (what === 'ui') {
  const apiDll = path.join(ROOT, 'build', 'SAM.API.dll');
  const out = path.join(ROOT, 'build', 'SAM.Modern.exe');
  const sources = walk(path.join(ROOT, 'src')).filter(p => !p.endsWith('Nav.cs'));
  const icon = path.join(ROOT, 'build', 'sam.ico');
  build({
    out,
    target: 'exe',
    sources,
    refs: [...standardRefs, ...wv2Refs, apiDll],
    resources: [],
    icon: fs.existsSync(icon) ? icon : undefined,
  });
  copyWv2(path.dirname(out));
  fs.mkdirSync(path.join(path.dirname(out), 'ui'), { recursive: true });
  for (const n of fs.readdirSync(path.join(ROOT, 'ui'))) {
    fs.copyFileSync(path.join(ROOT, 'ui', n), path.join(path.dirname(out), 'ui', n));
  }
  if (fs.existsSync(icon)) fs.copyFileSync(icon, path.join(path.dirname(out), 'sam.ico'));
  console.log('built ' + out);
  signExe(out);
} else {
  console.log('usage: node tools/build.mjs <api|probe|icon|ui>');
}