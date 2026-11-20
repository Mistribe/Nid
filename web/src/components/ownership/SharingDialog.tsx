import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Segmented } from '@/components/form/Segmented';
import { Button } from '@/components/ui/button';
import '@/locales/forms';
import { usePrivateRequest } from './privateRequest';
import { ITEM_SHARING } from './SharingBadges';

/**
 * #294: « Partage », a shared item's level (view only, or view and edit). Only its owners change it: anyone else who reads it
 * sees the level and why they cannot change it. #384: its sole owner may also ask the other adult to make it private (« Privé (vous
 * seul) »): a request, not a change.
 */
export function SharingDialog({ item, onClose, onDone }: { item: string; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const options = { params: { path: { id: item } } };
  const sharing = $api.useQuery('get', ITEM_SHARING, options);
  const save = $api.useMutation('put', ITEM_SHARING);
  const asking = usePrivateRequest(item);
  const [askError, setAskError] = useState<unknown>(null);
  const [level, setLevel] = useState<string>();
  const labelId = useId();
  const effectId = useId();
  const current = level ?? sharing.data?.shareLevel ?? 'view';
  const canChange = sharing.data?.canChange === true;
  const choices = asking.canAsk ? (['view', 'edit', 'private'] as const) : (['view', 'edit'] as const);
  const effect = current === 'private' ? 'sharing.private_ask_effect' : current === 'edit' ? 'sharing.edit_effect' : 'sharing.view_effect';

  return (
    <FormDialog title={t('sharing.level')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          if (current === 'private') {
            setAskError(null);
            asking.settle(true, '', done, setAskError);
            return;
          }
          save.mutate({ ...options, body: { shareLevel: current } }, {
            onSuccess: () => {
              void queryClient.invalidateQueries({ queryKey: ['get', ITEM_SHARING, options] });
              done(t('sharing.saved'));
            },
          });
        }}>
          {save.error || askError ? <ProblemMessage problem={present(toProblem(save.error ?? askError))} /> : null}
          {sharing.data ? (
            <div className="space-y-1">
              <span id={labelId} className="sr-only">{t('sharing.level')}</span>
              {canChange ? (
                <Segmented aria-labelledby={labelId} aria-describedby={effectId} value={current} onValueChange={setLevel}
                  options={choices.map((v) => ({ value: v, label: t(`sharing.${v}`) }))} />
              ) : <p className="font-medium">{t(current === 'edit' ? 'sharing.edit' : 'sharing.view')}</p>}
              <p id={effectId} className="text-sm text-muted-foreground">
                {t(effect)}
                {canChange ? null : <> {t('sharing.not_owner')}</>}
              </p>
            </div>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('sharing.cancel')}</Button>
            {canChange ? <Button type="submit" disabled={save.isPending}>{t(current === 'private' ? 'sharing.ask' : 'sharing.save')}</Button> : null}
          </div>
        </form>
      )}
    </FormDialog>
  );
}
