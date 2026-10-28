# SPEC-08E: Clans, diplomacy, and chat audit

This topical contract supplements `08-admin-commands-and-runtime-variables.md`; the numeric prefix is a task reference. The Clan menu is an emulator defined directory rather than a captured official clan contract.

## Current contract

`LocalClanStore` provides a fixed, company-scoped directory with recruiting flags, single active membership, signed join/leave forms, expected-membership conflict checks, and action-key replay protection. PostgreSQL membership replaces cache-era state on first read. The menu exposes bounded search, recruiting/closed filters, and clan details only for the pilot's company. `LocalMessagingStore` persists account messaging state; it is not a clan chat channel.

`LocalClanPostgresTests` covers durable membership and form conflicts. `LocalClanValidationTests`, `LocalClanActionHardeningTests`, and `LocalClanDetailTests` cover request boundaries and page projection.

## Provisional Task 7 contract

The first member of a directory clan becomes its leader. Leadership passes to the lowest account ID among remaining members when the leader leaves; an empty clan loses its leader. Only the current leader may change diplomacy or upgrade station modules. Relations are limited to clans of the same company and persisted once per unordered clan pair. A leader may propose an alliance; the other leader may accept or reject it. A leader may declare war from neutral or allied status, and either leader may end an alliance or war. All operations use session-bound, expiring form signatures and durable replay keys. State checks and writes are serialized per relation or module.

Each clan has provisional hull, defense, and relay modules, initially level zero. An upgrade increases one level up to level three and sets a five-minute ready time; another upgrade is rejected until then. Construction has no resource cost because no captured client contract specifies costs. The directory page shows relations and module progress to members and actions to leaders. Migration `032_clan_diplomacy_stations` stores leaders, relations, modules, and operation keys. Invitations/applications, station ownership in the world, and clan chat remain unspecified by captured evidence and are not exposed as inferred gameplay.
