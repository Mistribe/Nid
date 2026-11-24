import { useQueryClient } from '@tanstack/react-query';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { AI_SETTINGS, aiProblem, ALLOWED_KINDS, CLOUD_DOCUMENT_KINDS } from '@/api/ai';
import { InfoTip } from '@/components/InfoTip';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import '@/locales/ai';

/**
 * « Documents envoyés au cloud » (#556, `allowed_kinds`, docs/ai/README.md §6.1): the document types a cloud provider may read
 * to embed them, none by default. Each adult concerned must still allow it; a payslip is never offered. Each tick saves.
 */
export function AllowedKindsSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const legendId = useId();
  const settings = $api.useQuery('get', AI_SETTINGS);
  const save = $api.useMutation('put', ALLOWED_KINDS, {
    onSuccess: () => { toast.success(t('ai.kinds.saved')); void queryClient.invalidateQueries({ queryKey: ['get', AI_SETTINGS] }); },
  });
  const allowed = settings.data?.allowedKinds ?? [];
  const toggle = (kind: string, on: boolean) =>
    save.mutate({ body: { kinds: on ? [...allowed, kind] : allowed.filter((k) => k !== kind) } });

  return (
    <Section title={t('ai.kinds.title')} tip={<InfoTip label={t('ai.kinds.title')}>{t('ai.kinds.tip')}</InfoTip>}>
      {settings.error ?? save.error ? <ProblemMessage problem={aiProblem(t, settings.error ?? save.error)} /> : null}
      {settings.data ? (
        <div className="space-y-2">
          <p id={legendId} className="text-sm font-medium">{t('ai.kinds.legend')}</p>
          <div role="group" aria-labelledby={legendId} className="flex flex-wrap gap-x-4 gap-y-2">
            {CLOUD_DOCUMENT_KINDS.map((kind) => (
              <div key={kind} className="flex items-center gap-2">
                <Checkbox id={`${legendId}-${kind}`} checked={allowed.includes(kind)} disabled={save.isPending}
                  onCheckedChange={(checked) => toggle(kind, checked === true)} />
                <Label htmlFor={`${legendId}-${kind}`}>{t(`ai.kinds.kind.${kind}`)}</Label>
              </div>
            ))}
          </div>
          {allowed.length === 0 ? <p className="text-sm text-muted-foreground">{t('ai.kinds.body')}</p> : null}
        </div>
      ) : null}
    </Section>
  );
}
