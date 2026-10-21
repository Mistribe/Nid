import { Link } from '@tanstack/react-router';
import { useCallback, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ESTATE_ROUTINE, type EstateRecap } from './api';
import { estateDraft } from './draft';
import { EstateForm, freshEstate, startEstate, type EstateValues } from './EstateForm';
import { EstateRecapPanel } from './EstateRecap';
import '@/locales/routine';

/** Spec 3E2 §4: the routine's « Biens » — its own reading, save, recap and « Annuler », beside the envelopes'. */
export function EstateSection({ on }: { on: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const headingId = useId();
  const answer = $api.useQuery('get', ESTATE_ROUTINE, { params: { query: { on } } });
  const [saved, setSaved] = useState<{ recap: EstateRecap; values: EstateValues } | null>(null);
  const [restored, setRestored] = useState<EstateValues | null>(null);
  const [undone, setUndone] = useState(false);
  // After « Annuler » the button is gone: focus lands on the status.
  const focusStatus = useCallback((el: HTMLElement | null) => el?.focus(), []);
  const view = answer.data;
  const quiet = view !== undefined && !view.estimates.some((e) => e.due) && view.costs.length === 0 && view.rents.length === 0;

  return (
    <section aria-labelledby={headingId} data-guide="routine.estate" className="space-y-4 border-t pt-4">
      <h2 id={headingId} className="text-xl font-semibold">{t('routine_estate.title')}</h2>
      {answer.error && !view ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {view && quiet && !saved ? (
        <p>
          {t('routine_estate.empty')}
          {view.estimates.length === 0 ? <> <Link to="/properties/new" className="underline">{t('routine_estate.add_property')}</Link></> : null}
        </p>
      ) : null}
      {view && view.estimates.length > 0 && !saved ? (
        // A new proposal from the server (a refetch after a save elsewhere) starts the form again from it and the draft.
        <EstateForm key={`${restored ? 'restored' : 'fresh'}:${JSON.stringify(freshEstate(view))}`} view={view}
          initial={startEstate(view, restored ?? estateDraft.load(on))}
          onSaved={(recap, values) => { estateDraft.clear(on); setUndone(false); setSaved({ recap, values }); }} />
      ) : null}
      {saved ? <EstateRecapPanel recap={saved.recap} onUndone={() => { setRestored(saved.values); setSaved(null); setUndone(true); }} /> : null}
      {undone && !saved ? <p role="status" tabIndex={-1} ref={focusStatus}>{t('routine_estate.undone')}</p> : null}
    </section>
  );
}
