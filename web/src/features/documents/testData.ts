import type { DocumentDetail, DocumentLink, DocumentList, DocumentSummary, DocumentTargetList, DocumentTargetView } from './api';

export const DOC = '0192f0aa-0000-7000-8000-00000000d001';
export const ITEM = '0192f0aa-0000-7000-8000-00000000e001';

export const link = (over: Partial<DocumentLink> = {}): DocumentLink => ({
  targetKind: 'core.item', targetId: ITEM, anchorItemId: ITEM, role: 'lease', label: 'Appartement Lyon', canWrite: true, ...over,
});

export const summary = (over: Partial<DocumentSummary> = {}): DocumentSummary => ({
  id: DOC, title: 'Bail 2024', mediaType: 'application/pdf', sizeBytes: 250_880, documentDate: '2024-03-01',
  uploadedAt: '2026-10-20T09:00:00Z', uploadedBy: ALICE, archived: false, canEdit: true, links: [link()], ...over,
});

export const list = (items: DocumentSummary[]): DocumentList => ({ items });

export const ALICE = '0192f0aa-0000-7000-8000-00000000a001';
export const BOB_ITEM = '0192f0aa-0000-7000-8000-00000000e009';

export const detail = (over: Partial<DocumentSummary> = {}, more: Partial<Omit<DocumentDetail, 'document'>> = {}): DocumentDetail => ({
  document: summary(over), originalFilename: 'bail.pdf', note: 'Signé chez le notaire', uploadedBy: ALICE, ...more,
});

export const target = (over: Partial<DocumentTargetView> = {}): DocumentTargetView => ({
  kind: 'core.item', id: ITEM, label: 'Appartement Lyon', module: null, itemKind: null, canWrite: true, ...over,
});

/** Alice (the uploader), the linked flat, a shared item Alice only reads, a writable one, and the household. */
export const targets = (): DocumentTargetList => ({ items: [
  target(),
  target({ id: BOB_ITEM, label: 'Voiture de Bob', canWrite: false }),
  target({ id: '0192f0aa-0000-7000-8000-00000000e002', label: 'Box internet' }),
  target({ kind: 'core.person', id: ALICE, label: 'Alice' }),
  target({ kind: 'core.household', id: '0192f0aa-0000-7000-8000-0000000000f0', label: 'Foyer' }),
] });
