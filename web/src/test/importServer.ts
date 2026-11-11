import { http, HttpResponse } from 'msw/http';
import { delay } from 'msw/utils/delay';
import type { components } from '@/api/schema';
import { ACTIVE_STATES, effectiveState } from '@/features/import/states';
import '@/locales/import';

type Session = components['schemas']['ImportSessionView'];
type Report = components['schemas']['ImportReport'];
type Issue = components['schemas']['ImportIssue'];
type Person = components['schemas']['PersonDto'];
type Refusal = { status: number; code: string; title: string };

export const SESSION_ID = '0192aaaa-0000-7000-8000-000000000001';

export const householdPersons: Person[] = [
  { id: '01926b9e-0000-7000-8000-00000000000a', kind: 'adult', displayName: 'Alice Dev', email: 'alice@example.test', retirementAge: 64, retirementMonths: 0, signedIn: true },
  { id: '01926b9e-0000-7000-8000-00000000000b', kind: 'adult', displayName: 'Bob Dev', email: 'bob@example.test', retirementAge: 64, retirementMonths: 0, signedIn: true },
  { id: '01926b9e-0000-7000-8000-00000000000c', kind: 'dependent', displayName: 'Léo', email: null, retirementAge: 64, retirementMonths: 0, signedIn: false },
];

export function session(overrides: Partial<Session> = {}): Session {
  return { id: SESSION_ID, state: 'open', createdAt: '2026-09-24T10:00:00+00:00', expiresAt: '2099-01-01T00:00:00+00:00', report: null, ...overrides };
}

export type ReceivedBatch = { section: string; index: number; hash: string; raw: string; count: number; contentType: string | null };

export type FakeImportOptions = {
  /** Sessions the server already holds, newest first. */
  sessions?: Session[];
  /** `POST /imports` answers 409 import.session_active. */
  sessionActive?: boolean;
  /** `POST /imports` answers 409 import.session_active while the server holds an active session, as the server does. */
  oneActive?: boolean;
  /** The first N batch PUTs answer a bare 503. */
  failPuts?: number;
  /** Each PUT waits this long before answering. */
  putDelayMs?: number;
  /** A batch answers 200 `accepted: false` with these issues. */
  refuse?: (section: string, index: number) => Issue[] | null;
  /** A batch answers this Problem. */
  failWith?: (section: string, index: number) => Refusal | null;
  /** What validate answers; by default, every received entity created. */
  validation?: Report;
  /** The first N validates answer 409 import.state. */
  validateStale?: number;
  /** Commit answers 422 import.not_committed and stores these issues in the report. */
  commitFails?: Issue[];
};

/** An in-memory import server with the 1D1 protocol's answers, for component tests. */
export function fakeImportServer(options: FakeImportOptions = {}) {
  const sessions = new Map((options.sessions ?? []).map((s) => [s.id, { ...s }]));
  const received: ReceivedBatch[] = [];
  const started: unknown[] = [];
  const calls: string[] = [];
  const bodies: { validate: unknown[]; commit: unknown[] } = { validate: [], commit: [] };
  let puts = 0;
  let validates = 0;

  const problem = ({ status, code, title }: Refusal) => HttpResponse.json({ status, code, title }, { status });
  const missing = () => problem({ status: 404, code: 'platform.not_found', title: 'Not found.' });

  const handlers = [
    http.get('*/api/v1/core/persons', () => HttpResponse.json(householdPersons)),
    http.get('*/api/v1/platform/imports', () => HttpResponse.json([...sessions.values()])),
    http.post('*/api/v1/platform/imports', async ({ request }) => {
      calls.push('start');
      started.push(await request.json());
      const active = options.oneActive && [...sessions.values()].some((s) => ACTIVE_STATES.has(effectiveState(s)));
      if (options.sessionActive || active) return problem({ status: 409, code: 'import.session_active', title: 'You already have an import in progress.' });
      const created = session();
      sessions.set(created.id, created);
      return HttpResponse.json(created, { status: 201 });
    }),
    http.get('*/api/v1/platform/imports/:id', ({ params }) => {
      const found = sessions.get(String(params.id));
      return found ? HttpResponse.json(found) : missing();
    }),
    http.delete('*/api/v1/platform/imports/:id', ({ params }) => {
      calls.push('cancel');
      const found = sessions.get(String(params.id));
      if (!found) return missing();
      found.state = 'cancelled';
      return HttpResponse.json(found);
    }),
    http.put('*/api/v1/platform/imports/:id/batches/:section/:n', async ({ request, params }) => {
      const section = String(params.section);
      const index = Number(params.n);
      puts += 1;
      calls.push(`put ${section}/${index}`);
      if (options.putDelayMs) await delay(options.putDelayMs);
      if (puts <= (options.failPuts ?? 0)) return new HttpResponse(null, { status: 503 });
      const raw = await request.text();
      const body = JSON.parse(raw) as { hash: string; entities: unknown[] };
      received.push({ section, index, hash: body.hash, raw, count: body.entities.length, contentType: request.headers.get('content-type') });
      const refusal = options.failWith?.(section, index);
      if (refusal) return problem(refusal);
      const issues = options.refuse?.(section, index) ?? [];
      return HttpResponse.json({ section, index, count: body.entities.length, accepted: issues.length === 0, issues });
    }),
    http.post('*/api/v1/platform/imports/:id/validate', async ({ params, request }) => {
      validates += 1;
      calls.push('validate');
      bodies.validate.push(await request.json().catch(() => null));
      const found = sessions.get(String(params.id));
      if (!found) return missing();
      if (validates <= (options.validateStale ?? 0))
        return problem({ status: 409, code: 'import.state', title: 'The import changed while it was being validated; validate again.' });
      const report = options.validation ?? createdOf(received);
      found.state = report.state;
      found.report = report;
      return HttpResponse.json(found);
    }),
    http.post('*/api/v1/platform/imports/:id/commit', async ({ params, request }) => {
      calls.push('commit');
      bodies.commit.push(await request.json().catch(() => null));
      const found = sessions.get(String(params.id));
      if (!found) return missing();
      if (options.commitFails) {
        found.state = 'invalid';
        found.report = { state: 'invalid', sections: found.report?.sections ?? {}, issues: options.commitFails };
        return problem({ status: 422, code: 'import.not_committed', title: 'The import was not written.' });
      }
      found.state = 'committed';
      found.report = { ...(found.report ?? createdOf(received)), state: 'committed' };
      return HttpResponse.json(found);
    }),
  ];

  return { handlers, received, started, calls, sessions, bodies };
}

/** A clean validation of what was received: every entity created. */
function createdOf(received: ReceivedBatch[]): Report {
  const sections: Report['sections'] = {};
  for (const batch of received) {
    const counts = sections[batch.section] ?? { created: 0, unchanged: 0, conflict: 0, replaced: 0 };
    counts.created += batch.count;
    sections[batch.section] = counts;
  }
  return { state: 'validated', sections, issues: [] };
}
