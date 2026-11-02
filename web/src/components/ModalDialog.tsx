import { flushSync } from 'react-dom';
import { useId, useImperativeHandle, useRef, useState, type ReactNode, type RefObject } from 'react';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';

/**
 * A modal dialog (shadcn `Dialog`) open while mounted, never taller than the screen: it scrolls, unless its content (a form
 * with `min-h-0`) keeps its own parts in view. Escape and the close button call `onClose`. The page owns the open
 * state, the focus and the announcement of a success, so a dialog closed by Escape announces nothing. `dialogRef` lets a
 * dialog close itself before handing back (« Nouveau fournisseur »): the page then owns the focus (the close is
 * committed at once, so the focus the page moves is no longer trapped, and Radix does not return it).
 */
export function ModalDialog({ title, onClose, dialogRef, children }: {
  title: string;
  onClose: () => void;
  dialogRef?: RefObject<{ close: () => void } | null>;
  children: (titleId: string) => ReactNode;
}) {
  const titleId = useId();
  const [open, setOpen] = useState(true);
  const handedBack = useRef(false);

  useImperativeHandle(dialogRef, () => ({ close: () => { handedBack.current = true; flushSync(() => setOpen(false)); } }), []);

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next) onClose(); }}>
      <DialogContent showCloseButton={false} aria-labelledby={titleId} aria-describedby={undefined} className="flex max-h-[calc(100dvh-2rem)] flex-col overflow-y-auto"
        onCloseAutoFocus={(event) => { if (handedBack.current) event.preventDefault(); }}>
        <DialogTitle id={titleId}>{title}</DialogTitle>
        {children(titleId)}
      </DialogContent>
    </Dialog>
  );
}
