import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';

/**
 * A write in the admin area is confirmed in place: the button turns into the effect it will have, and a choice
 * (screens.md §12). Inline rather than a dialog, so it stays keyboard-first and needs no focus trap. Because the
 * trigger unmounts when the confirmation group appears, focus is moved deliberately: to the Confirm button when
 * it appears, and back to the trigger once the group is dismissed (Cancel or Confirm) — otherwise it falls to
 * <body>.
 */
export function ConfirmAction(props: {
  label: string;
  confirmation: string;
  onConfirm: () => void;
  disabled?: boolean;
  /** Disables the trigger and the Confirm button while the mutation this action starts is in flight. */
  pending?: boolean;
}) {
  const { t } = useTranslation();
  const [asking, setAsking] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const wasAsking = useRef(false);

  useEffect(() => {
    if (wasAsking.current && !asking) triggerRef.current?.focus();
    wasAsking.current = asking;
  }, [asking]);

  if (!asking) {
    return (
      <Button ref={triggerRef} variant="outline" size="sm" disabled={props.disabled || props.pending} onClick={() => setAsking(true)}>
        {props.label}
      </Button>
    );
  }

  return (
    <span role="group" aria-label={props.label} className="inline-flex flex-wrap items-center gap-2">
      <span>{props.confirmation}</span>
      <Button autoFocus size="sm" disabled={props.pending} onClick={() => { setAsking(false); props.onConfirm(); }}>{t('admin.confirm')}</Button>
      <Button size="sm" variant="ghost" onClick={() => setAsking(false)}>{t('admin.cancel')}</Button>
    </span>
  );
}
