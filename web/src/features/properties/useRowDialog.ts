import { useEffect, useRef, useState } from 'react';

/**
 * The state every table of the property shares (the loan fees' pattern): one dialog (adding, or correcting `editing`), the
 * section's live region and where the focus lands — the heading after a save, the row's menu (`${menuPrefix}-${id}`) after
 * « Annuler », the add button when adding. A correction whose row is gone (then « Recharger ») closes onto the heading.
 */
export function useRowDialog<T extends { id: string }>(rows: T[], menuPrefix: string) {
  const [dialog, setDialog] = useState<{ editing?: T } | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [focusId, setFocusId] = useState<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const open = (editing?: T) => { setAnnouncement(''); setDialog({ editing }); };
  const saved = (message: string) => { setDialog(null); announce(message); };
  const cancel = () => { const row = dialog?.editing?.id; setDialog(null); if (row) setFocusId(row); else addButtonRef.current?.focus(); };
  useEffect(() => {
    if (!focusId) return;
    // No such menu (the row is gone): the heading.
    (document.getElementById(`${menuPrefix}-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId, menuPrefix]);
  useEffect(() => {
    if (dialog?.editing && !rows.some((r) => r.id === dialog.editing?.id)) { setDialog(null); setFocusId('heading'); }
  }, [dialog, rows]);

  return { dialog, open, saved, cancel, announce, announcement, setAnnouncement, headingRef, addButtonRef };
}
