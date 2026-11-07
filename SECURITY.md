# Security policy

Nid holds a household's financial data, so security reports are welcome and taken seriously.

## Reporting a vulnerability

**Do not open a public issue, pull request or discussion.** Report it privately through GitHub:
[Security › Report a vulnerability](https://github.com/Mistribe/Nid/security/advisories/new).

Please include:

- what an attacker can do, and what they need first (an account, a token, network access, a crafted file…);
- the steps to reproduce it, on the demo household or invented data, never on a real instance's data;
- the version (shown in the app's menu, or the image tag) or the commit;
- a suggested fix, if you have one.

Nid is maintained in spare time: we aim to acknowledge a report within a week, keep you informed while the fix is
prepared, publish an advisory with the release that fixes it, and credit you unless you prefer otherwise.

## Supported versions

Only the latest release is supported: every merge to `main` is a release, and fixes land there. Update to the latest
version before reporting, if you can.

## Scope

In scope, for example:

- reading or changing another adult's **private** items, or anything without being signed in;
- getting past sign-in, a session, CSRF protection, or the scopes of a personal access token;
- making the MCP endpoint act beyond its token, or turning content an assistant reads (labels, documents, imported
  data) into instructions it follows;
- an import file, uploaded document or statement file that reads or writes what it should not, or runs code;
- making the server send requests to a host it should not reach (SSRF), or leak secrets, tokens or bank sessions into
  logs, traces, exports or backups;
- a figure that can be falsified without the change showing in the audit log.

Out of scope:

- vulnerabilities in [Pocket ID](https://github.com/pocket-id/pocket-id), Enable Banking, PostgreSQL or other software
  Nid relies on: report them to their maintainers;
- an attacker with root access to the host, or control of the reverse proxy the host provides;
- denial of service by volume;
- findings that need an instance deployed against the advice of [`deploy/README.md`](deploy/README.md), for example
  without TLS.

The threat model, the trust boundaries and the decisions behind them are in [`docs/security/`](docs/security/README.md)
(in French).
