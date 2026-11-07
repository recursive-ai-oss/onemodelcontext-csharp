// Turns the per-case JSON-line files a run wrote into results.md and results.json.
// usage: node report.mjs OUT_DIR
import fs from 'node:fs';
import path from 'node:path';

const out = process.argv[2];
const dir = path.join(out, 'cases');
const meta = fs.existsSync(path.join(out, 'meta.json')) ? JSON.parse(fs.readFileSync(path.join(out, 'meta.json'), 'utf8')) : {};
const cases = [];
for (const f of fs.readdirSync(dir).filter((x) => x.endsWith('.jsonl')).sort()) {
  const id = f.slice(0, -6);
  const [client, server, transport, era] = id.split('__');
  const checks = fs.readFileSync(path.join(dir, f), 'utf8').split('\n').filter((l) => l.startsWith('{')).flatMap((l) => { try { return [JSON.parse(l)]; } catch { return []; } });
  const exitFile = path.join(dir, id + '.exit');
  const exit = fs.existsSync(exitFile) ? Number(fs.readFileSync(exitFile, 'utf8').trim()) : null;
  const connect = checks.find((c) => c.check === 'connect');
  cases.push({ id, client, server, transport, era, exit, connect: connect ? { ok: connect.ok, detail: connect.detail } : null, checks });
}
const passed = (c) => c.checks.filter((x) => x.ok).length;
const summary = { cases: cases.length, fullyPassing: cases.filter((c) => c.checks.length > 0 && passed(c) === c.checks.length).length, meta };
fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify({ summary, cases }, null, 2));

const lines = [];
lines.push('# Interop results', '');
for (const [k, v] of Object.entries(meta)) lines.push(`- ${k}: ${v}`);
lines.push('', '| client | server | transport | era | checks | negotiated |', '|---|---|---|---|---|---|');
for (const c of cases) {
  const n = c.checks.length;
  const mark = n === 0 ? 'no output' : `${passed(c)}/${n}`;
  lines.push(`| ${c.client} | ${c.server} | ${c.transport} | ${c.era} | ${mark} | ${(c.connect?.detail ?? '').replace(/\|/g, '/').slice(0, 90)} |`);
}
const failing = cases.flatMap((c) => c.checks.filter((x) => !x.ok).map((x) => ({ c, x })));
if (failing.length) {
  lines.push('', '## Failures', '');
  for (const { c, x } of failing) lines.push(`- \`${c.id}\` · ${x.check}: ${x.detail.replace(/\n/g, ' ').slice(0, 260)}`);
}
for (const c of cases.filter((c) => c.checks.length === 0)) lines.push(`- \`${c.id}\`: no checks recorded (exit ${c.exit})`);
fs.writeFileSync(path.join(out, 'results.md'), lines.join('\n') + '\n');
console.log(lines.slice(0, 3).join('\n'));
console.log(`${summary.fullyPassing}/${summary.cases} cases fully passing; ${failing.length} failing checks; see ${path.join(out, 'results.md')}`);
