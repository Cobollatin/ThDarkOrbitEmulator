# ThDarkOrbitEmulator CMS

Angular 21 frontend for CMS registration, login, and user profile.

## Run with Aspire

Start the AppHost — the `cms` resource runs `npm start` and appears in the Aspire dashboard.

## Run standalone

```bash
npm install
npm start
```

API calls are proxied to the Gateway at `http://localhost:60953` via `proxy.conf.json`.

## Routes

| Path | Description |
|------|-------------|
| `/` | Dashboard (username, email, game user ID) — requires login |
| `/login` | Sign in |
| `/register` | Create account (Keycloak + local DB) |
