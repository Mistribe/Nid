import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem, type Presentation } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ConfirmAction } from '@/components/ConfirmAction';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { fieldErrorsOf } from '../subscriptions/fieldErrors';
import { discordFieldText } from './channelErrors';
import '@/locales/notifications';

/** One Discord webhook, for Réglages and Administration. Not part of the preferences form: its buttons are type="button" and Enter saves the webhook only. */
export function DiscordWebhookControl({ webhook, onSave, onRemove, onTest, testText, removeConfirmation, testProblem, pending }: {
  webhook: { hint: string; status: string } | null;
  onSave: (url: string) => Promise<void>;
  onRemove: () => Promise<void>;
  onTest: () => void;
  testText: string | null;
  /** What removing the webhook means for whoever receives on it (the dialog's body). */
  removeConfirmation: string;
  /** A test that failed with something other than a field error. */
  testProblem?: Presentation | null;
  pending: boolean;
}) {
  const { t } = useTranslation();
  const [url, setUrl] = useState('');
  const [replacing, setReplacing] = useState(false);
  const [error, setError] = useState<string>();
  const [other, setOther] = useState<Presentation>();
  const [saving, setSaving] = useState(false);
  const [focus, setFocus] = useState<'field' | 'replace' | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const replaceRef = useRef<HTMLButtonElement>(null);
  const editing = !webhook || replacing;

  // A swap unmounts the button that had focus: hand it to the control that takes its place (WCAG 2.4.3).
  useEffect(() => {
    const target = focus === 'field' ? inputRef.current : focus === 'replace' ? replaceRef.current : null;
    if (target) { target.focus(); setFocus(null); }
  }, [focus, editing, webhook]);

  const cancel = () => { setReplacing(false); setUrl(''); setError(undefined); setFocus('replace'); };

  const save = async () => {
    if (saving) return;
    setSaving(true);
    setError(undefined);
    setOther(undefined);
    try {
      await onSave(url);
      setUrl('');
      setReplacing(false);
      setFocus('replace');
    } catch (e) {
      const field = fieldErrorsOf(e).find((f) => f.path === 'url');
      if (field) setError(discordFieldText(t, field));
      else setOther(present(toProblem(e)));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-2">
      {editing ? (
        <div className="flex flex-wrap items-end gap-3">
          <div className="w-full max-w-lg">
            <Field label={t('discord.label')} hint={t('discord.hint')} error={error}>
              {(c) => (
                <Input {...c} ref={inputRef} type="url" autoComplete="off" value={url} onChange={(e) => setUrl(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void save(); } }} />
              )}
            </Field>
          </div>
          <Button type="button" aria-label={t('discord.save_of')} disabled={pending || saving || url.trim() === ''} onClick={() => void save()}>{t('discord.save')}</Button>
          {webhook ? <Button type="button" variant="ghost" onClick={cancel}>{t('discord.cancel')}</Button> : null}
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span>{t('discord.label')} — {t('discord.saved', { hint: webhook.hint })}</span>
          <Button ref={replaceRef} type="button" variant="outline" size="sm" onClick={() => { setReplacing(true); setFocus('field'); }}>{t('discord.replace')}</Button>
          <ConfirmAction label={t('discord.remove')} title={t('discord.remove_title')} confirmation={removeConfirmation} pending={pending}
            onConfirm={() => { setOther(undefined); onRemove().then(() => setFocus('field'), (e) => setOther(present(toProblem(e)))); }} />
        </div>
      )}
      {other ?? testProblem ? <ProblemMessage problem={(other ?? testProblem) as Presentation} /> : null}
      {webhook?.status === 'broken' ? <p role="alert" className="text-sm text-status-warning-text">{t('discord.broken')}</p> : null}
      {webhook ? (
        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" variant="outline" disabled={pending || webhook.status === 'broken'} onClick={onTest}>{t('discord.test')}</Button>
          <p role="status" aria-live="polite" className="text-sm">{testText}</p>
        </div>
      ) : null}
    </div>
  );
}
