import type { ProposalView } from '@/api/proposals';

let n = 0;
/** A pending valuation proposal; override any field. */
export function proposal(over: Partial<ProposalView> = {}): ProposalView {
  n += 1;
  return {
    id: `00000000-0000-7000-8000-${String(n).padStart(12, '0')}`,
    kind: 'investments.record_valuation',
    targetItemId: '00000000-0000-7000-8000-0000000000e1',
    targetName: 'PEA',
    status: 'pending',
    payload: { asOf: '2025-09-30', amount: { amount: '23500.00', currency: 'EUR' } },
    editedPayload: null,
    payloadVersion: 1,
    warnings: [],
    subjectKey: '2025-09-30',
    sourceLabel: 'Relevé PEA septembre',
    createdVia: 'mcp',
    createdAt: '2026-10-16T08:00:00+00:00',
    createdByPersonId: 'a',
    supersededById: null,
    decidedAt: null,
    decidedByPersonId: null,
    decisionNote: null,
    appliedRefs: [],
    version: 1,
    ...over,
  };
}
