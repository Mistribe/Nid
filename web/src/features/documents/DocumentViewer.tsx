import { contentUrl } from './api';
import type { Previewed } from './preview';

/** Issue #413: a PDF in the browser's own viewer, an image as an image, from the inline variant of the download. */
export function DocumentViewer({ doc }: { doc: Previewed }) {
  const src = `${contentUrl(doc.id)}?inline=true`;
  return doc.mediaType === 'application/pdf'
    ? <iframe src={src} title={doc.title} className="h-[75dvh] w-full rounded-md border" />
    : <img src={src} alt={doc.title} className="mx-auto max-h-[75dvh] max-w-full object-contain" />;
}
