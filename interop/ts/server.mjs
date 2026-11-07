// The fixture server on the TypeScript SDK, from its published README API.
// usage: server.mjs stdio | http PORT | sse PORT
//   http: Streamable HTTP with sessions; sse: the deprecated 2024-11-05 HTTP+SSE transport.
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { SSEServerTransport } from '@modelcontextprotocol/sdk/server/sse.js';
import { z } from 'zod';

const PNG_1X1 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==';

function build() {
  const server = new McpServer({ name: 'interop-ts-server', version: '1.0.0' });
  server.registerTool('echo', { description: 'Echoes the message.', inputSchema: { message: z.string() } },
    async ({ message }) => ({ content: [{ type: 'text', text: message }] }));
  server.registerTool('image', { description: 'A one-pixel PNG.', inputSchema: {} },
    async () => ({ content: [{ type: 'image', data: PNG_1X1, mimeType: 'image/png' }] }));
  server.registerTool('slow', { description: 'Reports progress.', inputSchema: { steps: z.number() } }, async ({ steps }, extra) => {
    const token = extra._meta?.progressToken;
    for (let i = 1; i <= steps; i++) {
      if (token !== undefined) await extra.sendNotification({ method: 'notifications/progress', params: { progressToken: token, progress: i, total: steps } });
      await new Promise((r) => setTimeout(r, 50));
    }
    return { content: [{ type: 'text', text: 'done' }] };
  });
  server.registerTool('wait', { description: 'Waits until cancelled.', inputSchema: {} }, async (_a, extra) => {
    await new Promise((resolve) => { extra.signal.addEventListener('abort', resolve); setTimeout(resolve, 15000); });
    return { content: [{ type: 'text', text: extra.signal.aborted ? 'cancelled' : 'timed out' }] };
  });
  server.registerTool('confirm', { description: 'Asks the user to confirm.', inputSchema: {} }, async () => {
    const r = await server.server.elicitInput({ message: 'Proceed?', requestedSchema: { type: 'object', properties: { confirm: { type: 'boolean' } }, required: ['confirm'] } });
    return { content: [{ type: 'text', text: r.action === 'accept' && r.content?.confirm ? 'confirmed' : 'declined' }] };
  });
  server.registerResource('hello', 'mem://hello', { description: 'A greeting.', mimeType: 'text/plain' },
    async (uri) => ({ contents: [{ uri: uri.href, text: 'hello from interop' }] }));
  server.registerPrompt('greet', { description: 'Greets someone.', argsSchema: { name: z.string() } },
    ({ name }) => ({ messages: [{ role: 'user', content: { type: 'text', text: `Say hello to ${name}.` } }] }));
  return server;
}

const [mode, portArg] = process.argv.slice(2);
if (mode === 'stdio') {
  await build().connect(new StdioServerTransport());
} else {
  const transports = {};
  const srv = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    try {
      if (mode === 'sse') {
        if (req.method === 'GET' && url.pathname === '/sse') {
          const t = new SSEServerTransport('/messages', res);
          transports[t.sessionId] = t;
          res.on('close', () => delete transports[t.sessionId]);
          await build().connect(t);
        } else if (req.method === 'POST' && url.pathname === '/messages') {
          const t = transports[url.searchParams.get('sessionId')];
          if (!t) { res.writeHead(400).end('no such session'); return; }
          await t.handlePostMessage(req, res);
        } else res.writeHead(404).end();
        return;
      }
      if (url.pathname !== '/mcp') { res.writeHead(404).end(); return; }
      let body;
      if (req.method === 'POST') { const chunks = []; for await (const c of req) chunks.push(c); body = JSON.parse(Buffer.concat(chunks).toString('utf8')); }
      const sid = req.headers['mcp-session-id'];
      let t = sid ? transports[sid] : undefined;
      if (!t) {
        if (req.method !== 'POST') { res.writeHead(400).end('no session'); return; }
        t = new StreamableHTTPServerTransport({ sessionIdGenerator: () => randomUUID(), onsessioninitialized: (id) => { transports[id] = t; } });
        t.onclose = () => { if (t.sessionId) delete transports[t.sessionId]; };
        await build().connect(t);
      }
      await t.handleRequest(req, res, body);
    } catch (e) { if (!res.headersSent) res.writeHead(500).end(String(e)); }
  });
  srv.listen(Number(portArg ?? 0), '127.0.0.1', () => console.log('listening ' + srv.address().port));
}
