import { useNavigate } from '@tanstack/react-router';
import { useEffect } from 'react';
import { useDocument } from '@/features/documents/api';
import DocumentPreviewDialog from '@/features/documents/DocumentPreviewDialog';
import { previewable } from '@/features/documents/preview';

/** The line only knows a receipt's id and title: its media type comes from the document. A mail (not previewable) opens its own page. */
export default function ReceiptPreview({ id, title, onClose }: { id: string; title: string; onClose: () => void }) {
  const doc = useDocument(id);
  const navigate = useNavigate();
  const mediaType = doc.data?.document.mediaType;
  const shown = mediaType !== undefined && previewable(mediaType);
  useEffect(() => { if (mediaType !== undefined && !shown) { onClose(); void navigate({ to: '/documents/$id', params: { id } }); } }, [mediaType, shown, id, navigate, onClose]);
  return shown ? <DocumentPreviewDialog doc={{ id, title, mediaType }} onClose={onClose} /> : null;
}
