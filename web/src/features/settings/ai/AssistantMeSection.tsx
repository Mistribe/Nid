import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, ASSISTANT_ME, ASSISTANT_ME_ENABLED, ASSISTANT_ME_OFFSITE } from '@/api/ai';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { Section } from '@/components/page/Section';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Skeleton } from '@/components/ui/skeleton';
import '@/locales/ai';

const PREFERENCES = ['ask', 'allow', 'never'] as const;

/**
 * « L'assistant, pour vous » (#533, docs/ai/README.md §6.2, §6.3): each adult turns the assistant on for themselves, which is
 * their consent for their own data; towards the cloud, the confirmation says what leaves and every adult is told. Their
 * preference for their data off the premises: `never` keeps what they own at home, shared items included.
 */
export function AssistantMeSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const me = $api.useQuery('get', ASSISTANT_ME);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['get', ASSISTANT_ME] });
  const enable = $api.useMutation('put', ASSISTANT_ME_ENABLED, {
    onSuccess: (view) => { toast.success(t(view.enabled ? 'ai.me.enabled' : 'ai.me.disabled')); refresh(); },
  });
  const offsite = $api.useMutation('put', ASSISTANT_ME_OFFSITE, { onSuccess: () => { toast.success(t('ai.me.offsite_saved')); refresh(); } });
  const [confirming, setConfirming] = useState(false);
  const wrapper = useRef<HTMLDivElement>(null);
  const error = me.error ?? enable.error ?? offsite.error;

  const toggle = (on: boolean) => {
    if (on && me.data?.cloudPossible) setConfirming(true);
    else enable.mutate({ body: { enabled: on } });
  };

  return (
    <Section title={t('ai.me.title')}>
      {error ? <ProblemMessage problem={aiProblem(t, error)} /> : null}
      {me.data ? (
        <div ref={wrapper} className="space-y-4">
          <SwitchField label={t('ai.me.enable')} hint={t('ai.me.enable_hint')} checked={me.data.enabled} onCheckedChange={toggle} />
          <Field label={t('ai.me.offsite')} hint={t('ai.me.offsite_hint')} tip={<InfoTip label={t('ai.me.offsite')}>{t('ai.me.offsite_tip')}</InfoTip>}>
            {(c) => (
              <SelectField {...c} value={me.data.offsiteDefault} disabled={offsite.isPending}
                onValueChange={(preference) => offsite.mutate({ body: { preference } })}
                options={PREFERENCES.map((value) => ({ value, label: t(`ai.me.offsite_${value}`) }))} />
            )}
          </Field>
        </div>
      ) : me.error ? null : <Skeleton aria-busy="true" className="h-12"><span className="sr-only">{t('ai.loading')}</span></Skeleton>}
      {confirming ? (
        <ConfirmFromMenu label={t('ai.me.enable')} confirmation={t('ai.me.enable_cloud_confirm')} pending={enable.isPending}
          returnFocus={{ current: wrapper.current?.querySelector<HTMLElement>('[role="switch"]') ?? null }}
          onClose={() => setConfirming(false)} onConfirm={() => enable.mutate({ body: { enabled: true } })} />
      ) : null}
    </Section>
  );
}
