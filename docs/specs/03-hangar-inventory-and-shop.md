# SPEC-03E: Hangar inventory and Shop audit

This topical contract supplements `03-identity-companies-and-onboarding.md`. The numeric prefix in this filename is a task reference, not a replacement for the master `SPEC-03` identity contract. The post-company HTML is emulator defined; no official Shop or Dock payload has been captured.

## Implemented contract

- `LocalShopPurchase` accepts a signed, account-scoped purchase form. `EconomyStore.PurchaseAsync` serializes the account and listing changes, checks enabled item and listing, selected enabled currency, stock, account limit, wallet, quantity overflow, and idempotency key before committing inventory, purchase count, wallet ledger, and stock audit. A replay with the same request returns duplicate; a reused key with different inputs is invalid.
- `LocalHangarEquipmentStore` projects owned PostgreSQL inventory and saved equipment. Cache-era assignments import once; later reads use PostgreSQL. Configurations 1 and 2 are alternative loadouts on each of two ships, so one physical item may appear in both configurations of one ship. Reservations across ships add. Each configuration is bounded by its ship's laser, generator, drone, P.E.T., and extra slot counts.
- Legacy assignment reconciliation is deterministic by item ID and ship slot. It discards missing items, clamps to owned quantity, and caps each slot kind per configuration. Only the largest quantity of an item on a ship reserves physical inventory; unused quantity can be equipped on the other ship. A mutation is checked again under the PostgreSQL account lock.

## Edge cases and verification

`EconomyPostgresTests.LegacyEquipmentImport_CapsShipSlotsAndKeepsRemainderTransferable` imports an overfilled laser configuration, verifies the persisted cap from another store instance, equips the remaining laser on the other ship, and rejects an over-capacity assignment. `LocalHangarSlotCapacityTests` covers direct assignment boundaries. `EconomyPostgresTests` covers purchase stock, balance, count, overflow, and idempotency.

The Dock currently offers individual equip/unequip operations. An atomic two-ship transfer form is not implemented; a pilot can lower the source assignment and then equip the freed quantity on the destination.
