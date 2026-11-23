import type { Api } from '@/api/client';
import { ProblemError, toProblem } from '@/api/errors';

/** A page of Nid the answer names, built by Nid from the ids its tools returned (decision P): never a URL the model wrote. */
export type AssistantLink = { id: string; module: string; name: string };

export type AssistantEvent =
  | { type: 'text'; text: string }
  | { type: 'tool'; tool: string }
  | { type: 'held_back'; count: number; destination: string; placement: string }
  | { type: 'done'; provider: string; placement: string; model: string; reason: string; links: AssistantLink[] }
  | { type: 'error'; code: string };

export type AssistantMessage = { role: 'user' | 'assistant'; content: string };

/**
 * One turn of `POST /api/v1/ai/assistant/turn` (#501): its server-sent events, in order. A refused request (a field, the
 * session) rejects with the server's problem, as a query would.
 */
export async function* assistantTurn(api: Api, question: string, history: AssistantMessage[], signal?: AbortSignal): AsyncGenerator<AssistantEvent> {
  const { response, error } = await api.fetchClient.POST('/api/v1/ai/assistant/turn', { body: { question, history }, parseAs: 'stream', signal });
  if (error !== undefined || !response.ok || !response.body) throw new ProblemError(error !== undefined ? toProblem(error) : { status: response.status });
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += value;
    let end: number;
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, end);
      buffer = buffer.slice(end + 2);
      const event = parseBlock(block);
      if (event) yield event;
    }
  }
}

/** One `event:` / `data:` block; anything else (a comment, an unknown event) is skipped. */
export function parseBlock(block: string): AssistantEvent | null {
  let type = '';
  let data = '';
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) type = line.slice(6).trim();
    else if (line.startsWith('data:')) data += line.slice(5).trimStart();
  }
  if (!['text', 'tool', 'held_back', 'done', 'error'].includes(type) || !data) return null;
  return { type, ...JSON.parse(data) } as AssistantEvent;
}
