import type { Api } from '@/api/client';
import { ProblemError, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';

type Manifest = components['schemas']['ExportManifestView'];
type Client = Api['fetchClient'];

export type ExportProgress = { received: number; total: number };
export type ExportResult = { kind: 'done'; fileName: string; blob: Blob; count: number } | { kind: 'changed' };

const REVISION_CHANGED = 'export.revision_changed';

const pad = (n: number) => String(n).padStart(2, '0');

/** `nid-YYYY-MM-DD.nid.json`, on the browser's local date. */
export const exportFileName = (today: Date): string =>
  `nid-${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}.nid.json`;

async function readManifest(client: Client, signal: AbortSignal): Promise<Manifest> {
  const { data, error } = await client.GET('/api/v1/platform/export/manifest', { signal });
  if (!data) throw new ProblemError(toProblem(error));
  return data;
}

/**
 * The reader's nid.data file, assembled from 1D2's pages (import-format.md §10). The first page of each section has
 * no cursor, so nothing checks it against the revision: the manifest is read again at the end, and any change means
 * no file. Throws on any other failure, and on abort.
 */
export async function runExport(
  client: Client,
  { signal, onProgress, today = new Date() }: { signal: AbortSignal; onProgress?: (p: ExportProgress) => void; today?: Date },
): Promise<ExportResult> {
  const first = await readManifest(client, signal);
  const announced = first.sections.reduce((sum, section) => sum + section.count, 0);
  let received = 0;
  const report = () => onProgress?.({ received, total: Math.max(announced, received) });
  report();

  const sections: Record<string, unknown[]> = {};
  for (const { name } of first.sections) {
    const entities: unknown[] = [];
    let cursor: string | undefined;
    do {
      const { data, error, response } = await client.GET('/api/v1/platform/export/{section}', {
        params: { path: { section: name }, query: cursor ? { cursor } : {} },
        signal,
      });
      if (!data) {
        const problem = toProblem(error);
        if (response.status === 409 && problem.code === REVISION_CHANGED) return { kind: 'changed' };
        throw new ProblemError(problem);
      }
      entities.push(...data.entities);
      received += data.entities.length;
      report();
      cursor = data.nextCursor ?? undefined;
    } while (cursor);
    sections[name] = entities;
  }

  const last = await readManifest(client, signal);
  if (last.revision !== first.revision) return { kind: 'changed' };

  const envelope = { format: first.format, version: first.version, exported_at: first.exportedAt, ...sections };
  const blob = new Blob([`${JSON.stringify(envelope, null, 2)}\n`], { type: 'application/json' });
  return { kind: 'done', fileName: exportFileName(today), blob, count: received };
}
