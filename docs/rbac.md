# RBAC (Keycloak)

Azure IAM–style model: **realm roles** bundle **client permissions** on `thdarkorbit-api`.

## Realm roles

| Role | Permissions |
|------|-------------|
| `emulator-admin` | All `backoffice.*` client roles |
| `emulator-operator` | identity read/write, keycloak read, cache write, ops read |
| `emulator-viewer` | identity read, keycloak read, ops read (default for new registrations) |

## Client permissions

| Permission | CMS / API |
|------------|-----------|
| `backoffice.identity.read` | List/count identity users |
| `backoffice.identity.write` | (reserved) |
| `backoffice.identity.delete` | Delete all `identity.accounts` |
| `backoffice.keycloak.read` | List Keycloak users |
| `backoffice.cache.write` | Redis FLUSHALL via API |
| `backoffice.ops.read` | Stack overview |

## Grant admin (dev)

1. Open http://localhost:8081 → realm **thdarkorbit** → **Users** → your user → **Role mapping**.
2. Assign realm role **emulator-admin**.
3. Sign out/in in CMS to refresh JWT.

Bootstrap creates roles on ApiService startup (`KeycloakRbacBootstrap`).

## JWT claims

- `realm_access.roles` → ASP.NET roles + `realm_role` claims
- `resource_access.thdarkorbit-api.roles` → `permission` claims

Policies: `Permission:backoffice.identity.read` etc. (`EmulatorPermissions.PolicyName`).
