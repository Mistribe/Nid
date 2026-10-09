import { useEffect, useId, useRef, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';

/**
 * A native modal dialog in a portal (2E1 spec §4): opened on mount, closed on unmount, Escape calls `onClose`. The page
 * owns the open state, the focus and the announcement of a success, so a dialog closed by Escape announces nothing.
 * `dialogRef` lets a dialog close itself before handing back (« Nouveau fournisseur »).
 */
export function ModalDialog({ title, onClose, dialogRef, children }: {
  title: string;
  onClose: () => void;
  dialogRef?: RefObject<HTMLDialogElement | null>;
  children: (titleId: string) => ReactNode;
}) {
  const own = useRef<HTMLDialogElement>(null);
  const ref = dialogRef ?? own;
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => { if (dialog?.open) dialog.close(); };
  }, [ref]);

  return createPortal(
    <dialog ref={ref} aria-labelledby={titleId}
      onCancel={(event) => { event.preventDefault(); onClose(); }}
      onKeyDown={(event) => { if (event.key === 'Escape') { event.preventDefault(); onClose(); } }}
      className="m-auto w-full max-w-md rounded-md border bg-background p-4 text-foreground backdrop:bg-black/50">
      <div className="space-y-3">
        <h2 id={titleId} className="text-lg font-semibold">{title}</h2>
        {children(titleId)}
      </div>
    </dialog>,
    document.body,
  );
}
