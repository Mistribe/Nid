import { Link } from '@tanstack/react-router';
import { Paperclip } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import '@/locales/documents';

/** « Source » next to a value or a flow: the document it came from. The server nulls an id the reader cannot read. */
export function SourceLink({ documentId }: { documentId?: string | null }) {
  const { t } = useTranslation();
  if (!documentId) return null;
  return (
    <Link to="/documents/$id" params={{ id: documentId }} aria-label={t('documents_item.source_label')}
      className="ml-2 inline-flex items-center gap-1 text-sm underline-offset-4 hover:underline">
      <Paperclip className="size-3" aria-hidden />{t('documents_item.source')}
    </Link>
  );
}
