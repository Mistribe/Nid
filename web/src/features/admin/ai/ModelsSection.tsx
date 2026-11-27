import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { AI_SETTINGS, aiProblem, CAPABILITIES, CLOUD, DECLARED_MODELS, USE_MODE, USE_MODEL, type UseSetting } from '@/api/ai';
import { SUGGESTION_CALIBRATIONS } from '@/api/banking';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { Input } from '@/components/ui/input';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { DecideCalibration } from './DecideCalibration';
import '@/locales/ai';

/** The capability a worker declares for each use's models (`ModelUses.Capability` on the server). */
const CAPABILITY: Record<string, string> = { chat: 'chat', extract: 'chat', embed: 'embed', decide: 'decide' };

/**
 * « Modèles » (#486, decision H, #585): for each use, how far it may go and the model it runs on, among the models the
 * paired workers declare. Self-hosted is the default; going on to a cloud provider is offered once one serving the use is
 * enabled. #556: `embed` may also run on an enabled cloud profile's model, the one model of the whole index; #812: `decide` too
 * (Jev). #819: the chat may go straight to the cloud, on each profile's own model: no worker, no model to choose.
 */
export function ModelsSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const settings = $api.useQuery('get', AI_SETTINGS);
  const models = $api.useQuery('get', DECLARED_MODELS);
  const cloud = $api.useQuery('get', CLOUD);
  const enabledFor = (use: string) => (cloud.data?.profiles ?? []).filter((p) => p.enabled && p.capabilities.includes(CAPABILITY[use] ?? use));
  // #450: what suggests categories follows the decide and embed uses' model.
  const refresh = () => Promise.all([AI_SETTINGS, CAPABILITIES, SUGGESTION_CALIBRATIONS].map((path) => queryClient.invalidateQueries({ queryKey: ['get', path] })));
  const saved = { onSuccess: () => { toast.success(t('ai.models.saved')); void refresh(); } };
  const mode = $api.useMutation('put', USE_MODE, saved);
  const model = $api.useMutation('put', USE_MODEL, saved);
  const error = settings.error ?? models.error ?? mode.error ?? model.error;
  const embedModel = settings.data?.uses.find((u) => u.use === 'embed' && u.mode !== 'off')?.model ?? null;

  const row = (use: UseSetting) => {
    const label = t(`ai.models.use.${use.use}` as 'ai.models.use.chat', { defaultValue: use.use });
    const path = { params: { path: { use: use.use } } };
    const offered = (models.data?.models ?? []).filter((m) => m.capability === CAPABILITY[use.use]);
    // #556, #812: a cloud profile's model is the use's only for embed and decide; a chat turn on the cloud runs on its profile's own model.
    const cloudModels = use.use === 'embed' || use.use === 'decide' ? enabledFor(use.use).filter((p) => !offered.some((m) => m.name === p.model)) : [];
    const onCloud = cloudModels.some((p) => p.model === use.model);
    const provider = (name: string) => t(`ai.cloud.provider.${name}` as 'ai.cloud.provider.anthropic', { defaultValue: name });
    return (
      <div key={use.use} className="space-y-2 border-b pb-4 last:border-b-0">
        <h3 className="flex items-center gap-1.5 text-sm font-medium">
          {label}
          <InfoTip label={label}>{t(`ai.models.use_tip.${use.use}` as 'ai.models.use_tip.chat', { defaultValue: '' })}</InfoTip>
        </h3>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('ai.models.mode')} hint={use.use === 'decide' && cloudModels.length > 0 ? t('ai.models.decide_cloud') : undefined}>
            {(c) => (
              <SelectField {...c} value={use.mode} onValueChange={(value) => mode.mutate({ ...path, body: { mode: value } })} options={[
                { value: 'self_hosted', label: t('ai.models.mode_self_hosted') },
                { value: 'off', label: t('ai.models.mode_off') },
                // Offered once a cloud profile serving the use is enabled (#487, #556), or while set.
                ...(enabledFor(use.use).length > 0 || use.mode === 'self_hosted_then_cloud'
                  ? [{ value: 'self_hosted_then_cloud', label: t('ai.models.mode_cloud') }] : []),
                ...(use.use === 'chat' && (enabledFor('chat').length > 0 || use.mode === 'cloud')
                  ? [{ value: 'cloud', label: t('ai.models.mode_cloud_only') }] : []),
              ]} />
            )}
          </Field>
          {use.mode === 'cloud' ? (
            <Field label={t('ai.models.model')} hint={t('ai.models.cloud_only_hint')}>
              {(c) => <Input {...c} readOnly value={t('ai.models.cloud_only_model')} />}
            </Field>
          ) : null}
          {/* #450: without a model of its own, decide compares labels by meaning with the embed use's model: shown, not chosen. */}
          {use.mode === 'cloud' ? null : use.use === 'decide' && offered.length === 0 && cloudModels.length === 0 ? (
            <Field label={t('ai.models.model')} hint={embedModel ? t('ai.models.decide_by_meaning') : t('ai.models.decide_needs_embed')}>
              {(c) => <Input {...c} readOnly value={embedModel ? t('ai.models.decide_embed_model', { name: embedModel }) : t('ai.models.no_model')} />}
            </Field>
          ) : (
          <Field label={t('ai.models.model')}
            hint={onCloud ? t(use.use === 'decide' ? 'ai.models.decide_cloud_model_hint' : 'ai.models.cloud_model_hint')
              : use.use === 'decide' ? t(use.model ? 'ai.models.decide_by_ai' : 'ai.models.decide_by_meaning')
              : offered.length === 0 && cloudModels.length === 0 ? t('ai.models.none_declared') : undefined}>
              {(c) => (
                <SelectField {...c} value={use.model ?? ''} onValueChange={(value) => model.mutate({ ...path, body: { model: value || null } })}
                  options={[
                    // #450: decide's model is a worker's (#812: or a cloud profile's); without one, the suggestions go by meaning on the search model.
                    { value: '', label: use.use !== 'decide' ? t('ai.models.no_model')
                      : embedModel ? t('ai.models.decide_by_meaning_option', { name: embedModel }) : t('ai.models.decide_by_meaning_none') },
                    ...offered.map((m) => ({ value: m.name, label: t('ai.models.served_by', { name: m.name, count: m.workers }) })),
                    ...cloudModels.map((p) => ({ value: p.model, label: t('ai.models.cloud_model', { name: p.model, provider: provider(p.provider) }) })),
                    // A model no worker declares any more stays visible until the admin picks another.
                    ...(use.model && !offered.some((m) => m.name === use.model) && !cloudModels.some((p) => p.model === use.model)
                      ? [{ value: use.model, label: t('ai.models.gone', { name: use.model }) }] : []),
                  ]} />
              )}
            </Field>
          )}
        </div>
        {use.use === 'decide' ? <DecideCalibration /> : null}
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
