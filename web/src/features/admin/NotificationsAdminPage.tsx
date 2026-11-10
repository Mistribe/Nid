import { useState } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SortableTable } from '@/components/page/SortableTable';
import { Button } from '@/components/ui/button';
import { channelErrorText, discordFieldText, throttledText } from '@/features/notifications/channelErrors';
import { DiscordWebhookControl } from '@/features/notifications/DiscordWebhookControl';
import { DISCORD_HOUSEHOLD, useWebhook } from '@/features/notifications/preferences';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { AdminFrame } from './AdminFrame';
import { Loading, SectionSkeleton } from '@/components/page/skeletons';
import { Section } from '@/components/page/Section';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';
import '@/locales/notifications';

type TestOutcome = { kind: 'sent' } | { kind: 'throttled'; text: string } | { kind: 'refused'; error: string } | { kind: 'problem'; problem: Presentation };

/** Administration › Notifications (spec §6.4): the email channel's state, a test send, the deliveries that failed. */
export function NotificationsAdminPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const view = $api.useQuery('get', '/api/v1/admin/notifications');
  const [outcome, setOutcome] = useState<TestOutcome | null>(null);
  const test = $api.useMutation('post', '/api/v1/admin/notifications/test', {
    onSuccess: (result) => setOutcome(result.sent ? { kind: 'sent' } : { kind: 'refused', error: result.error ?? '' }),
    onError: (error) => {
      const throttled = throttledText(t, error);
      setOutcome(throttled ? { kind: 'throttled', text: throttled } : { kind: 'problem', problem: present(toProblem(error)) });
    },
  });

  const webhook = useWebhook(DISCORD_HOUSEHOLD, ['get', '/api/v1/admin/notifications']);
  const [discordOutcome, setDiscordOutcome] = useState<string | null>(null);
  const [discordProblem, setDiscordProblem] = useState<Presentation | null>(null);
  const clearDiscord = () => { setDiscordOutcome(null); setDiscordProblem(null); };
  const discordTest = $api.useMutation('post', '/api/v1/admin/notifications/test', {
    onSuccess: (result) => {
      setDiscordOutcome(result.sent ? t('discord.test_sent') : t('discord.test_failed', { error: channelErrorText(t, result.error ?? '') }));
      // The send just marked the webhook broken: show it at once.
      if (result.error?.startsWith('notifications.channel_broken')) void queryClient.invalidateQueries({ queryKey: ['get', '/api/v1/admin/notifications'] });
    },
    onError: (error) => {
      const f = fieldErrorsOf(error)[0];
      const throttled = throttledText(t, error);
      if (f) setDiscordOutcome(discordFieldText(t, f));
      else if (throttled) setDiscordOutcome(throttled);
      else setDiscordProblem(present(toProblem(error)));
    },
  });

  const title = t('admin.notifications.title');
  if (view.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(view.error))} /></AdminFrame>;
  if (!view.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><SectionSkeleton /></Loading></AdminFrame>;
  const { smtpConfigured, smtpHost, failures } = view.data;

  return (
    <AdminFrame title={title}>
      <Section title={t('admin.notifications.email_title')}>
        <p>{smtpConfigured ? t('admin.notifications.configured', { host: smtpHost }) : t('admin.notifications.not_configured')}</p>
        {smtpConfigured ? null : (
          <p className="text-sm text-muted-foreground">
            <Trans i18nKey="admin.notifications.hint" components={{ code: <code className="font-mono" /> }} />
          </p>
        )}
        <Button variant="outline" disabled={!smtpConfigured || test.isPending}
          onClick={() => { setOutcome(null); test.mutate({ body: { channel: 'email' } }); }}>
          {t('admin.notifications.test')}
        </Button>
        <p role="status">{outcome?.kind === 'sent' ? t('admin.notifications.test_sent') : outcome?.kind === 'throttled' ? outcome.text : null}</p>
        {outcome?.kind === 'refused' ? (
          <div role="alert" className="space-y-1 rounded-md border border-destructive p-3">
            <p>{t('admin.notifications.test_failed')}</p>
            {outcome.error ? <p className="text-sm">{channelErrorText(t, outcome.error)} <code className="font-mono">{outcome.error}</code></p> : null}
          </div>
        ) : null}
        {outcome?.kind === 'problem' ? <ProblemMessage problem={outcome.problem} /> : null}
      </Section>

      <Section title={t('admin.notifications.discord_title')}>
        <p className="text-sm text-muted-foreground">{t('admin.notifications.discord_hint')}</p>
        <DiscordWebhookControl webhook={view.data.discord ?? null} onSave={async (url) => { await webhook.save(url); clearDiscord(); }}
          onRemove={async () => { await webhook.remove(); clearDiscord(); }} testProblem={discordProblem}
          pending={discordTest.isPending} removeConfirmation={t('discord.remove_household')} testText={discordOutcome}
          onTest={() => { clearDiscord(); discordTest.mutate({ body: { channel: 'discord_household' } }); }} />
      </Section>

      <Section title={t('admin.notifications.failures_title')}>
        {failures.length === 0 ? (
          <EmptyState module="/admin" title={t('admin.notifications.no_failures')} body="" level={3} />
        ) : (
          <SortableTable
            caption={t('admin.notifications.failures_title')}
            rows={failures}
            rowKey={(f) => f.id}
            initialSort={{ id: 'at', desc: true }}
            columns={[
              { id: 'channel', header: t('admin.notifications.channel'), cell: (f) => f.channel, sort: (f) => f.channel },
              { id: 'kind', header: t('admin.notifications.kind'), cell: (f) => <Kind kind={f.kind} />, sort: (f) => f.kind },
              { id: 'at', header: t('admin.notifications.queued'), cell: (f) => formatInstant(f.at), sort: (f) => Date.parse(f.at) },
              { id: 'error', header: t('admin.notifications.error'), cell: (f) => <ErrorText error={f.error} /> },
            ]}
            mobile={(f) => (
              <div className="space-y-1">
                <p><Kind kind={f.kind} /> · {f.channel}</p>
                <p className="text-sm text-muted-foreground">{formatInstant(f.at)}</p>
                <p className="text-sm"><ErrorText error={f.error} /></p>
              </div>
            )}
          />
        )}
      </Section>
    </AdminFrame>
  );
}

/** « Résumé » for a digest, the kind's code otherwise: the admin page holds no notification row to name it. */
function Kind({ kind }: { kind: string }) {
  const { t } = useTranslation();
  return kind === 'digest' ? <>{t('admin.notifications.digest')}</> : <code className="font-mono">{kind}</code>;
}

/** The sentence for the error code, then the code itself for whoever reads the logs. */
function ErrorText({ error }: { error: string }) {
  const { t } = useTranslation();
  const text = channelErrorText(t, error);
  return text === error ? <code className="font-mono">{error}</code> : <>{text} <code className="font-mono">{error}</code></>;
}
