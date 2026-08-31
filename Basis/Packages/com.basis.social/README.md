# Basis Social Client

Optional, removable Basis Social integration for the Basis client. The package owns
its API transport, authentication session boundary, bootstrap, Basis-native UI, and
tests. It registers itself at runtime and does not require scene objects or changes to
the main client packages.

## Assembly boundaries

- `Basis.Social` contains REST contracts, token storage abstractions, and the API client.
- `Basis.Social.UI` contains the `BasisMenuActionProvider` implementation and depends on
  Basis UI. Removing this package removes the Social menu entry with it.
- `Basis.Social.Tests` contains EditMode contract tests.

The initial credential store is intentionally memory-only. Access and refresh tokens
must not be written to `PlayerPrefs`, logs, serialized scenes, world metadata, or BEE
content. A later persistent store must use the platform credential vault.

## Development endpoint

The local default is `http://127.0.0.1:8080`. Override it without modifying the client:

- environment: `BASIS_SOCIAL_BASE_URL=https://social.example`
- command line: `--basis-social-url=https://social.example`

The Social panel also accepts an endpoint for the current process. Changing it clears
the in-memory session so credentials can never move between hosts.
