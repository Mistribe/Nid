import { useRef, type ReactNode } from 'react';
import { ModalDialog } from '@/components/ModalDialog';

/**
 * A form in a modal dialog whose page owns the focus and the announcement. `done(message)` hands the dialog back before the
 * page takes over (so Radix does not return the focus to the trigger); `done()` is « Annuler »: the dialog just closes.
 */
export function FormDialog({ title, onClose, onDone, children }: {
  title: string; onClose: () => void; onDone: (message: string) => void;
  children: (props: { titleId: string; done: (message?: string) => void }) => ReactNode;
}) {
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={title} onClose={onClose} dialogRef={dialog}>
      {(titleId) => children({ titleId, done: (message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); } })}
    </ModalDialog>
  );
}
