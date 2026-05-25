# Operations dashboard (Aspire commands)

There is no separate backoffice web app for infrastructure. **Player CMS** includes an RBAC-gated **Backoffice** section (`/backoffice`) that calls the API. **Low-level operations** run through **Aspire resource commands** on the AppHost and return **Markdown** in the dashboard text visualizer.

Local Aspire dashboard only (not Azure Container Apps).

## RBAC (Keycloak)

| Layer | Names | Purpose |
|-------|--------|---------|
| **Realm roles** | `emulator-admin`, `emulator-operator`, `emulator-viewer` | Azure IAM–style role bundles (composite) |
| **Client roles** (`thdarkorbit-api`) | `backoffice.identity.read`, `.write`, `.delete`, `backoffice.keycloak.read`, `backoffice.cache.write`, `backoffice.ops.read` | Fine-grained permissions in JWT `resource_access` |
| **Default** | New registrations → `emulator-viewer` | Read-only backoffice |

Grant **`emulator-admin`** in Keycloak Admin (realm `thdarkorbit` → Users → Role mapping) for full access.

API policies: `Permission:backoffice.*` (see `ThDarkOrbitEmulator.ServiceDefaults/Authorization`).

## CMS backoffice routes

| Route | Permission |
|-------|------------|
| `/backoffice/overview` | `backoffice.ops.read` |
| `/backoffice/identity` | `backoffice.identity.read` |
| `/backoffice/keycloak` | `backoffice.keycloak.read` |
| `/backoffice/operations` | `backoffice.cache.write` |

## API backoffice (`/api/v1/backoffice`)

| Method | Path | Permission |
|--------|------|------------|
| GET | `auth/permissions` | Authenticated |
| GET | `identity/users` | `backoffice.identity.read` |
| GET | `identity/users/count` | `backoffice.identity.read` |
| DELETE | `identity/users` | `backoffice.identity.delete` |
| GET | `keycloak/users` | `backoffice.keycloak.read` |
| POST | `operations/cache/clear` | `backoffice.cache.write` |
| GET | `operations/overview` | `backoffice.ops.read` |

## BO commands (Aspire — low-level)

Run from **Aspire dashboard → Resources → ⋮ → command**, or CLI `aspire resource <name> <command-id>`.

### Postgres (`thdarkorbit-emulator`)

| Command id | Label | Action |
|------------|-------|--------|
| `bo-identity-users` | BO: List identity users | `SELECT` from `identity.accounts` (DB `thdarkorbit`) |
| `bo-identity-user-count` | BO: Identity user count | `COUNT(*)` |
| `bo-identity-delete-all` | BO: Delete all identity users | `DELETE FROM identity.accounts` (confirmation) |
| `operations-database` | Operations: Database | Markdown schema layout |

### Keycloak (`keycloak`)

| Command id | Label | Action |
|------------|-------|--------|
| `bo-keycloak-users` | BO: List Keycloak users | Admin REST API, realm `thdarkorbit` |

### Redis (`cache`)

| Command id | Label | Action |
|------------|-------|--------|
| `operations-redis-info` | Operations: Redis info | Markdown notes |
| `operations-clear-cache` | Operations: Clear cache | `FLUSHALL` (confirmation) |

### Gateway (`gateway`)

| Command id | Label | Action |
|------------|-------|--------|
| `operations-overview` | Operations: Overview | Stack summary + links |
| `operations-deployments` | Operations: Deployments | AppHost resource list |

### ApiService (`apiservice`)

| Command id | Label | Action |
|------------|-------|--------|
| `operations-emulator` | Operations: Emulator | Config / feature flags markdown |

### Two user stores

| Store | Aspire BO | CMS / API |
|-------|-----------|-----------|
| **identity.accounts** | Postgres BO commands | `/backoffice/identity` |
| **Keycloak** realm users | Keycloak BO command | `/backoffice/keycloak` |

Deleting identity rows does **not** remove Keycloak users.

## Observability

| UI | URL | Notes |
|----|-----|-------|
| **Aspire dashboard** | https://localhost:17081 | Traces, structured logs (OTLP from apps → collector → dashboard) |
| **Prometheus** | http://localhost:19090 | Scrapes `otel-collector:8889` |
| **Grafana** | http://localhost:13000 (admin/admin) | Prometheus datasource + emulator overview dashboard |
| **RedisInsight** | http://localhost:5540 | Auto-connected to `cache` via `.WithRedisInsight()` (Aspire wires host, port, password) |
| **pgAdmin** | http://localhost:5050 | Postgres admin |

`apiservice` and `gateway` send OTLP to **`otel-collector`** (`http://otel-collector:4318`). Metrics are exported to Prometheus; traces/logs are forwarded to the Aspire OTLP endpoint configured on the collector.

## Traces and structured logs

See previous sections in this doc: generate traffic on `apiservice` / `gateway`, filter by resource in the Aspire dashboard.

## Implementation

- Commands: `ThDarkOrbitEmulator.AppHost/Operations/*.cs`
- Observability configs: `ThDarkOrbitEmulator.AppHost/observability/`
- RBAC bootstrap: `KeycloakBootstrapHostedService` + `KeycloakRbacBootstrap`

## CLI examples

```bash
aspire resource thdarkorbit-emulator bo-identity-users
aspire resource thdarkorbit-emulator bo-identity-delete-all
aspire resource keycloak bo-keycloak-users
aspire resource cache operations-clear-cache
aspire resource gateway operations-overview
```
