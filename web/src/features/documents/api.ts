import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import type { Api } from '@/api/client';
import { ProblemError, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';

export type DocumentList = components['schemas']['DocumentList'];
export type DocumentSummary = components['schemas']['DocumentSummary'];
export type DocumentDetail = components['schemas']['DocumentDetail'];
export type DocumentLink = components['schemas']['DocumentLinkView'];
export type DocumentTargetView = components['schemas']['DocumentTargetView'];
export type DocumentTargetList = components['schemas']['DocumentTargetList'];
export type LinkPreview = components['schemas']['LinkPreview'];

export const DOCUMENTS = '/api/v1/documents' as const;
export const DOCUMENT = '/api/v1/documents/{id}' as const;
export const DOCUMENT_CONTENT = '/api/v1/documents/{id}/content' as const;
export const DOCUMENT_LINKS = '/api/v1/documents/{id}/links' as const;
export const DOCUMENT_LINK = '/api/v1/documents/{id}/links/{targetKind}/{targetId}' as const;
export const DOCUMENT_LINK_PREVIEW = '/api/v1/documents/{id}/links/preview' as const;
export const DOCUMENT_TARGETS = '/api/v1/documents/targets' as const;
export const ARCHIVE = '/api/v1/documents/{id}/archive' as const;
export const UNARCHIVE = '/api/v1/documents/{id}/unarchive' as const;

export type Scope = 'me' | 'household';

/** The download is a plain same-origin link: the session cookie goes with it. */
export const contentUrl = (id: string) => DOCUMENT_CONTENT.replace('{id}', encodeURIComponent(id));

/** Every write invalidates the list and every document (a link changes both). */
export function invalidateDocuments(queryClient: QueryClient): Promise<void> {
  return Promise.all([DOCUMENTS, DOCUMENT].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}

/** `inbox` lists the unlinked documents whatever the scope; `archived` adds the archived ones. */
export function useDocuments(scope: Scope, archived: boolean, inbox: boolean) {
  const { $api } = useApi();
  return $api.useQuery('get', DOCUMENTS, { params: { query: { scope, archived, inbox } } });
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

/**
 * One file, unlinked unless `fields` name a target (else it lands in « À classer »), as `multipart/form-data`: the name stays out of the URL. The
 * generated type says `file: string` (binary), so the `FormData` is passed as is; the client keeps the CSRF header and the
 * browser writes the boundary. A refusal is thrown as a `ProblemError`.
 */
export async function uploadDocument(
  fetchClient: Api['fetchClient'],
  file: File,
  fields: { title?: string; date?: string; targetKind?: string; targetId?: string; role?: string } = {},
): Promise<components['schemas']['IdResponse']> {
  const body = new FormData();
  body.append('file', file);
  for (const [key, value] of Object.entries(fields)) if (value !== undefined) body.append(key, value);
  const { data, error } = await fetchClient.POST(DOCUMENTS, { body: body as never });
  if (error !== undefined || !data) throw new ProblemError(toProblem(error));
  return data;
}
