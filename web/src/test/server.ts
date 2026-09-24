import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { aliceSessionResponse, allCapabilities } from './fixtures';

export const handlers = [
  http.get('*/auth/session', () => HttpResponse.json(aliceSessionResponse)),
  http.get('*/api/v1/platform/capabilities', () => HttpResponse.json(allCapabilities)),
  http.get('*/api/v1/platform/imports', () => HttpResponse.json([])),
];

export const server = setupServer(...handlers);
