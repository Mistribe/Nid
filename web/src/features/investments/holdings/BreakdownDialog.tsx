import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { messageFor } from '@/components/form/fieldErrors';
import { todayIso } from '@/lib/today';
import { HOLDING_BREAKDOWN, invalidateInvestments, type EnvelopeDetail, type Holding, type HoldingBreakdown } from '@/api/investments';
import { REGIONS, regionText, SECTORS, sectorText } from './breakdown';
import '@/locales/investments';
import '@/locales/forms';

type Part = { code: string; pct: number };
type Kind = 'regions' | 'sectors';
const ISO = /^\d{4}-\d{2}-\d{2}$/;

/** A percentage typed on a French keyboard; blank means « not given ». The body takes numbers. */
const number = (s: string) => Number(s.trim().replace(',', '.'));
const typed = (values: Record<string, string>, codes: readonly string[]): Part[] =>
  codes.filter((c) => (values[c] ?? '').trim() !== '').map((code) => ({ code, pct: number(values[code]!) }));
const texts = (parts?: readonly Part[] | null) => Object.fromEntries((parts ?? []).map((p) => [p.code, String(p.pct)]));

/** A form aid, not an amount: the sum of the percentages typed so far. The server decides whether it is 100. */
const total = (values: Record<string, string>) =>
  new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 2 }).format(Math.round(Object.values(values).reduce((sum, v) => sum + (v.trim() === '' ? 0 : number(v) || 0), 0) * 100) / 100);

/** Holdings exposure §5: a fund's regions and sectors as its factsheet gives them; an equity is one region and one sector at 100 %. */
export function BreakdownDialog({ envelope, holding, current, onClose, onDone }: {
  envelope: EnvelopeDetail; holding: Holding; current?: HoldingBreakdown; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const equity = holding.kind === 'equity';
  const oneLine = (b?: HoldingBreakdown) => !b || (b.regions.length === 1 && b.regions[0]!.pct === 100 && (b.sectors ?? []).length <= 1);
  const [mode, setMode] = useState<'single' | 'detail'>(equity && oneLine(current) ? 'single' : 'detail');
  const [asOf, setAsOf] = useState('');
  const [regions, setRegions] = useState(texts(current?.regions));
  const [sectors, setSectors] = useState(texts(current?.sectors));
  const [region, setRegion] = useState(equity && oneLine(current) ? current?.regions[0]?.code ?? '' : '');
  const [sector, setSector] = useState(equity && oneLine(current) ? current?.sectors?.[0]?.code ?? '' : '');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [problem, setProblem] = useState<Presentation | null>(null);
  const save = $api.useMutation('put', HOLDING_BREAKDOWN, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const single = equity && mode === 'single';

  return (
    <FormDialog title={t('breakdown.dialog_title', { name: holding.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => {
        const submit = () => {
          setProblem(null);
          const sent: Record<Kind, Part[]> = single
            ? { regions: region ? [{ code: region, pct: 100 }] : [], sectors: sector ? [{ code: sector, pct: 100 }] : [] }
            : { regions: typed(regions, REGIONS), sectors: typed(sectors, SECTORS) };
          const missing: Record<string, string> = {};
          if (!ISO.test(asOf)) missing.asOf = t('edit.required');
          if (single && !region) missing.regions = t('edit.required');
          if (single && !sector) missing.sectors = t('edit.required');
          setErrors(missing);
          if (Object.keys(missing).length > 0) return;
          save.mutate({
            params: { path: { id: envelope.id, holdingId: holding.id } },
            // No sector typed: the key is left out, an empty list would be refused as not 100 %.
            body: { asOf, regions: sent.regions, ...(sent.sectors.length > 0 ? { sectors: sent.sectors } : {}) },
          }, {
            onSuccess: () => done(t('breakdown.saved')),
            onError: (error) => {
              const problem = toProblem(error);
              const placed: Record<string, string> = {};
              for (const f of problem.errors ?? []) {
                const [, kind, index] = /^(regions|sectors)(?:\[(\d+)\])?/.exec(f.path) ?? [];
                const message = f.code === 'investments.breakdown_not_100' ? t('breakdown.not_100') : messageFor(t, f);
                if (f.path === 'asOf') placed.asOf = message;
                else if (kind) placed[index === undefined ? kind : `${kind}.${sent[kind as Kind][Number(index)]?.code}`] = message;
              }
              setErrors(placed);
              if (Object.keys(placed).length === 0) setProblem(present(problem));
            },
          });
        };
        const group = (kind: Kind, codes: readonly string[], values: Record<string, string>, set: (v: Record<string, string>) => void, hint?: string) => (
          <fieldset className="space-y-2">
            <legend className="text-sm font-medium">{t(`breakdown.${kind}`)}</legend>
            {hint ? <p className="text-sm text-muted-foreground">{hint}</p> : null}
            <div className="grid gap-3 sm:grid-cols-2">
              {codes.map((c) => (
                <Field key={c} label={(kind === 'regions' ? regionText : sectorText)(t, c)} error={errors[`${kind}.${c}`]}>
                  {(ctl) => <Input inputMode="decimal" {...ctl} value={values[c] ?? ''} onChange={(ev) => set({ ...values, [c]: ev.target.value })} />}
                </Field>
              ))}
            </div>
            <p className="text-sm text-muted-foreground tabular-nums">{t('breakdown.total', { value: total(values) })}</p>
            {errors[kind] ? <p role="alert" className="text-sm text-destructive">{errors[kind]}</p> : null}
          </fieldset>
        );
        return (
          <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(ev) => { ev.preventDefault(); ev.stopPropagation(); submit(); }}>
            {problem ? <ProblemMessage problem={problem} /> : null}
            <Field label={t('breakdown.as_of_field')} error={errors.asOf}>
              {(ctl) => <DateField {...ctl} value={asOf} onChange={setAsOf} max={todayIso()} />}
            </Field>
            {equity ? (
              <Segmented value={mode} aria-label={t('breakdown.mode')} onValueChange={(v) => setMode(v === 'detail' ? 'detail' : 'single')}
                options={[{ value: 'single', label: t('breakdown.mode_single') }, { value: 'detail', label: t('breakdown.mode_detail') }]} />
            ) : null}
            {single ? (
              <>
                <Field label={t('breakdown.region')} error={errors.regions}>
                  {(ctl) => <SelectField {...ctl} value={region} onValueChange={setRegion} options={REGIONS.map((c) => ({ value: c, label: regionText(t, c) }))} />}
                </Field>
                <Field label={t('breakdown.sector')} error={errors.sectors}>
                  {(ctl) => <SelectField {...ctl} value={sector} onValueChange={setSector} options={SECTORS.map((c) => ({ value: c, label: sectorText(t, c) }))} />}
                </Field>
              </>
            ) : (
              <>
                {group('regions', REGIONS, regions, setRegions)}
                {group('sectors', SECTORS, sectors, setSectors, t('breakdown.sectors_hint'))}
              </>
            )}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => done()}>{t('envelope.cancel')}</Button>
              <Button type="submit" disabled={save.isPending}>{t('envelope.save')}</Button>
            </div>
          </form>
        );
      }}
    </FormDialog>
  );
}
