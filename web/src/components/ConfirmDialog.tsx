import { useRef, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertDialog, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Button } from '@/components/ui/button';

/** The alert dialog of `ConfirmAction`, loaded on the first click so Radix's dialog stays out of the initial bundle. */
export default function ConfirmDialog({ label, title, confirmation, pending, returnFocus, onConfirm, onClose }: {
  label: string; title?: string; confirmation: string; pending?: boolean; onConfirm: () => void; onClose: () => void;
  /** Where the focus goes once the dialog closes, for a dialog without a Radix trigger to return to. */
  returnFocus?: RefObject<HTMLElement | null>;
}) {
  const { t } = useTranslation();
  const confirm = useRef<HTMLButtonElement>(null);
  return (
    <AlertDialog open onOpenChange={(open) => { if (!open) onClose(); }}>
      <AlertDialogContent onOpenAutoFocus={(event) => { event.preventDefault(); confirm.current?.focus(); }}
        onCloseAutoFocus={returnFocus ? (event) => { event.preventDefault(); returnFocus.current?.focus(); } : undefined}>
        <AlertDialogHeader>
          <AlertDialogTitle>{title ?? label}</AlertDialogTitle>
          <AlertDialogDescription>{confirmation}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>{t('common.cancel')}</AlertDialogCancel>
          <Button ref={confirm} disabled={pending} onClick={onConfirm}>{t('common.confirm')}</Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
