import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { aliceSessionResponse, allCapabilities } from './fixtures';

export const handlers = [
  http.get('*/auth/session', () => HttpResponse.json(aliceSessionResponse)),
  http.get('*/api/v1/platform/capabilities', () => HttpResponse.json(allCapabilities)),
];

export const server = setupServer(...handlers);
