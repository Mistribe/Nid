import type { QueryClient } from '@tanstack/react-query';
import type { components } from './schema';

export type DocumentList = components['schemas']['DocumentList'];
export type DocumentFacets = components['schemas']['DocumentFacets'];
export type DocumentSummary = components['schemas']['DocumentSummary'];
export type DocumentDetail = components['schemas']['DocumentDetail'];
export type DocumentLink = components['schemas']['DocumentLinkView'];
export type DocumentTargetView = components['schemas']['DocumentTargetView'];
export type DocumentTargetList = components['schemas']['DocumentTargetList'];
export type LinkPreview = components['schemas']['LinkPreview'];
export type ExpectedDocument = components['schemas']['ExpectedDocumentView'];

export const DOCUMENTS = '/api/v1/documents' as const;
export const DOCUMENT = '/api/v1/documents/{id}' as const;
export const DOCUMENT_CONTENT = '/api/v1/documents/{id}/content' as const;
export const DOCUMENT_LINKS = '/api/v1/documents/{id}/links' as const;
export const DOCUMENT_LINK = '/api/v1/documents/{id}/links/{targetKind}/{targetId}' as const;
export const DOCUMENT_LINK_PREVIEW = '/api/v1/documents/{id}/links/preview' as const;
export const DOCUMENT_TARGETS = '/api/v1/documents/targets' as const;
export const ARCHIVE = '/api/v1/documents/{id}/archive' as const;
export const UNARCHIVE = '/api/v1/documents/{id}/unarchive' as const;
export const EXPECTED = '/api/v1/documents/expected' as const;
export const EXPECTED_DISMISSALS = '/api/v1/documents/expected/dismissals' as const;

/** The download is a plain same-origin link: the session cookie goes with it. */
export const contentUrl = (id: string) => DOCUMENT_CONTENT.replace('{id}', encodeURIComponent(id));

/** Every write invalidates the list, every document (a link changes both) and the expected documents (#414). */
export function invalidateDocuments(queryClient: QueryClient): Promise<void> {
  return Promise.all([DOCUMENTS, DOCUMENT, EXPECTED].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] }))).then(() => undefined);
}
