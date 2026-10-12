# Dashboard null-metric crash fix

## Symptom

After logging in, the dashboard error boundary displayed:

`Cannot read properties of null (reading 'toFixed')`

## Cause

The model health API deliberately returns `null` for feature drift statistics until a model and sufficient recent transactions are available. The dashboard checked for `undefined`, but JSON `null` passes that check, so it called `.toFixed(2)` on `null`.

## Fix

- Added `formatFixed`, which safely formats finite numbers and renders `N/A` for `null`, `undefined`, `NaN`, or infinity.
- Updated the mean and maximum feature-shift cards to use that formatter.
- Added safe formatting for feature sensitivity deltas.
- Made the trend chart tolerate a missing risk score rather than inventing a zero.
- Updated frontend types to represent nullable API metrics explicitly.

## Apply

Rebuild the web image and recreate the frontend container:

```powershell
docker compose build web
docker compose up -d web
```

Or rebuild/start the full stack:

```powershell
docker compose up --build -d
```

No database reset is required. Do not use `docker compose down -v` for this fix.
