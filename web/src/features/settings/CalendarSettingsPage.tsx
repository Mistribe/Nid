import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { ConfirmAction } from '@/components/ConfirmAction';
import { Segmented } from '@/components/form/Segmented';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { formatInstant, formatInstantDay } from '@/lib/format';
import { TOKEN, TOKENS } from '../security/tokens';
import { SettingsFrame } from './SettingsFrame';
import '@/locales/calendar';

export const CALENDAR = '/api/v1/core/me/calendar';

type Feed = components['schemas']['CalendarFeedView'];
type Scope = 'household' | 'me';

/**
 * Issue #80, Réglages › Calendrier: « À venir » as a calendar subscription. The address carries its secret, so it is shown once,
 * after a creation or a regeneration, and lives in this component's state only (never in the query cache).
 */
export function CalendarSettingsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const mine = $api.useQuery('get', CALENDAR);
  const [url, setUrl] = useState<string | null>(null);

  return (
    <SettingsFrame title={t('calendar_settings.title')}>
      <Section title={t('calendar_settings.section')}>
        <p>{t('calendar_settings.intro')}</p>
        {url ? <CreatedUrl url={url} onDone={() => setUrl(null)} /> : null}
        {mine.data ? (
          // A new feed (created, regenerated) starts the form again from what the server holds.
          <FeedForm key={mine.data.feed?.token.id ?? 'none'} feed={mine.data.feed ?? null} onCreated={setUrl} />
        ) : mine.error ? (
          <ProblemMessage problem={present(toProblem(mine.error))} />
        ) : (
          <Skeleton aria-busy="true" className="h-24"><span className="sr-only">{t('calendar_settings.loading')}</span></Skeleton>
        )}
      </Section>
    </SettingsFrame>
  );
}

function FeedForm({ feed, onCreated }: { feed: Feed | null; onCreated: (url: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [scope, setScope] = useState<Scope>(feed?.scope === 'me' ? 'me' : 'household');
  const [amounts, setAmounts] = useState(feed?.amounts ?? false);
  const scopeId = useId();
  const amountsId = useId();
  const amountsHintId = useId();
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['get', CALENDAR] });
    void queryClient.invalidateQueries({ queryKey: ['get', TOKENS] });
  };
  // gcTime 0: the response (with the address) leaves the mutation cache as soon as nothing observes it.
  const create = $api.useMutation('post', CALENDAR, { gcTime: 0, onSuccess: (created) => { onCreated(created.url); refresh(); } });
  const save = $api.useMutation('put', CALENDAR, { onSuccess: () => { toast.success(t('calendar_settings.saved')); refresh(); } });
  const revoke = $api.useMutation('delete', TOKEN, { onSuccess: () => { toast.success(t('calendar_settings.revoked')); refresh(); } });
  const problem = [create.error, save.error, revoke.error].map((e) => (e ? toProblem(e) : null)).find((p) => p !== null) ?? null;
  const changed = feed !== null && (feed.scope !== scope || feed.amounts !== amounts);

  return (
    <div className="space-y-4">
      {problem?.code === 'core.token_limit_reached'
        ? <p role="alert">{t('calendar_settings.limit_reached')}</p>
        : problem ? <ProblemMessage problem={present(problem)} /> : null}
      <div className="space-y-2">
        <p id={scopeId} className="font-medium">{t('calendar_settings.scope_label')}</p>
        <Segmented aria-labelledby={scopeId} value={scope} onValueChange={(v) => setScope(v as Scope)}
          options={(['household', 'me'] as const).map((s) => ({ value: s, label: t(`calendar_settings.scope.${s}`) }))} />
      </div>
      <div className="space-y-1">
        <div className="flex items-center gap-2">
          <Checkbox id={amountsId} checked={amounts} aria-describedby={amountsHintId} onCheckedChange={(checked) => setAmounts(checked === true)} />
          <Label htmlFor={amountsId}>{t('calendar_settings.amounts')}</Label>
        </div>
        <p id={amountsHintId} className="text-sm text-muted-foreground">{t('calendar_settings.amounts_hint')}</p>
      </div>
      {feed === null ? (
        <Button type="button" disabled={create.isPending} onClick={() => create.mutate({ body: { scope, amounts } })}>{t('calendar_settings.create')}</Button>
      ) : (
        <>
          <Button type="button" disabled={!changed || save.isPending} onClick={() => save.mutate({ body: { scope, amounts } })}>{t('calendar_settings.save')}</Button>
          <div className="space-y-1 text-sm">
            <p className="font-medium">{t('calendar_settings.active')}</p>
            <p className="text-muted-foreground">{t('calendar_settings.expires', { date: formatInstantDay(feed.token.expiresAt) })}</p>
            <p className="text-muted-foreground">
              {feed.token.lastUsedAt ? t('calendar_settings.last_used', { date: formatInstant(feed.token.lastUsedAt) }) : t('calendar_settings.never_used')}
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <ConfirmAction label={t('calendar_settings.regenerate')} confirmation={t('calendar_settings.regenerate_confirm')} pending={create.isPending}
              onConfirm={() => create.mutate({ body: { scope: feed.scope, amounts: feed.amounts } })} />
            <ConfirmAction label={t('calendar_settings.revoke')} confirmation={t('calendar_settings.revoke_confirm')} pending={revoke.isPending}
              onConfirm={() => revoke.mutate({ params: { path: { id: feed.token.id } } })} />
          </div>
        </>
      )}
    </div>
  );
}

/** The address, once: copied, or opened as `webcal://` so the system's calendar app subscribes to it. */
function CreatedUrl({ url, onDone }: { url: string; onDone: () => void }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const titleId = useId();
  const webcal = url.replace(/^https?:\/\//, 'webcal://');
  return (
    <div role="region" aria-labelledby={titleId} className="space-y-3 rounded-lg border p-3">
      <p id={titleId} className="font-medium">{t('calendar_settings.url_title')}</p>
      <p>{t('calendar_settings.url_once')}</p>
      <code className="block break-all rounded-md border bg-muted p-2 font-mono text-sm">{url}</code>
      <p className="text-sm text-muted-foreground">{t('calendar_settings.url_secret')}</p>
      <p role="status">{copied ? t('calendar_settings.copied') : ''}</p>
      <div className="flex flex-wrap justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => { void navigator.clipboard.writeText(url).then(() => setCopied(true)); }}>
          {t('calendar_settings.copy')}
        </Button>
        <Button asChild variant="outline"><a href={webcal}>{t('calendar_settings.open')}</a></Button>
        <Button type="button" onClick={onDone}>{t('calendar_settings.done')}</Button>
      </div>
    </div>
  );
}
