import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import '@/locales/deletion';

type DeletionPreview = components['schemas']['ItemDeletionPreview'];

/** Deleting an item created by mistake: what goes with it, then the name typed back (nothing happens on Enter). */
export function DeleteItemDialog({ preview, previewError, closeHint, pending, error, onConfirm, onClose }: {
  preview: DeletionPreview | undefined; previewError: unknown; closeHint?: string; pending: boolean; error: unknown;
  onConfirm: (version: number) => void; onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
  const [typed, setTyped] = useState('');
  const [version, setVersion] = useState(preview?.version);
  // A refetched preview (after a 409) is a new decision: the name is typed again.
  if (preview?.version !== version) { setVersion(preview?.version); setTyped(''); }
  const label = (key: string, count: number) => t(`deletion.keys.${key}`, { count, defaultValue: key });
  const blocked = (preview?.blockers.length ?? 0) > 0;
  return (
    <ModalDialog title={t('deletion.title', { name: preview?.name ?? '' })} onClose={onClose}>
      {() => previewError || !preview ? (
        <div className="space-y-4">
          {previewError ? <ProblemMessage problem={present(toProblem(previewError))} /> : <p>{t('deletion.loading')}</p>}
          <div className="flex justify-end"><Button type="button" variant="outline" onClick={onClose}>{t('common.close')}</Button></div>
        </div>
      ) : (
        <form className="space-y-4" onSubmit={(event) => event.preventDefault()}>
          <p className="rounded-md bg-destructive/10 p-3 text-sm text-destructive">{closeHint ? t('deletion.warning', { hint: closeHint }) : t('deletion.warning_closed')}</p>
          {preview.removes.length > 0 ? (
            <div className="space-y-1 text-sm">
              <p>{t('deletion.removes')}</p>
              <ul className="list-disc pl-5">{preview.removes.map((r) => <li key={r.key}>{label(r.key, r.count)}</li>)}</ul>
            </div>
          ) : null}
          <p className="text-sm">{t('deletion.recomputed')}</p>
          {preview.imported ? <p className="text-sm">{t('deletion.imported')}</p> : null}
          {blocked ? (
            <div className="space-y-1 text-sm">
              <p className="font-medium">{t('deletion.blocked')}</p>
              <ul className="list-disc pl-5">
                {preview.blockers.map((b) => (
                  <li key={b.key}>
                    {label(b.key, b.count)}{b.items.length > 0 ? ` : ${b.items.map((i) => i.name).join(', ')}` : null}
                    {i18n.exists(`deletion.way_out.${b.key}`) ? <span className="block text-muted-foreground">{t(`deletion.way_out.${b.key}`, { defaultValue: '' })}</span> : null}
                  </li>
                ))}
              </ul>
            </div>
          ) : (
            <div className="space-y-2">
              <label htmlFor="delete-confirm" className="text-sm font-medium">{t('deletion.type', { name: preview.name })}</label>
              <Input id="delete-confirm" autoFocus autoComplete="off" value={typed} onChange={(event) => setTyped(event.target.value)} />
            </div>
          )}
          {error ? <ProblemMessage problem={present(toProblem(error))} /> : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>{t(blocked ? 'common.close' : 'common.cancel')}</Button>
            {blocked ? null : (
              <Button type="button" variant="destructive" disabled={pending || typed !== preview.name} onClick={() => onConfirm(preview.version)}>{t('deletion.confirm')}</Button>
            )}
          </div>
        </form>
      )}
    </ModalDialog>
  );
}
