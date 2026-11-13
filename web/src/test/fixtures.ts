import type { components } from '@/api/schema';
import type { Session } from '@/app/session';
import { allGuidesSeen } from '@/features/guides/guideData';

type SessionResponse = components['schemas']['SessionResponse'];

export const aliceSession: Session = {
  person: { id: '01926b9e-0000-7000-8000-00000000000a', name: 'Alice Dev', kind: 'adult' },
  csrf: 'csrf-alice',
  sessionEpoch: 0,
  preferences: { theme: 'sobre', mode: 'system', chartPatterns: false, guidesSeen: allGuidesSeen() },
  demo: false,
};

export const aliceSessionResponse: SessionResponse = { authenticated: true, ...aliceSession };

export const signedOutResponse: SessionResponse = {
  authenticated: false, person: null, csrf: null, sessionEpoch: null, preferences: null, demo: false,
};

export const allCapabilities: components['schemas']['CapabilitiesView'] = { modules: ['core', 'subscriptions', 'import', 'investments', 'income', 'realestate', 'goals', 'documents', 'banking', 'childcare'], admin: false };

export const adminCapabilities: components['schemas']['CapabilitiesView'] = { ...allCapabilities, admin: true };
