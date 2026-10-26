# Aspire dashboard naming

## Convention

Resource IDs use **`{purpose}-{technology}`** in kebab-case:

| Pattern | Meaning |
|---------|---------|
| **Purpose** (first segment) | What the resource does for the emulator |
| **Technology** (last segment) | The product/protocol experts recognize |

Examples: `sessions-redis`, `auth-keycloak`, `dashboards-grafana`.

Wire/DNS names follow the Aspire resource ID. Connection strings and env vars use the same ID (e.g. `SESSIONS_REDIS_*`, `ConnectionStrings:sessions-redis`).

## Resource map

| Resource ID | Purpose | Technology |
|-------------|---------|------------|
| `sessions-redis` | Game session cache | Redis |
| `sessions-redisinsight` | Cache browser UI | RedisInsight |
| `data-postgres` | Application + IdP database | PostgreSQL |
| `app-db` | Logical DB `thdarkorbit` | PostgreSQL database |
| `auth-keycloak` | Login, RBAC, JWT | Keycloak |
| `telemetry-otel` | OTLP fan-out | OpenTelemetry Collector |
| `metrics-prometheus` | Metrics storage | Prometheus |
| `dashboards-grafana` | Metrics dashboards | Grafana |
| `api-game` | Game + identity API | .NET (FastEndpoints) |
| `gateway-players` | Public HTTP entry | .NET (YARP) |
| `cms-players` | Player / backoffice CMS | Angular |

## Dashboard polish

- **Icons**: Fluent UI via `WithPurposeIcon` (`Database`, `ShieldKeyhole`, `ChartMultiple`, …).
- **URLs**: Primary UI link on the `http` endpoint; docs links where useful.
- **Metrics**: Apps → OTLP → `telemetry-otel` → Prometheus → `dashboards-grafana` (Emulator overview + HTTP RED dashboards).
