import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { AI_SETTINGS, aiProblem, CAPABILITIES, CLOUD, DECLARED_MODELS, USE_MODE, USE_MODEL, type UseSetting } from '@/api/ai';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import '@/locales/ai';

/** The capability a worker declares for each use's models (`ModelUses.Capability` on the server). */
const CAPABILITY: Record<string, string> = { chat: 'chat', extract: 'chat', embed: 'embed', decide: 'decide' };

/**
 * « Modèles » (#486, decision H): for each use, how far it may go (any adult) and the model it runs on (an admin, among the
 * models the paired workers declare). Self-hosted is the default; going on to a cloud provider is offered once one is enabled.
 */
export function ModelsSection({ admin }: { admin: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const settings = $api.useQuery('get', AI_SETTINGS);
  const models = $api.useQuery('get', DECLARED_MODELS);
  const cloud = $api.useQuery('get', CLOUD);
  const cloudOn = cloud.data?.profiles.some((p) => p.enabled) === true;
  const refresh = () => Promise.all([AI_SETTINGS, CAPABILITIES].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] })));
  const saved = { onSuccess: () => { toast.success(t('ai.models.saved')); void refresh(); } };
  const mode = $api.useMutation('put', USE_MODE, saved);
  const model = $api.useMutation('put', USE_MODEL, saved);
  const error = settings.error ?? models.error ?? mode.error ?? model.error;

  const row = (use: UseSetting) => {
    const label = t(`ai.models.use.${use.use}` as 'ai.models.use.chat', { defaultValue: use.use });
    const path = { params: { path: { use: use.use } } };
    const offered = (models.data?.models ?? []).filter((m) => m.capability === CAPABILITY[use.use]);
    return (
      <div key={use.use} className="space-y-2 border-b pb-4 last:border-b-0">
        <h3 className="flex items-center gap-1.5 text-sm font-medium">
          {label}
          <InfoTip label={label}>{t(`ai.models.use_tip.${use.use}` as 'ai.models.use_tip.chat', { defaultValue: '' })}</InfoTip>
        </h3>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('ai.models.mode')}>
            {(c) => (
              <SelectField {...c} value={use.mode} onValueChange={(value) => mode.mutate({ ...path, body: { mode: value } })} options={[
                { value: 'self_hosted', label: t('ai.models.mode_self_hosted') },
                { value: 'off', label: t('ai.models.mode_off') },
                // Offered once a cloud profile is enabled (#487), or while set; never for embed, which no provider serves yet (#556).
                ...((cloudOn && use.use !== 'embed') || use.mode === 'self_hosted_then_cloud'
                  ? [{ value: 'self_hosted_then_cloud', label: t('ai.models.mode_cloud') }] : []),
              ]} />
            )}
          </Field>
          {admin ? (
            <Field label={t('ai.models.model')} hint={offered.length === 0 ? t('ai.models.none_declared') : undefined}>
              {(c) => (
                <SelectField {...c} value={use.model ?? ''} onValueChange={(value) => model.mutate({ ...path, body: { model: value || null } })}
                  options={[
                    { value: '', label: t('ai.models.no_model') },
                    ...offered.map((m) => ({ value: m.name, label: t('ai.models.served_by', { name: m.name, count: m.workers }) })),
                    // A model no worker declares any more stays visible until the admin picks another.
                    ...(use.model && !offered.some((m) => m.name === use.model) ? [{ value: use.model, label: t('ai.models.gone', { name: use.model }) }] : []),
                  ]} />
              )}
            </Field>
          ) : (
            <div className="space-y-1">
              <p className="text-sm">{t('ai.models.model')}</p>
              <p className="text-sm text-muted-foreground">{use.model ?? t('ai.models.no_model')}</p>
            </div>
          )}
        </div>
      </div>
    );
  };

  return (
    <Section title={t('ai.models.title')} tip={<InfoTip label={t('ai.models.title')}>{t('ai.models.tip')}</InfoTip>}>
      {error ? <ProblemMessage problem={aiProblem(t, error)} /> : null}
      {settings.data ? <div className="space-y-4">{settings.data.uses.map(row)}</div> : null}
    </Section>
  );
}
