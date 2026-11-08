import { Download } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ModalDialog } from '@/components/ModalDialog';
import { Button } from '@/components/ui/button';
import { contentUrl } from '@/api/documents';
import { DocumentViewer } from './DocumentViewer';
import type { Previewed } from './preview';
import '@/locales/documents';

/**
 * Issue #413: « Voir » from a menu. The download link and the close button come before the viewer, so the keyboard reaches
 * them without going through the PDF.
 */
export default function DocumentPreviewDialog({ doc, onClose }: { doc: Previewed; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <ModalDialog title={doc.title} onClose={onClose} className="sm:max-w-5xl">
      {() => (
        <>
          <div className="flex flex-wrap justify-end gap-2">
            <Button asChild variant="outline"><a href={contentUrl(doc.id)} download><Download aria-hidden="true" />{t('documents_page.download')}</a></Button>
            <Button type="button" variant="outline" onClick={onClose}>{t('common.close')}</Button>
          </div>
          <DocumentViewer doc={doc} />
        </>
      )}
    </ModalDialog>
  );
}
