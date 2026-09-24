import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';

/**
 * A write in the admin area is confirmed in place: the button turns into the effect it will have, and a choice
 * (screens.md §12). Inline rather than a dialog, so it stays keyboard-first and needs no focus trap.
 */
export function ConfirmAction(props: { label: string; confirmation: string; onConfirm: () => void; disabled?: boolean }) {
  const { t } = useTranslation();
  const [asking, setAsking] = useState(false);

  if (!asking)
    return <Button variant="outline" size="sm" disabled={props.disabled} onClick={() => setAsking(true)}>{props.label}</Button>;

  return (
    <span role="group" aria-label={props.label} className="inline-flex flex-wrap items-center gap-2">
      <span>{props.confirmation}</span>
      <Button size="sm" onClick={() => { setAsking(false); props.onConfirm(); }}>{t('admin.confirm')}</Button>
      <Button size="sm" variant="ghost" onClick={() => setAsking(false)}>{t('admin.cancel')}</Button>
    </span>
  );
}
