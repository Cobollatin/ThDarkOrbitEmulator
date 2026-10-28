# SPEC-07E: Economy, progression, Skylab, and Galaxy Gates audit

This topical contract supplements `07-scalable-server-mesh-and-aspire.md`. Its number is a task reference, not the master `SPEC-07` mesh contract. These local menu rules are emulator defined; captured official post-company menu contracts are still unavailable.

## Current contracts

- `EconomyStore` owns account balances, ledger, inventory, shop purchases, auction bids and claims, recipe crafts, rewards, collector progress, and gate parts. Account advisory locks and transaction-scoped idempotency protect cross-replica mutations. Catalog and admin endpoints live in `Features/Economy/`; menu adapters live in `Features/IndexInternal/InternalStart/`.
- Skylab has three collectors: prometium, endurium, and terbium. Each begins at level 1 with 500 stock. Upgrade costs `100 × current level` of its own resource and stops at level 10. Whole elapsed minutes generate `10 × level` per collector per minute, at most 60 minutes per collection. A collection advances the saved time by all elapsed whole minutes, discarding production beyond the 60-minute window. A stale expected time, replayed key, insufficient balance, or collection before one minute produces no partial credit.
- Galaxy Gates spend one energy per part. One spin form admits 1–10 parts, up to the remaining requirement. Activation requires all parts and costs no energy. An activated gate rejects further spins or activation. Parts and energy survive cache loss.
- Quests, battle pass, payment, vouchers, and expeditions use account-scoped claim keys and durable reward ledger entries where the matching menu is implemented.

## Verification and remaining contracts

`LocalSkylabPostgresTests` covers upgrade, maximum level, elapsed minute boundaries, cap, stale forms, and cache loss. `LocalGalaxyGatePostgresTests` covers energy debit, spin replay, incomplete/full/activated gates, concurrency, and cache loss. `EconomyPostgresTests` covers durable balances and cross-replica mutation.

Task 6 adds durable provisional progression in migration `031_skylab_jobs_and_gate_waves`: separate collector upgrade jobs, one refinery job per account, and three-wave gate runs. A timed collector upgrade reserves `100 × current level` of its own resource, takes `current level` whole minutes, and raises the level only on a ready claim. A refinery batch reserves 10 prometium and 10 endurium, takes five minutes, and credits 10 prometid on claim. An activated gate starts at wave 0 of 3; each signed action advances exactly one expected wave, and the third records completion. These values and menu forms are emulator-defined because no client evidence fixes rates, duration, rewards, or wave counts. The gate wave action records progression; it does not verify combat results or enter a world instance. PostgreSQL account locks, action keys, expected levels/waves, and transaction-scoped debits protect concurrent and replayed mutations. Existing instant collector upgrade actions remain accepted for legacy menu compatibility, while the hosted menu offers timed upgrades.
