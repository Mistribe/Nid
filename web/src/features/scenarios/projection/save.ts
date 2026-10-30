import { useQueryClient } from '@tanstack/react-query';
import { useApi } from '@/api/ApiProvider';
import { PROJECTION_SCENARIO, SCENARIOS, type ProjectionScenario, type ProjectionScenarioBody } from '../api';

/** The whole scenario as `PUT` replaces it: the document it was shown (the server keeps the events the reader cannot see). */
export const bodyOf = (s: ProjectionScenario, over: Partial<ProjectionScenarioBody> = {}): ProjectionScenarioBody => ({
  name: s.name, visibility: s.visibility, scope: s.scope, document: s.result.document, ...over,
});

/** A `PUT` of the scenario that hands the server's answer to the page's read, and the list its new name or gap. */
export function useSave(scenario: ProjectionScenario, onSaved: () => void, onError: (error: unknown) => void) {
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const init = { params: { path: { id: scenario.id } } };
  return $api.useMutation('put', PROJECTION_SCENARIO, {
    onSuccess: (view) => {
      queryClient.setQueryData($api.queryOptions('get', PROJECTION_SCENARIO, init).queryKey, view);
      void queryClient.invalidateQueries({ queryKey: ['get', SCENARIOS] });
      onSaved();
    },
    onError,
  });
}
