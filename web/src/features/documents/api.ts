import { keepPreviousData, useQueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import type { Api } from '@/api/client';
import { ProblemError, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { ARCHIVE, DOCUMENT, DOCUMENT_LINK, DOCUMENT_LINK_PREVIEW, DOCUMENT_LINKS, DOCUMENT_TARGETS, DOCUMENTS, EXPECTED, EXPECTED_DISMISSALS, invalidateDocuments, UNARCHIVE, type LinkPreview } from '@/api/documents';

export type Scope = 'me' | 'household';

/** The documents page's own narrowing (issue #420), applied by the server; `page` from 1, absent for every document (a picker). */
export type DocumentQuery = { role?: string; year?: number; linked?: string; q?: string; page?: number };

/** The server's page size: the documents list shows this many at a time. */
export const PAGE_SIZE = 50;

/**
 * `inbox` lists the unlinked documents whatever the scope; `archived` adds the archived ones. The previous page stays shown
 * while the next one loads, so a filter or a page change does not blank the table.
 */
export function useDocuments(scope: Scope, archived: boolean, inbox: boolean, narrow: DocumentQuery = {}) {
  const { $api } = useApi();
  return $api.useQuery('get', DOCUMENTS, { params: { query: { scope, archived, inbox, ...narrow } } }, { placeholderData: keepPreviousData });
}

/** The documents linked to the item or to anything anchored on it (a phase, a one-off). */
export function useAnchoredDocuments(itemId: string) {
  const { $api } = useApi();
  return $api.useQuery('get', DOCUMENTS, { params: { query: { anchor: itemId } } });
}

export function useDocument(id: string) {
  const { $api } = useApi();
  return $api.useQuery('get', DOCUMENT, { params: { path: { id } } });
}

export function useDocumentTargets() {
  const { $api } = useApi();
  return $api.useQuery('get', DOCUMENT_TARGETS);
}

/** Issue #414: the documents still missing on the item, or on every item the actor writes and their tax notice; only for a writer. */
export function useExpectedDocuments(itemId?: string, enabled = true) {
  const { $api } = useApi();
  return $api.useQuery('get', EXPECTED, { params: { query: { itemId } } }, { enabled });
}

/** « Pas disponible »: that document is no longer asked for. */
export function useDismissExpected() {
  const { $api } = useApi();
  return $api.useMutation('post', EXPECTED_DISMISSALS, useWrite());
}

function useWrite() {
  const queryClient = useQueryClient();
  return { onSuccess: () => invalidateDocuments(queryClient) };
}

export function useArchiveDocument() {
  const { $api } = useApi();
  return $api.useMutation('post', ARCHIVE, useWrite());
}

export function useUnarchiveDocument() {
  const { $api } = useApi();
  return $api.useMutation('post', UNARCHIVE, useWrite());
}

export function useEditDocument() {
  const { $api } = useApi();
  return $api.useMutation('put', DOCUMENT, useWrite());
}

export function useLinkDocument() {
  const { $api } = useApi();
  return $api.useMutation('post', DOCUMENT_LINKS, useWrite());
}

export function useSetLinkRole() {
  const { $api } = useApi();
  return $api.useMutation('put', DOCUMENT_LINK, useWrite());
}

export function useUnlinkDocument() {
  const { $api } = useApi();
  return $api.useMutation('delete', DOCUMENT_LINK, useWrite());
}

/** The adults a new link would let read the document; nothing is written. A refusal is thrown as a `ProblemError`. */
export async function previewLink(fetchClient: Api['fetchClient'], id: string, targetKind: string, targetId: string): Promise<LinkPreview> {
  const { data, error } = await fetchClient.GET(DOCUMENT_LINK_PREVIEW, { params: { path: { id }, query: { targetKind, targetId } } });
  if (error !== undefined || !data) throw new ProblemError(toProblem(error));
  return data;
}

/** One link, written outside a mutation hook so that several can run at once; the caller invalidates. A refusal is thrown as a `ProblemError`. */
export async function linkDocument(fetchClient: Api['fetchClient'], id: string, targetKind: string, targetId: string, role: string): Promise<void> {
  const { error } = await fetchClient.POST(DOCUMENT_LINKS, { params: { path: { id } }, body: { targetKind, targetId, role } });
  if (error !== undefined) throw new ProblemError(toProblem(error));
}

/**
 * One file, unlinked unless `fields` name a target (else it lands in « À classer »), as `multipart/form-data`: the name stays out of the URL. The
 * generated type says `file: string` (binary), so the `FormData` is passed as is; the client keeps the CSRF header and the
 * browser writes the boundary. A refusal is thrown as a `ProblemError`.
 */
export async function uploadDocument(
  fetchClient: Api['fetchClient'],
  file: File,
  fields: { title?: string; date?: string; targetKind?: string; targetId?: string; role?: string; warnDuplicate?: 'true' } = {},
): Promise<components['schemas']['IdResponse']> {
  const body = new FormData();
  body.append('file', file);
  for (const [key, value] of Object.entries(fields)) if (value !== undefined) body.append(key, value);
  const { data, error } = await fetchClient.POST(DOCUMENTS, { body: body as never });
  if (error !== undefined || !data) throw new ProblemError(toProblem(error));
  return data;
}
