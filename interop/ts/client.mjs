// The probe client built on the TypeScript SDK, from its published README API.
// usage: client.mjs (--stdio "CMD ARGS" | --url URL [--sse]) [--profile fixture|generic]
// One JSON line per check on stdout: {"check":..., "ok":..., "detail":...}.
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';
import { ElicitRequestSchema } from '@modelcontextprotocol/sdk/types.js';

const argv = process.argv.slice(2);
const opt = (n) => { const i = argv.indexOf(n); return i < 0 ? undefined : argv[i + 1]; };
const emit = (check, ok, detail = '') => console.log(JSON.stringify({ check, ok, detail: String(detail).slice(0, 300) }));
const step = async (check, fn) => { try { emit(check, true, await fn()); } catch (e) { emit(check, false, e?.message ?? e); } };
const text = (r) => (r.content ?? []).filter((c) => c.type === 'text').map((c) => c.text).join('|');

let transport;
if (opt('--stdio')) {
  const [command, ...args] = opt('--stdio').split(' ');
  transport = new StdioClientTransport({ command, args, stderr: 'ignore' });
} else {
  const url = new URL(opt('--url'));
  transport = argv.includes('--sse') ? new SSEClientTransport(url) : new StreamableHTTPClientTransport(url);
}

const client = new Client({ name: 'interop-ts', version: '1.0.0' }, { capabilities: { elicitation: {} } });
client.setRequestHandler(ElicitRequestSchema, async () => ({ action: 'accept', content: { confirm: true } }));
const deadline = setTimeout(() => { emit('deadline', false, 'run exceeded 90 s'); process.exit(3); }, 90000);

try {
  await client.connect(transport);
} catch (e) {
  emit('connect', false, e?.message ?? e);
  process.exit(1);
}
const info = client.getServerVersion();
emit('connect', true, `server ${info?.name} ${info?.version}; protocol ${transport.protocolVersion ?? 'n/a'}`);

if (opt('--profile') === 'generic') {
  await step('tools/list', async () => (await client.listTools()).tools.map((t) => t.name).join(','));
  await step('resources/list', async () => (await client.listResources()).resources.length + ' resources');
  await step('prompts/list', async () => (await client.listPrompts()).prompts.length + ' prompts');
} else {
  await step('tools/list', async () => { const t = (await client.listTools()).tools.map((x) => x.name); if (!t.includes('echo')) throw new Error('no echo: ' + t); return t.join(','); });
  await step('tools/call text', async () => { const r = await client.callTool({ name: 'echo', arguments: { message: 'hi' } }); if (text(r) !== 'hi') throw new Error(JSON.stringify(r)); return text(r); });
  await step('tools/call image', async () => { const r = await client.callTool({ name: 'image', arguments: {} }); const c = r.content.find((x) => x.type === 'image'); if (!c || c.mimeType !== 'image/png' || !c.data) throw new Error(JSON.stringify(r).slice(0, 200)); return c.mimeType + ' ' + c.data.length + ' b64'; });
  await step('resources/list', async () => (await client.listResources()).resources.map((r) => r.uri).join(','));
  await step('resources/read', async () => { const r = await client.readResource({ uri: 'mem://hello' }); return r.contents[0].text; });
  await step('prompts/list', async () => (await client.listPrompts()).prompts.map((p) => p.name).join(','));
  await step('prompts/get', async () => { const r = await client.getPrompt({ name: 'greet', arguments: { name: 'Ann' } }); return r.messages[0].content.text; });
  await step('error: unknown tool', async () => {
    try { const r = await client.callTool({ name: 'nope', arguments: {} }); if (r.isError) return 'isError result'; throw new Error('no error: ' + JSON.stringify(r).slice(0, 150)); }
    catch (e) { if (e?.code !== undefined) return 'JSON-RPC error ' + e.code; throw e; }
  });
  await step('progress', async () => {
    let seen = 0;
    const r = await client.callTool({ name: 'slow', arguments: { steps: 3 } }, undefined, { onprogress: () => { seen++; }, timeout: 20000 });
    if (seen < 1) throw new Error('no progress notifications; result ' + text(r));
    return seen + ' notifications';
  });
  await step('cancellation', async () => {
    const ac = new AbortController();
    const p = client.callTool({ name: 'wait', arguments: {} }, undefined, { signal: ac.signal, timeout: 20000 });
    setTimeout(() => ac.abort(), 300);
    try { await p; throw new Error('call completed'); } catch (e) { if (!/abort|cancel/i.test(String(e?.message ?? e)) && e?.name !== 'AbortError') throw e; }
    const r = await client.callTool({ name: 'echo', arguments: { message: 'after' } });
    return 'aborted; next call ' + text(r);
  });
  await step('elicitation', async () => { const r = await client.callTool({ name: 'confirm', arguments: {} }); if (text(r) !== 'confirmed') throw new Error(JSON.stringify(r).slice(0, 200)); return text(r); });
}
clearTimeout(deadline);
await client.close().catch(() => {});
process.exit(0);
