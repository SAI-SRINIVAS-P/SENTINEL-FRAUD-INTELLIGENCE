# UI fixes for blank dashboard and misaligned login

## What caused the login layout problem

`LoginScreen` includes a decorative `login-decoration` element before the left and right panels. The old `.login-shell` CSS treated all three elements as grid items, so the decorative element occupied a grid cell and pushed the actual panels into the wrong columns/rows. The decoration is now absolutely positioned and the two panes use explicit grid areas.

## What changed to protect dashboard rendering

- API JSON objects are normalized to camelCase recursively, so a serializer configured to emit PascalCase cannot make `roles`, transaction page items, or dashboard fields unexpectedly undefined.
- Stored auth data is validated and normalized before the dashboard renders.
- Login responses are checked for a token and email; malformed responses display an error rather than transitioning to a broken dashboard.
- The review queue and model list guard against absent arrays.
- A React error boundary renders a diagnostic page instead of leaving an empty background if a later UI render throws.
- The earlier `AuthController` and `ModelsController` compilation fixes are included in this archive.

## Local credentials and password recovery

The ZIP includes a local `.env` configured with `ADMIN_EMAIL=admin-recovery@local.dev` and the requested demo password. The frontend pre-fills that email. The compose file maps `ADMIN_RESET_PASSWORD_ON_STARTUP=true`, so the API creates the configured admin if missing or resets its password (and clears a lockout) if it already exists. This flag must be set to `false` before any shared deployment because it reapplies the configured environment password at every API startup.

## Apply the updated build

From the extracted project folder in PowerShell:

```powershell
docker compose down
docker compose up --build -d
docker compose ps
docker compose logs --tail=100 web api
```

Then open `http://localhost:3001` and perform a hard refresh with `Ctrl+F5`. If the app had stored an old or invalid login session, click Sign out if shown, or clear `sessionStorage` for `localhost` in browser DevTools and sign in again.

## Validation boundary

The modified TypeScript files pass syntax transpilation, and JSON/YAML/project XML plus key CSS layout assertions are checked during packaging. A complete `npm run build` could not be run here because npm dependency installation timed out. Run the Docker rebuild above to validate the production bundle in your environment.
