import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DASHBOARD } from '../../dashboard/api';
import { invalidateGoals } from '../../goals/api';
import { TAX, TAX_2044, TAX_PROFILE, type TaxEstimate } from '../api';
import { countChanges, draftErrors, initialDraft, profileBody, type DraftErrors, type ProfileDraft } from './draft';
import '@/locales/tax';

export type Draft = {
  /** The year and scope the draft is for: what keys the tab's local state (folds). */ key: string;
  value: ProfileDraft; /** The profile as last saved, the count's baseline. */ saved: ProfileDraft;
  set: (next: Partial<ProfileDraft>) => void; errors: DraftErrors; changes: number;
  /** Sends the draft; `false` when a field holds an error, and nothing is sent. */ submit: () => boolean;
  pending: boolean; problem: unknown; announcement: string;
};

type Held = { key: string; initial: ProfileDraft; value: ProfileDraft };
const fresh = (estimate: TaxEstimate, key: string): Held => {
  const initial = initialDraft(estimate);
  return { key, initial, value: initial };
};

/**
 * The profile tab's draft (spec §3), held by the page so it outlives a tab switch; another year or scope starts afresh from
 * that profile. `undefined` while the estimate loads.
 */
export function useProfileDraft(estimate: TaxEstimate | undefined, scope: 'me' | 'household'): Draft | undefined {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const save = $api.useMutation('put', TAX_PROFILE);
  const [announcement, setAnnouncement] = useState('');
  const key = estimate ? `${estimate.year}-${scope}` : '';
  const [held, setHeld] = useState<Held | null>(() => (estimate ? fresh(estimate, key) : null));
  let current = held;
  // Storing what the previous render saw: a new year or scope resets the draft during render, not in an effect.
  if (estimate && held?.key !== key) {
    current = fresh(estimate, key);
    setHeld(current);
    if (announcement) setAnnouncement('');
  }
  // Another year or scope drops the previous one's refused save; a save still in flight no longer speaks for this one.
  const keyRef = useRef(key);
  useEffect(() => {
    keyRef.current = key;
    save.reset();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `save` changes identity every render; only the key matters.
  }, [key]);
  if (!estimate || !current) return undefined;

  const value = current.value;
  const errors = draftErrors(value);
  const submit = () => {
    setAnnouncement('');
    if (Object.keys(errors).length) return false;
    save.mutate({ params: { path: { year: estimate.year }, query: { scope } }, body: profileBody(value) }, {
      onSuccess: () => {
        // Every read the profile feeds: the estimate, the 2044 (regime, carried deficit), the dashboard's and the goals' tax commitment.
        for (const path of [TAX, TAX_2044, DASHBOARD]) void queryClient.invalidateQueries({ queryKey: ['get', path] });
        void invalidateGoals(queryClient);
        // Saved for a year or scope no longer shown: its reads are stale, but this draft and its announcement are another's.
        if (keyRef.current !== key) return;
        // What was sent is the saved profile now: nothing left unsaved.
        setHeld((h) => (h && h.key === key ? { ...h, initial: value } : h));
        setAnnouncement(t('tax.profile.saved'));
      },
    });
    return true;
  };
  return {
    key, value, saved: current.initial, errors, changes: countChanges(value, current.initial), submit,
    pending: save.isPending, problem: save.error, announcement,
    set: (next) => setHeld((h) => (h ? { ...h, value: { ...h.value, ...next } } : h)),
  };
}
