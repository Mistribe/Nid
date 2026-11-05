import { lazy, Suspense, useState } from 'react';

// The viewer is its own chunk, loaded on the first « Voir » (the initial bundle stays under budget).
const DocumentPreviewDialog = lazy(() => import('./DocumentPreviewDialog'));

export type Previewed = { id: string; title: string; mediaType: string };

/** What the browser shows by itself; the server serves these inline, a mail stays a download. */
export const previewable = (mediaType: string) => ['application/pdf', 'image/png', 'image/jpeg'].includes(mediaType);

/** Issue #413: « Voir » opens the document in a dialog; `dialog` is rendered by the caller. */
export function usePreview() {
  const [doc, setDoc] = useState<Previewed | null>(null);
  const dialog = doc ? <Suspense fallback={null}><DocumentPreviewDialog doc={doc} onClose={() => setDoc(null)} /></Suspense> : null;
  return { show: setDoc, dialog };
}
