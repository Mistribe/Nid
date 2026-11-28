import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, CAPABILITIES, CLOUD, CLOUD_DISABLE, CLOUD_ENABLE, CLOUD_KEY, CLOUD_REGION, fieldError, type CloudProfile } from '@/api/ai';
import { ConfirmAction } from '@/components/ConfirmAction';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { PasswordInput } from '@/components/form/PasswordInput';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { formatLocalDate } from '@/components/LocalDate';
import '@/locales/ai';

/**
 * « Fournisseurs cloud » (#487, docs/ingestion/llm-providers.md §4, §10, §13): the profiles Nid ships, with what their terms
 * say; an admin confirms the region, enters a provider's key (never shown again) and enables a profile after reading its
 * terms.
 */
export function CloudSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const cloud = $api.useQuery('get', CLOUD);
  const refresh = () => Promise.all([CLOUD, CAPABILITIES].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] })));
  const done = (message: string) => ({ onSuccess: () => { toast.success(message); void refresh(); } });
  const region = $api.useMutation('put', CLOUD_REGION, done(t('ai.cloud.region_saved')));
  const enable = $api.useMutation('post', CLOUD_ENABLE, done(t('ai.cloud.enabled')));
  const disable = $api.useMutation('post', CLOUD_DISABLE, done(t('ai.cloud.disabled')));
  const revoke = $api.useMutation('delete', CLOUD_KEY, done(t('ai.cloud.key_revoked')));
  const [keying, setKeying] = useState<CloudProfile | null>(null);
  const error = cloud.error ?? region.error ?? enable.error ?? disable.error ?? revoke.error;

  const regionLabel = (value: string) => t(`ai.cloud.region_${value}` as 'ai.cloud.region_eea_ch_uk', { defaultValue: value });
  const profile = (p: CloudProfile) => {
    const name = t('ai.cloud.profile_name', { provider: t(`ai.cloud.provider.${p.provider}` as 'ai.cloud.provider.anthropic', { defaultValue: p.provider }), model: p.model });
    const path = { params: { path: { id: p.id } } };
    const keyPath = { params: { path: { provider: p.provider } } };
    return (
      <li key={p.id} className="space-y-2 border-b pb-4 last:border-b-0">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="text-sm font-medium">{name}</h3>
          <Badge variant="outline">{t(p.tier === 'free' ? 'ai.cloud.tier_free' : 'ai.cloud.tier_paid')}</Badge>
          {p.capabilities.includes('embed') ? <Badge variant="outline">{t('ai.cloud.embed')}</Badge> : null}
          {p.capabilities.includes('decide') ? <Badge variant="outline">{t('ai.cloud.decide')}</Badge> : null}
          {p.enabled ? <Badge variant="secondary">{t('ai.cloud.on')}</Badge> : null}
          {p.suspended ? <Badge variant="outline">{t('ai.cloud.suspended')}</Badge> : null}
        </div>
        <ul className="space-y-0.5 text-sm text-muted-foreground">
          <li>{t(p.trainsOnData ? 'ai.cloud.trains' : 'ai.cloud.no_training')}</li>
          <li className="flex items-center gap-1.5">
            {/* #812: null when its terms do not say. */}
            {t(p.humanReview == null ? 'ai.cloud.human_review_unknown' : p.humanReview ? 'ai.cloud.human_review' : 'ai.cloud.no_human_review')}
            <InfoTip label={t('ai.cloud.human_review_label')}>{t('ai.cloud.human_review_tip')}</InfoTip>
          </li>
          {p.retentionDays != null ? <li>{t('ai.cloud.retention', { count: p.retentionDays })}</li> : null}
          <li>
            {t('ai.cloud.reviewed', { date: formatLocalDate(p.reviewedOn) })}{' '}
            <a href={p.termsUrl} target="_blank" rel="noreferrer" className="font-medium text-foreground underline underline-offset-4">{t('ai.cloud.terms')}</a>
          </li>
          {p.reviewDue ? <li className="text-status-warning-text">{t('ai.cloud.review_due')}</li> : null}
        </ul>
        {p.provider === 'gemini' ? <p className="text-sm text-muted-foreground">{t('ai.cloud.gemini_paid')}</p> : null}
        {p.provider === 'typesafe' ? <p className="text-sm text-muted-foreground">{t('ai.cloud.typesafe_us')}</p> : null}
        <p className="text-sm">{p.keyHint ? t('ai.cloud.key_hint', { hint: p.keyHint }) : t('ai.cloud.no_key')}</p>
        <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" size="sm" onClick={() => setKeying(p)}>{t(p.keyHint ? 'ai.cloud.replace_key' : 'ai.cloud.add_key')}</Button>
            {p.keyHint ? (
              <ConfirmAction label={t('ai.cloud.revoke_key')} confirmation={t('ai.cloud.revoke_key_confirm', { name })} pending={revoke.isPending}
                onConfirm={() => revoke.mutate(keyPath)} />
            ) : null}
            {p.enabled ? (
              <ConfirmAction label={t('ai.cloud.disable')} confirmation={t('ai.cloud.disable_confirm', { name })} pending={disable.isPending}
                onConfirm={() => disable.mutate(path)} />
            ) : (
              <ConfirmAction label={t('ai.cloud.enable')} pending={enable.isPending}
                confirmation={t('ai.cloud.enable_confirm', { name, date: formatLocalDate(p.reviewedOn) })}
                onConfirm={() => enable.mutate({ ...path, body: { termsReviewedOn: p.reviewedOn } })} />
            )}
        </div>
      </li>
    );
  };

  return (
    <Section title={t('ai.cloud.title')} tip={<InfoTip label={t('ai.cloud.title')}>{t('ai.cloud.tip')}</InfoTip>}>
      {error ? <ProblemMessage problem={aiProblem(t, error)} /> : null}
      {cloud.data ? (
        <>
          <div className="max-w-sm">
              <Field label={t('ai.cloud.region')} hint={t('ai.cloud.region_hint')}>
                {(c) => (
                  <SelectField {...c} value={cloud.data.region ?? ''} onValueChange={(value) => value && region.mutate({ body: { region: value } })}
                    placeholder={t('ai.cloud.region_unset')}
                    options={['eea_ch_uk', 'other'].map((value) => ({ value, label: regionLabel(value) }))} />
                )}
              </Field>
            </div>
          <ul className="space-y-4">{cloud.data.profiles.map(profile)}</ul>
        </>
      ) : null}
      {keying ? <KeyDialog profile={keying} onClose={() => setKeying(null)} onDone={(message) => { setKeying(null); toast.success(message); void refresh(); }} /> : null}
    </Section>
  );
}

/** A provider's key: typed once, sent once, stored encrypted; only its last four characters come back. */
function KeyDialog({ profile, onClose, onDone }: { profile: CloudProfile; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [key, setKey] = useState('');
  const save = $api.useMutation('put', CLOUD_KEY);
  const keyError = fieldError(t, save.error, 'key', t('ai.cloud.key_required'));
  return (
    <FormDialog title={t('ai.cloud.key_title', { provider: t(`ai.cloud.provider.${profile.provider}` as 'ai.cloud.provider.anthropic', { defaultValue: profile.provider }) })}
      onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          save.mutate({ params: { path: { provider: profile.provider } }, body: { key } }, { onSuccess: () => done(t('ai.cloud.key_saved')) });
        }}>
          {save.error && !keyError ? <ProblemMessage problem={aiProblem(t, save.error)} /> : null}
          <Field label={t('ai.cloud.key')} hint={t('ai.cloud.key_hint_text')} error={keyError}>
            {(c) => <PasswordInput {...c} autoComplete="new-password" value={key} onChange={(e) => setKey(e.target.value)} spellCheck={false} />}
          </Field>
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('ai.cloud.save_key')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
