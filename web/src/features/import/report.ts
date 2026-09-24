import type { components } from '@/api/schema';

type Session = components['schemas']['ImportSessionView'];

/** The final report as a file (web design §6.2, step 7): the session and its report, as the server gave them. */
export function reportFile(session: Session): { name: string; content: string } {
  const content = JSON.stringify({ id: session.id, state: session.state, createdAt: session.createdAt, report: session.report }, null, 2);
  return { name: `nid-import-${session.id}.json`, content: `${content}\n` };
}

export function downloadReport(session: Session): void {
  const { name, content } = reportFile(session);
  const url = URL.createObjectURL(new Blob([content], { type: 'application/json' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  // Deferred: some browsers start the download after `click()` returns, and a revoked address downloads nothing.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
