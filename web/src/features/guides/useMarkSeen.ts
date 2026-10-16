import { useRef } from 'react';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { isUnseen, type Guide } from './guides';

/**
 * Spec §4.2: a guide closed is seen at once locally, then saved. A failed save says nothing: the guide plays again at the
 * next session, better than an error about a tutorial.
 */
export function useMarkSeen(): (guide: Guide) => void {
  const { $api } = useApi();
  const { session, setPreferences } = useSession();
  // Two marks before a re-render (leaving a section, then closing the next guide) must not lose the first.
  const latest = useRef(session.preferences);
  latest.current = session.preferences;
  // Same scope as the appearance page: preference saves run one after another, in order.
  const save = $api.useMutation('patch', '/api/v1/core/me/preferences', { scope: { id: 'preferences' } });

  return (guide) => {
    if (!isUnseen(guide, latest.current.guidesSeen)) return; // a replay from « ? » saves nothing
    const guidesSeen = { ...latest.current.guidesSeen, [guide.id]: guide.version };
    latest.current = { ...latest.current, guidesSeen };
    setPreferences(latest.current);
    save.mutate({ body: { guidesSeen } });
  };
}
