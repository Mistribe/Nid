// A scripted OpenAI-compatible engine for the end-to-end stack only (#528): the Compose worker's engine in compose.local.yaml,
// never on a real deployment. One model, one script: without a tool result yet, it asks for list_envelopes; with one, it
// names the first envelope it read and writes a URL, which the « Assistant » page must leave as plain text.
import { createServer } from 'node:http';

const MODEL = 'nid-test-model';

const chunk = (delta, finish = null) => ({ model: MODEL, choices: [{ index: 0, delta, finish_reason: finish }] });

/** The first `displayName` of a tool result, read as JSON: its text escapes accents. */
function firstName(content) {
  let found;
  const walk = (node) => {
    if (found || node === null || typeof node !== 'object') return;
    if (typeof node.displayName === 'string') found = node.displayName;
    else Object.values(node).forEach(walk);
  };
  try {
    walk(JSON.parse(content ?? ''));
  } catch {
    return undefined;
  }
  return found;
}

function turn(messages) {
  const results = messages.filter((m) => m.role === 'tool');
  if (results.length === 0)
    return [chunk({ tool_calls: [{ index: 0, id: 'call_e2e', type: 'function', function: { name: 'list_envelopes', arguments: '{}' } }] }, 'tool_calls')];
  const name = firstName(results.at(-1).content);
  const text = name
    ? `D'après Nid, votre enveloppe « ${name} » est suivie. Détail : https://example.test/ne-pas-suivre`
    : "Je ne trouve aucune enveloppe.";
  return [chunk({ content: text }), chunk({}, 'stop')];
}

createServer((request, response) => {
  if (request.method === 'GET' && request.url === '/v1/models') {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ object: 'list', data: [{ id: MODEL, object: 'model' }] }));
    return;
  }
  if (request.method === 'POST' && request.url === '/v1/chat/completions') {
    let body = '';
    request.on('data', (part) => { body += part; });
    request.on('end', () => {
      const chunks = turn(JSON.parse(body).messages ?? []);
      response.writeHead(200, { 'content-type': 'text/event-stream' });
      for (const c of [...chunks, { model: MODEL, choices: [], usage: { prompt_tokens: 100, completion_tokens: 20 } }])
        response.write(`data: ${JSON.stringify(c)}\n\n`);
      response.end('data: [DONE]\n\n');
    });
    return;
  }
  response.writeHead(404).end();
}).listen(11434);
