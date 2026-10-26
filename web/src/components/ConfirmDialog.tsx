import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertDialog, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog';
import { Button } from '@/components/ui/button';

/** The alert dialog of `ConfirmAction`, loaded on the first click so Radix's dialog stays out of the initial bundle. */
export default function ConfirmDialog({ label, title, confirmation, pending, onConfirm, onClose }: {
  label: string; title?: string; confirmation: string; pending?: boolean; onConfirm: () => void; onClose: () => void;
}) {
  const { t } = useTranslation();
  const confirm = useRef<HTMLButtonElement>(null);
  return (
    <AlertDialog open onOpenChange={(open) => { if (!open) onClose(); }}>
      <AlertDialogContent onOpenAutoFocus={(event) => { event.preventDefault(); confirm.current?.focus(); }}>
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
