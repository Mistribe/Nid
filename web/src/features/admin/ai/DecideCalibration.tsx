import { useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, aiReason } from '@/api/ai';
import { presentBankError, SUGGESTION_CALIBRATIONS, SUGGESTION_SAMPLE, type CalibrationFigures } from '@/api/banking';
import { Folded } from '@/components/form/Folded';
import { InfoTip } from '@/components/InfoTip';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { formatInstant, share } from '@/lib/format';
import '@/locales/ai';

/** While a calibration waits or runs, its state is read again this often. */
const POLL_MS = 3000;
const busy = (status: string | undefined) => status === 'queued' || status === 'running';

/**
 * #450, under « Suggestions de catégories » in Modèles: what suggests (a worker's `decide` model, or nearest neighbours on the
 * search model), its last calibration on the invented evaluation set and what it measured, in plain words; « Calibrer » queues one,
 * read again until it ends. Without a profile, nothing is suggested that way. With a worker's model, what it is sent, masked, folded.
 */
export function DecideCalibration() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const view = $api.useQuery('get', SUGGESTION_CALIBRATIONS, undefined, {
    refetchInterval: (query) => (busy(query.state.data?.latest?.status) ? POLL_MS : false),
  });
  const calibrate = $api.useMutation('post', SUGGESTION_CALIBRATIONS, {
    onSuccess: () => {
      toast.success(t('ai.calibration.queued'));
      void queryClient.invalidateQueries({ queryKey: ['get', SUGGESTION_CALIBRATIONS] });
    },
  });
  const [sampling, setSampling] = useState(false);
  const data = view.data;
  if (view.error) return <ProblemMessage problem={aiProblem(t, view.error)} />;
  if (!data) return null;

  const byAi = data.implementation !== 'nearest-neighbours';
  const latest = data.latest;
  const profile = data.profile;
  // A gateway refusal in its words; anything else (an exception mid-run) in one sentence.
  const failure = latest?.code?.startsWith('ai.') ? aiReason(t, latest.code) : t('ai.calibration.failed');
  const state = !latest ? null
    : latest.status === 'done' ? t('ai.calibration.status_done', { when: formatInstant(latest.finishedAt ?? latest.queuedAt) })
    : latest.status === 'failed' ? t('ai.calibration.status_failed', { reason: failure })
    : t(latest.status === 'running' ? 'ai.calibration.status_running' : 'ai.calibration.status_queued');

  return (
    <div className="space-y-3 rounded-md border px-3 py-3">
      <h4 className="flex items-center gap-1.5 text-sm font-medium">
        {t('ai.calibration.title')}
        <InfoTip label={t('ai.calibration.title')}>{t('ai.calibration.tip')}</InfoTip>
      </h4>
      <p className="text-sm">
        {data.model ? t(byAi ? 'ai.calibration.by_ai' : 'ai.calibration.by_meaning', { model: data.model }) : null}
        {data.unavailable ? <span className="block text-muted-foreground">{aiReason(t, data.unavailable)}</span> : null}
      </p>
      {state ? <p role="status" className="text-sm text-muted-foreground">{state}</p> : null}
      {calibrate.error ? <ProblemMessage problem={aiProblem(t, calibrate.error)} /> : null}
      {!profile ? <p className="text-sm font-medium">{t(byAi ? 'ai.calibration.none_ai' : 'ai.calibration.none_meaning')}</p>
        : byAi ? (
          <Mode title={t('ai.calibration.guess')} figures={profile.guess} cases={profile.cases} threshold={profile.scoreless ? null : profile.threshold}
            thresholdKey="ai.calibration.threshold_ai" badge={profile.scoreless ? (
              <span className="inline-flex items-center gap-1">
                <Badge variant="outline">{t('ai.calibration.scoreless')}</Badge>
                <InfoTip label={t('ai.calibration.scoreless')}>{t('ai.calibration.scoreless_tip')}</InfoTip>
              </span>
            ) : null} />
        ) : (
          <>
            <Mode title={t('ai.calibration.with_history')} figures={profile.withHistory} cases={profile.cases} threshold={profile.threshold}
              thresholdKey="ai.calibration.threshold_meaning" />
            <Mode title={t('ai.calibration.cold')} figures={profile.cold} cases={profile.cases} threshold={profile.coldThreshold}
              thresholdKey="ai.calibration.threshold_meaning" />
          </>
        )}
      <Button type="button" variant="outline" size="sm" disabled={!!data.unavailable || busy(latest?.status) || calibrate.isPending}
        onClick={() => calibrate.mutate({})}>
        {t('ai.calibration.calibrate')}
      </Button>
      {byAi ? (
        <Folded label={t('ai.calibration.sample')} hasError={false} open={sampling} onOpenChange={setSampling}>
          {sampling ? <Sample /> : null}
        </Folded>
      ) : null}
    </div>
  );
}

/** One way of suggesting and what the calibration measured at the threshold it kept; none kept: not offered. */
function Mode({ title, figures, cases, threshold, thresholdKey, badge }: {
  title: string; figures?: CalibrationFigures | null; cases: number; threshold?: number | null;
  thresholdKey: 'ai.calibration.threshold_ai' | 'ai.calibration.threshold_meaning'; badge?: ReactNode;
}) {
  const { t } = useTranslation();
  return (
    <div className="space-y-1 text-sm">
      <p className="flex flex-wrap items-center gap-1.5 font-medium">{title}{badge}</p>
      {!figures ? <p className="text-muted-foreground">{t('ai.calibration.not_offered')}</p> : (
        <ul className="space-y-0.5 text-muted-foreground">
          <li>{t('ai.calibration.figures', { cases, correct: figures.correct, wrong: figures.wrong, dontKnow: figures.dontKnow })}</li>
          <li className="flex items-center gap-1.5">
            {t('ai.calibration.wrong_rate', { low: share(figures.wrongLow), high: share(figures.wrongHigh) })}
            <InfoTip label={t('ai.calibration.wrong_rate_label')}>{t('ai.calibration.wrong_rate_tip')}</InfoTip>
          </li>
          <li className="flex items-center gap-1.5">
            {t('ai.calibration.coverage', { coverage: share(figures.coverage) })}
            <InfoTip label={t('ai.calibration.coverage_label')}>{t('ai.calibration.coverage_tip')}</InfoTip>
          </li>
          {threshold != null ? <li>{t(thresholdKey, { threshold: share(threshold) })}</li> : null}
        </ul>
      )}
    </div>
  );
}

/** Up to three of the household's own lines as the guess job would send them, masked; read once unfolded. */
function Sample() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const sample = $api.useQuery('get', SUGGESTION_SAMPLE);
  if (sample.error) return <ProblemMessage problem={presentBankError(t, sample.error)} />;
  if (!sample.data) return null;
  return (
    <div className="space-y-2">
      <p className="text-sm text-muted-foreground">{t('ai.calibration.sample_hint')}</p>
      {sample.data.texts.length === 0 ? <p className="text-sm">{t('ai.calibration.sample_empty')}</p> : (
        <ul aria-label={t('ai.calibration.sample')} className="space-y-2">
          {sample.data.texts.map((text) => (
            <li key={text}><pre className="rounded-md bg-muted px-3 py-2 text-xs break-all whitespace-pre-wrap">{text}</pre></li>
          ))}
        </ul>
      )}
    </div>
  );
}
