# RBAC (Keycloak)

Azure IAM–style model: **realm roles** bundle **client permissions** on `thdarkorbit-api`.

## Realm roles

| Role | Permissions |
|------|-------------|
| `emulator-admin` | All `backoffice.*` client roles |
| `emulator-operator` | identity read/write, keycloak read, cache write, ops read/write |
| `emulator-viewer` | identity read, keycloak read, ops read (default for new registrations) |

## Client permissions

| Permission | CMS / API |
|------------|-----------|
| `backoffice.identity.read` | List/count identity users |
| `backoffice.identity.write` | (reserved) |
| `backoffice.identity.delete` | Delete all `identity.accounts` |
| `backoffice.keycloak.read` | List Keycloak users |
| `backoffice.cache.write` | Redis FLUSHALL via API |
| `backoffice.ops.read` | Stack overview, mesh topology, world overview |
| `backoffice.ops.write` | Mesh scale, drain, rebalance, and pilot teleport/repair |

## Platform owner (seeded, dev)

On ApiService startup a normal CMS account is created with full backoffice access:

| Field | Default |
|-------|---------|
| Username | `owner` |
| Password | `owner` (override via AppHost parameter `owner-password`) |
| Keycloak role | `emulator-admin` |

Sign in at the CMS like any user; the **Backoffice** nav link appears when the JWT includes `emulator-admin` (or `backoffice.*` permissions).

Configure in `PlatformOwnerSeed` (`appsettings.json` or AppHost env vars). Set `Enabled` to `false` to disable seeding.

## Grant admin manually

1. Open http://localhost:8081 → realm **thdarkorbit** → **Users** → your user → **Role mapping**.
2. Assign realm role **emulator-admin**.
3. Sign out/in in CMS to refresh JWT.

Bootstrap creates roles on ApiService startup (`KeycloakRbacBootstrap`).

## JWT claims

- `realm_access.roles` → ASP.NET roles + `realm_role` claims
- `resource_access.thdarkorbit-api.roles` → `permission` claims

Policies: `Permission:backoffice.identity.read` etc. (`EmulatorPermissions.PolicyName`).
