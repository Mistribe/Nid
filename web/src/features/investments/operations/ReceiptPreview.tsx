import { useNavigate } from '@tanstack/react-router';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useDocument } from '@/features/documents/api';
import DocumentPreviewDialog from '@/features/documents/DocumentPreviewDialog';
import { previewable } from '@/features/documents/preview';
import '@/locales/banks';

/** The line only knows a receipt's id and title: its media type comes from the document. A mail (not previewable) opens its own page; a gone one closes with a toast. */
export default function ReceiptPreview({ id, title, onClose }: { id: string; title: string; onClose: () => void }) {
  const { t } = useTranslation();
  const doc = useDocument(id);
  const navigate = useNavigate();
  // Unlinked or out of the reader's sight since the list was read: the paperclip must not go dead.
  useEffect(() => { if (doc.isError) { toast.error(t('banks.operations.receipt_gone')); onClose(); } }, [doc.isError, onClose, t]);
  const mediaType = doc.data?.document.mediaType;
  const shown = mediaType !== undefined && previewable(mediaType);
  useEffect(() => { if (mediaType !== undefined && !shown) { onClose(); void navigate({ to: '/documents/$id', params: { id } }); } }, [mediaType, shown, id, navigate, onClose]);
  return shown ? <DocumentPreviewDialog doc={{ id, title, mediaType }} onClose={onClose} /> : null;
}
