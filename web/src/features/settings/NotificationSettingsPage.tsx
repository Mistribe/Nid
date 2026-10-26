import { ChevronRightIcon } from 'lucide-react';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { DiscordWebhookControl } from '../notifications/DiscordWebhookControl';
import { channelErrorText, discordFieldText } from '../notifications/channelErrors';
import { fieldErrorsOf } from '../subscriptions/fieldErrors';
import {
  DISCORD_PERSON, NOTIFICATION_PREFERENCES, usePreferences, useReloadPreferences, useWebhook, useSavePreferences, useTestChannel,
  type NotificationPreferencesBody, type PreferencesView,
} from '../notifications/preferences';
import { SettingsFrame } from './SettingsFrame';
import '@/locales/notifications';

const WEEKDAYS = [1, 2, 3, 4, 5, 6, 7] as const;
const LEAD_PREFIX = 'leadDays.';

/** A number field's text as the integer the server checks: anything else goes as -1, which it refuses on that field. */
const daysOf = (text: string) => (/^\d+$/.test(text.trim()) ? Number(text) : -1);

/** Réglages › Notifications (notifications spec §6.3): channels, rhythm, lead times; one « Enregistrer » for the whole set. */
export function NotificationSettingsPage() {
  const { t } = useTranslation();
  const query = usePreferences();
  const [saved, setSaved] = useState(false);

  return (
    <SettingsFrame title={t('notification_settings.title')}>
      {query.data ? (
        // A new version (saved here, or reloaded after a conflict) starts the form again from what the server holds.
        <PreferencesForm key={query.data.version} prefs={query.data} saved={saved} onSaved={setSaved} />
      ) : query.error ? (
        <ProblemMessage problem={present(toProblem(query.error))} />
      ) : (
        <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('notification_settings.loading')}</span></Skeleton>
      )}
    </SettingsFrame>
  );
}

function PreferencesForm({ prefs, saved, onSaved }: { prefs: PreferencesView; saved: boolean; onSaved: (saved: boolean) => void }) {
  const { t } = useTranslation();
  const save = useSavePreferences();
  const reload = useReloadPreferences();
  const test = useTestChannel();
  const discordTest = useTestChannel();
  const webhook = useWebhook(DISCORD_PERSON, ['get', NOTIFICATION_PREFERENCES]);
  const [emailEnabled, setEmailEnabled] = useState(prefs.emailEnabled);
  const [action, setAction] = useState(prefs.action);
  const [info, setInfo] = useState(prefs.info);
  const [frequency, setFrequency] = useState(prefs.frequency);
  const [weekday, setWeekday] = useState(prefs.weekday);
  const [leads, setLeads] = useState<Record<string, string>>(() => Object.fromEntries(prefs.leadDays.map((l) => [l.kind, String(l.days)])));
  const [stale, setStale] = useState(false);
  const [open, setOpen] = useState(false);

  const emailId = useId();
  const emailNoteId = useId();
  const actionLabel = useId();
  const infoLabel = useId();
  const frequencyLabel = useId();

  const edited = <T,>(set: (value: T) => void) => (value: T) => { onSaved(false); set(value); };
  const messageOf = (code: string, fallback: string) => t(`notification_settings.error.${code.replaceAll('.', '_')}`, { defaultValue: fallback });

  const fields = fieldErrorsOf(save.error);
  const errorOf = (path: string) => { const f = fields.find((x) => x.path === path); return f ? messageOf(f.code, f.message) : undefined; };
  const leadError = (kind: string) => errorOf(`${LEAD_PREFIX}${kind}`);
  const placed = (path: string) => path === 'emailEnabled' || path === 'weekday' || (path.startsWith(LEAD_PREFIX) && path.slice(LEAD_PREFIX.length) in leads);
  const unplaced = save.error && !stale && !(fields.length > 0 && fields.every((f) => placed(f.path)));
  const leadsWithError = fields.some((f) => f.path.startsWith(LEAD_PREFIX));

  const submit = () => {
    onSaved(false);
    setStale(false);
    const body: NotificationPreferencesBody = {
      emailEnabled: emailEnabled && prefs.emailAvailable,
      action: action,
      info: info,
      frequency: frequency,
      weekday,
      leadDays: Object.fromEntries(prefs.leadDays.map((l) => [l.kind, daysOf(leads[l.kind] ?? '')])),
      version: prefs.version,
    };
    save.mutate(body, {
      onSuccess: () => onSaved(true),
      onError: (error) => setStale(toProblem(error).code === 'notifications.preferences_conflict'),
    });
  };

  const testFields = fieldErrorsOf(test.error);
  const testProblem = test.error && testFields.length === 0 ? present(toProblem(test.error)) : null;

  const discordFields = fieldErrorsOf(discordTest.error);
  const discordText = discordTest.data
    ? (discordTest.data.sent ? t('discord.test_sent') : t('discord.test_failed', { error: channelErrorText(t, discordTest.data.error ?? '') }))
    : discordFields[0] ? discordFieldText(t, discordFields[0]) : null;
  const discordProblem = discordTest.error && discordFields.length === 0 ? present(toProblem(discordTest.error)) : null;

  return (
    <form noValidate className="space-y-8" onSubmit={(e) => { e.preventDefault(); submit(); }}>
      <Section title={t('notification_settings.channels')}>
        <div className="flex items-center gap-2 text-sm">
          <Checkbox id={emailId} checked={emailEnabled && prefs.emailAvailable} disabled={!prefs.emailAvailable}
            aria-describedby={prefs.emailAvailable ? undefined : emailNoteId}
            onCheckedChange={(checked) => edited(setEmailEnabled)(checked === true)} />
          <Label htmlFor={emailId}>{t('notification_settings.email')}</Label>
        </div>
        {prefs.emailAvailable ? null : <p id={emailNoteId} className="text-sm text-muted-foreground">{t(prefs.hasEmail ? 'notification_settings.email_unavailable' : 'notification_settings.no_address')}</p>}
        {errorOf('emailEnabled') ? <p className="text-sm text-destructive">{errorOf('emailEnabled')}</p> : null}
        <div className="flex flex-wrap items-center gap-3">
          <Button type="button" variant="outline" disabled={!prefs.emailAvailable || test.isPending}
            onClick={() => { test.reset(); test.mutate({ body: { channel: 'email' } }); }}>
            {t('notification_settings.test')}
          </Button>
          <p role="status" aria-live="polite" className="text-sm">
            {test.data ? (test.data.sent ? t('notification_settings.test_sent') : t('notification_settings.test_failed', { error: channelErrorText(t, test.data.error ?? '') })) : null}
            {testFields[0] ? messageOf(testFields[0].code, testFields[0].message) : null}
          </p>
        </div>
        {testProblem ? <ProblemMessage problem={testProblem} /> : null}
        <DiscordWebhookControl webhook={prefs.discord ?? null} onSave={async (url) => { await webhook.save(url); discordTest.reset(); }}
          onRemove={async () => { await webhook.remove(); discordTest.reset(); }} testProblem={discordProblem}
          pending={discordTest.isPending} removeConfirmation={t('discord.remove_person')}
          onTest={() => { discordTest.reset(); discordTest.mutate({ body: { channel: 'discord_person' } }, { onSuccess: (r) => { if (r.error?.startsWith('notifications.channel_broken')) void reload(); } }); }} testText={discordText} />
      </Section>

      <Section title={t('notification_settings.rhythm')}>
        <p className="text-sm text-muted-foreground">{t('notification_settings.critical')}</p>
        <div className="space-y-1">
          <p id={actionLabel} className="text-sm">{t('notification_settings.action')}</p>
          <Segmented aria-labelledby={actionLabel} value={action} onValueChange={edited(setAction)} options={[
            { value: 'immediate', label: t('notification_settings.action_immediate') },
            { value: 'digest', label: t('notification_settings.action_digest') },
          ]} />
        </div>
        <div className="space-y-1">
          <p id={infoLabel} className="text-sm">{t('notification_settings.info')}</p>
          <Segmented aria-labelledby={infoLabel} value={info} onValueChange={edited(setInfo)} options={[
            { value: 'digest', label: t('notification_settings.info_digest') },
            { value: 'centre', label: t('notification_settings.info_centre') },
          ]} />
        </div>
        <div className="space-y-1">
          <p id={frequencyLabel} className="text-sm">{t('notification_settings.digest')}</p>
          <Segmented aria-labelledby={frequencyLabel} value={frequency} onValueChange={edited(setFrequency)} options={[
            { value: 'daily', label: t('notification_settings.frequency_daily') },
            { value: 'weekly', label: t('notification_settings.frequency_weekly') },
          ]} />
        </div>
        {frequency === 'weekly' ? (
          <div className="max-w-48">
            <Field label={t('notification_settings.weekday')} error={errorOf('weekday')}>
              {(c) => (
                <SelectField {...c} value={String(weekday)} onValueChange={(v) => edited(setWeekday)(Number(v))}
                  options={WEEKDAYS.map((d) => ({ value: String(d), label: t(`notification_settings.weekday_${d}`) }))} />
              )}
            </Field>
          </div>
        ) : null}
      </Section>

      <Collapsible open={open || leadsWithError} onOpenChange={setOpen} className="space-y-2">
        <h2 className="text-lg font-semibold">
          <CollapsibleTrigger className="group flex items-center gap-1 rounded-md outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">
            <ChevronRightIcon aria-hidden="true" className="size-4 transition-transform group-data-[state=open]:rotate-90" />
            {t('notification_settings.lead_days')}
          </CollapsibleTrigger>
        </h2>
        <CollapsibleContent className="space-y-3">
          {prefs.leadDays.map((lead) => {
            const value = leads[lead.kind] ?? '';
            return (
              <div key={lead.kind} className="flex flex-wrap items-start gap-3">
                <div className="w-48">
                  <Field label={lead.title} hint={t('notification_settings.default_days', { n: lead.defaultDays })} error={leadError(lead.kind)}>
                    {(c) => (
                      <Input {...c} type="number" inputMode="numeric" min={0} max={365} step={1} value={value}
                        onChange={(e) => { onSaved(false); setLeads((all) => ({ ...all, [lead.kind]: e.target.value })); }} />
                    )}
                  </Field>
                </div>
                <Button type="button" variant="ghost" size="sm" className="mt-6" disabled={value === String(lead.defaultDays)}
                  aria-label={t('notification_settings.reset_of', { title: lead.title })}
                  onClick={() => { onSaved(false); setLeads((all) => ({ ...all, [lead.kind]: String(lead.defaultDays) })); }}>
                  {t('notification_settings.reset')}
                </Button>
              </div>
            );
          })}
        </CollapsibleContent>
      </Collapsible>

      {stale ? (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-md border border-destructive p-3 text-sm">
          <p>{t('notification_settings.conflict')}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => { setStale(false); save.reset(); void reload(); }}>
            {t('notification_settings.reload')}
          </Button>
        </div>
      ) : null}
      {unplaced ? <ProblemMessage problem={present(toProblem(save.error))} /> : null}

      <div className="flex items-center gap-3">
        <Button type="submit" disabled={save.isPending}>{t('notification_settings.save')}</Button>
        <p role="status" aria-live="polite" className="text-sm">{saved ? t('notification_settings.saved') : null}</p>
      </div>
    </form>
  );
}
