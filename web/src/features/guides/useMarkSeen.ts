import { useRef } from 'react';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { GUIDES, isUnseen, type Guide } from './guides';

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
    // An API older than the web has no guidesSeen (the two images deploy separately).
    const seen = latest.current.guidesSeen ?? {};
    if (!isUnseen(guide, seen)) return; // a replay from « ? » saves nothing
    // A retired guide's id is dropped, or the map would creep up to the server's 32-entry cap.
    const kept = Object.entries(seen).filter(([id]) => GUIDES.some((g) => g.id === id));
    const guidesSeen = { ...Object.fromEntries(kept), [guide.id]: guide.version };
    latest.current = { ...latest.current, guidesSeen };
    setPreferences(latest.current);
    save.mutate({ body: { guidesSeen } });
  };
}
