import type { components } from '@/api/schema';
import type { Session } from '@/app/session';
import { allGuidesSeen } from '@/features/guides/guides';

type SessionResponse = components['schemas']['SessionResponse'];

export const aliceSession: Session = {
  person: { id: '01926b9e-0000-7000-8000-00000000000a', name: 'Alice Dev', kind: 'adult' },
  csrf: 'csrf-alice',
  sessionEpoch: 0,
  preferences: { theme: 'neutre', mode: 'system', chartPatterns: false, guidesSeen: allGuidesSeen() },
};

export const aliceSessionResponse: SessionResponse = { authenticated: true, ...aliceSession };

export const signedOutResponse: SessionResponse = {
  authenticated: false, person: null, csrf: null, sessionEpoch: null, preferences: null,
};

export const allCapabilities: components['schemas']['CapabilitiesView'] = { modules: ['core', 'subscriptions', 'import', 'investments', 'income', 'realestate'], admin: false };

export const adminCapabilities: components['schemas']['CapabilitiesView'] = { ...allCapabilities, admin: true };
