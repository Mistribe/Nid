import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import '@/locales/banks';

// Loaded when opened: most visits import no statement.
const ImportStatementDialog = lazy(() => import('./ImportStatementDialog'));

/** « Importer un relevé » (spec §5.2) on no envelope: banking on is enough, a household may use statement files alone. */
export function ImportStatement() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [landOn, setLandOn] = useState(false);
  const button = useRef<HTMLButtonElement>(null);
  // Once the dialog handed itself back, the focus returns to its button, never <body>.
  useEffect(() => { if (landOn) { button.current?.focus(); setLandOn(false); } }, [landOn]);
  return (
    <>
      <Button ref={button} type="button" variant="outline" onClick={() => setOpen(true)}>{t('banks.import.open')}</Button>
      {open ? (
        <Suspense fallback={null}>
          <ImportStatementDialog onClose={() => setOpen(false)} onDone={(message) => { setOpen(false); toast.success(message); setLandOn(true); }} />
        </Suspense>
      ) : null}
    </>
  );
}
