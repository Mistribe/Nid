// A scripted OpenAI-compatible engine for the end-to-end stack only (#528): the Compose worker's engine in compose.local.yaml,
// never on a real deployment. Two models, two scripts. nid-test-model chats: without a tool result yet, it asks for
// list_envelopes; with one, it names the first envelope it read and writes a URL, which the « Assistant » page must leave as
// plain text. nid-test-decide (#450) answers decide's one-token calls: the letter of the first sub-category whose own name shares
// a word with the pattern, else « none of these », with log-probabilities. Right often enough on the evaluation set (27 shown,
// 1 wrong) for a calibration to offer it, so a journey sees its guesses.
import { createServer } from 'node:http';

const MODEL = 'nid-test-model';
const DECIDE = 'nid-test-decide';

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

/** The words of a text, five letters or more (« PRET » would put a friend's refund on the home loan), upper case and without accents. */
const words = (text) => new Set(text.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toUpperCase().split(/[^A-Z]+/).filter((w) => w.length >= 5));

/** decide: `X: Parent › Name` lines, then the text in its block; the pattern's words pick a sub-category by its own name (a top
 * category's, « Santé », is too broad to be right often enough). */
function decide(messages) {
  const user = messages.find((m) => m.role === 'user')?.content ?? '';
  const options = [...user.matchAll(/^([A-Za-z]): (.+)$/gm)].map((m) => ({ id: m[1], name: m[2] }));
  const pattern = words(/^Motif : (.*)$/m.exec(user)?.[1] ?? '');
  const none = options.at(-1);
  const chosen = options.find((o) => o !== none && o.name.includes(' › ') && [...words(o.name.split(' › ').at(-1))].some((w) => pattern.has(w))) ?? none;
  const top = [{ token: chosen.id, logprob: -0.05 }, ...(chosen === none ? [] : [{ token: none.id, logprob: -3.2 }]), { token: 'Le', logprob: -4.5 }];
  return {
    object: 'chat.completion', model: DECIDE,
    choices: [{ index: 0, message: { role: 'assistant', content: chosen.id }, logprobs: { content: [{ token: chosen.id, logprob: -0.05, top_logprobs: top }] }, finish_reason: 'length' }],
    usage: { prompt_tokens: 200, completion_tokens: 1 },
  };
}

createServer((request, response) => {
  if (request.method === 'GET' && request.url === '/v1/models') {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ object: 'list', data: [{ id: MODEL, object: 'model' }, { id: DECIDE, object: 'model' }] }));
    return;
  }
  if (request.method === 'POST' && request.url === '/v1/chat/completions') {
    let body = '';
    request.on('data', (part) => { body += part; });
    request.on('end', () => {
      const parsed = JSON.parse(body);
      if (parsed.model === DECIDE) {
        response.writeHead(200, { 'content-type': 'application/json' });
        response.end(JSON.stringify(decide(parsed.messages ?? [])));
        return;
      }
      const chunks = turn(parsed.messages ?? []);
      response.writeHead(200, { 'content-type': 'text/event-stream' });
      for (const c of [...chunks, { model: MODEL, choices: [], usage: { prompt_tokens: 100, completion_tokens: 20 } }])
        response.write(`data: ${JSON.stringify(c)}\n\n`);
      response.end('data: [DONE]\n\n');
    });
    return;
  }
  response.writeHead(404).end();
}).listen(11434);
