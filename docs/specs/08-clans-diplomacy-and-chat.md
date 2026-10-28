# SPEC-08E: Clans, diplomacy, and chat audit

This topical contract supplements `08-admin-commands-and-runtime-variables.md`; the numeric prefix is a task reference. The Clan menu is an emulator defined directory rather than a captured official clan contract.

## Current contract

`LocalClanStore` provides a fixed, company-scoped directory with recruiting flags, single active membership, signed join/leave forms, expected-membership conflict checks, and action-key replay protection. PostgreSQL membership replaces cache-era state on first read. The menu exposes bounded search, recruiting/closed filters, and clan details only for the pilot's company. `LocalMessagingStore` persists account messaging state; it is not a clan chat channel.

`LocalClanPostgresTests` covers durable membership and form conflicts. `LocalClanValidationTests`, `LocalClanActionHardeningTests`, and `LocalClanDetailTests` cover request boundaries and page projection.

Diplomatic relations, invitations and applications, station ownership, station module construction and timers, and clan chat messages are not represented in the current domain or schema. They require clan identity and authority rules, durable state, and authorized routes before a complete diplomacy/station contract can be tested. No captured client contract presently fixes these values or actions.
