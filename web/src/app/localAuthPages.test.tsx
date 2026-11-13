import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { signedOutResponse } from '@/test/fixtures';
import { server } from '@/test/server';
import { fragmentToken } from './localAuth';
import { LoginPage } from './LoginPage';
import { PasswordLinkPage } from './PasswordLinkPage';
import { safeReturnUrl } from './session';
import { SetupPage } from './SetupPage';

/** Issue 215: the pages of local accounts, before any session (local-accounts.md §14.5). */

const preSession = (overrides: object = {}) => http.get('*/auth/session', () =>
  HttpResponse.json({ ...signedOutResponse, csrf: 'pre-token', methods: ['password'], ...overrides }));

describe('LoginPage', () => {
  it('signs in with the pre-session token and goes back to where the person was going', async () => {
    let sent: { body: unknown; csrf: string | null } | null = null;
    server.use(preSession(), http.post('*/auth/login/password', async ({ request }) => {
      sent = { body: await request.json(), csrf: request.headers.get('X-Nid-Csrf') };
      return HttpResponse.json({ next: 'done', returnUrl: '/dashboard' });
    }));
    const location = { assign: vi.fn() };
    const { container } = render(<LoginPage returnUrl="/dashboard" login={null} location={location} />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Identifiant'), 'alice');
    await user.type(screen.getByLabelText('Mot de passe'), 'a long walk to the lighthouse');
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Se connecter' }));

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('/dashboard'));
    expect(sent).toEqual({ body: { login: 'alice', password: 'a long walk to the lighthouse', returnUrl: '/dashboard' }, csrf: 'pre-token' });
  });

  it('says one thing for any refusal, and never leaves the page', async () => {
    server.use(preSession(), http.post('*/auth/login/password', () =>
      HttpResponse.json({ status: 401, code: 'auth.invalid_credentials', title: 'Wrong' }, { status: 401 })));
    const location = { assign: vi.fn() };
    render(<LoginPage returnUrl={null} login="alice" location={location} />);
    const user = userEvent.setup();

    expect(await screen.findByLabelText('Identifiant')).toHaveValue('alice');
    await user.type(screen.getByLabelText('Mot de passe'), 'not the right one at all');
    await user.click(screen.getByRole('button', { name: 'Se connecter' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Identifiant ou mot de passe incorrect');
    expect(location.assign).not.toHaveBeenCalled();
  });

  it('shows the password while asked to', async () => {
    server.use(preSession());
    render(<LoginPage returnUrl={null} login={null} location={{ assign: vi.fn() }} />);
    const user = userEvent.setup();

    const field = await screen.findByLabelText('Mot de passe');
    expect(field).toHaveAttribute('type', 'password');
    expect(field).toHaveAttribute('autocomplete', 'current-password');
    await user.click(screen.getByRole('button', { name: 'Afficher le mot de passe' }));
    expect(field).toHaveAttribute('type', 'text');
  });

  it('offers the identity provider when both ways are on, and says where the setup is on a fresh instance', async () => {
    server.use(preSession({ methods: ['password', 'oidc'], setupRequired: true }));
    render(<LoginPage returnUrl="/subscriptions" login={null} location={{ assign: vi.fn() }} />);

    expect(await screen.findByRole('link', { name: "Se connecter avec le fournisseur d'identité" }))
      .toHaveAttribute('href', '/auth/login/oidc?returnUrl=%2Fsubscriptions');
    expect(screen.getByRole('status')).toHaveTextContent('docker compose logs nid');
  });

  it('goes straight back when someone is already signed in', async () => {
    server.use(http.get('*/auth/session', () => HttpResponse.json({ ...signedOutResponse, authenticated: true })));
    const location = { assign: vi.fn() };
    render(<LoginPage returnUrl="//evil.example" login={null} location={location} />);

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('/'));
  });
});

describe('SetupPage', () => {
  it('creates the first administrator with the token of the log, then leads to sign-in', async () => {
    let body: unknown = null;
    server.use(preSession({ setupRequired: true }), http.post('*/auth/setup', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({ login: 'alice' }, { status: 201 });
    }));
    const location = { assign: vi.fn() };
    const { container } = render(<SetupPage token="nid_setup_abc" location={location} />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Votre prénom'), 'Alice');
    await user.type(screen.getByLabelText('Identifiant'), 'Alice');
    await user.type(screen.getByLabelText('Mot de passe'), 'a long walk to the lighthouse');
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Créer le compte administrateur' }));

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('/login?login=alice'));
    expect(body).toEqual({ token: 'nid_setup_abc', name: 'Alice', login: 'Alice', password: 'a long walk to the lighthouse' });
  });

  it('says where the address is without a token, and that Nid is set up once it is', async () => {
    server.use(preSession({ setupRequired: true }));
    const first = render(<SetupPage token={null} location={{ assign: vi.fn() }} />);
    expect(await screen.findByText(/journal du conteneur/)).toBeInTheDocument();
    first.unmount();

    server.use(preSession({ setupRequired: false }));
    render(<SetupPage token="nid_setup_abc" location={{ assign: vi.fn() }} />);
    expect(await screen.findByText(/déjà en place/)).toBeInTheDocument();
  });
});

describe('PasswordLinkPage', () => {
  it('lets a new adult choose an identifier and a password', async () => {
    let completed: unknown = null;
    server.use(
      preSession(),
      http.post('*/auth/link/inspect', () => HttpResponse.json({ purpose: 'enroll', name: 'Bob', login: 'bob@example.test' })),
      http.post('*/auth/link/complete', async ({ request }) => {
        completed = await request.json();
        return HttpResponse.json({ login: 'bob' });
      }),
    );
    const location = { assign: vi.fn() };
    const { container } = render(<PasswordLinkPage token="nid_link_abc" location={location} />);
    const user = userEvent.setup();

    const login = await screen.findByLabelText('Identifiant');
    expect(screen.getByRole('heading', { name: 'Bienvenue dans Nid' })).toBeInTheDocument();
    expect(login).toHaveValue('bob@example.test');
    await user.clear(login);
    await user.type(login, 'bob');
    await user.type(screen.getByLabelText('Nouveau mot de passe'), 'seven green parrots at noon');
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Créer mon accès' }));

    await waitFor(() => expect(location.assign).toHaveBeenCalledWith('/login?login=bob'));
    expect(completed).toEqual({ token: 'nid_link_abc', login: 'bob', password: 'seven green parrots at noon' });
  });

  it('shows the refusal of a too common password in French', async () => {
    server.use(
      preSession(),
      http.post('*/auth/link/inspect', () => HttpResponse.json({ purpose: 'reset', name: 'Bob', login: 'bob' })),
      http.post('*/auth/link/complete', () => HttpResponse.json({ status: 400, code: 'auth.password_too_common', title: 'Too common' }, { status: 400 })),
    );
    render(<PasswordLinkPage token="nid_link_abc" location={{ assign: vi.fn() }} />);
    const user = userEvent.setup();

    expect(await screen.findByRole('heading', { name: 'Choisir un nouveau mot de passe' })).toBeInTheDocument();
    expect(screen.getByLabelText('Identifiant')).toHaveAttribute('readonly');
    await user.type(screen.getByLabelText('Nouveau mot de passe'), 'password1234');
    await user.click(screen.getByRole('button', { name: 'Enregistrer le mot de passe' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('trop courant');
  });

  it('says a used or expired link does not work any more', async () => {
    server.use(preSession(), http.post('*/auth/link/inspect', () => HttpResponse.json({ status: 404, code: 'auth.link_unusable' }, { status: 404 })));
    render(<PasswordLinkPage token="nid_link_old" location={{ assign: vi.fn() }} />);

    expect(await screen.findByRole('alert')).toHaveTextContent('ne fonctionne plus');
  });
});

describe('helpers', () => {
  it('reads a token only from its own kind of fragment', () => {
    expect(fragmentToken('#nid_link_abc', 'nid_link_')).toBe('nid_link_abc');
    expect(fragmentToken('#nid_setup_abc', 'nid_link_')).toBeNull();
    expect(fragmentToken('', 'nid_link_')).toBeNull();
    expect(fragmentToken('#nid_link_', 'nid_link_')).toBeNull();
  });

  it('keeps only a relative return path', () => {
    expect(safeReturnUrl('/dashboard?scope=me')).toBe('/dashboard?scope=me');
    expect(safeReturnUrl('//evil.example')).toBe('/');
    expect(safeReturnUrl('https://evil.example')).toBe('/');
    expect(safeReturnUrl('/\\evil.example')).toBe('/');
    expect(safeReturnUrl(null)).toBe('/');
  });
});
