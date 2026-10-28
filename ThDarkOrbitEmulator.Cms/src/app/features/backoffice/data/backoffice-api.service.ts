import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { APP_SETTINGS } from '../../../core/config/app-settings';

export interface IdentityUserRow {
  id: string;
  username: string;
  email: string;
  gameUserId: number;
}

export interface ClientLanguage { code: string; name: string; enabled: boolean; isDefault: boolean }
export interface ClientServer { code: string; instanceName: string; host: string; mapId: number; status: 'online' | 'maintenance' | 'offline'; enabled: boolean; isDefault: boolean }
export interface CatalogShip { shipId: string; name: string; modelId: number; iconPath: string; enabled: boolean }
export interface HangarAssignment { accountId: string; slotNumber: number; shipId: string; hangarId: string }
export interface LocaleOverride { languageCode: string; category: string; key: string; value: string }
export interface ActiveHangarState { activeSlot: number; assignments: HangarAssignment[] }
export interface CollectorJob { collectorId: string; expectedLevel: number; readyAt: string }
export interface SkylabState { collectors: CollectorJob[]; refinery: { readyAt: string } | null }
export interface GateState { gateId: string; wave: number; totalWaves: number; kills: number; npcHp: number; shipHp: number; lives: number; completedAt: string | null }
export interface StationModule { moduleId: string; level: number; readyAt: string | null }
export interface ClanRelation { otherClanId: string; status: string; proposer: string }

export interface KeycloakUserRow {
  id: string;
  username: string;
  email: string;
  enabled: boolean;
}

export interface BackofficeOverview {
  services: { name: string; role: string; status: string }[];
  links: { label: string; url: string }[];
}

export interface ActivityTrackingSession {
  id: string;
  subjectId: string;
  operatorName: string;
  reason: string;
  startedAtUtc: string;
}

export interface ActivityTrackingEvent {
  sequence: number;
  sessionId: string;
  type: string;
  source: string;
  details: string;
  occurredAtUtc: string;
}

export interface ActivityTrackingSnapshot {
  session: ActivityTrackingSession;
  events: ActivityTrackingEvent[];
}

export interface EconomyAsset {
  id: string;
  kind: 'currency' | 'resource';
  name: string;
  enabled: boolean;
}

export interface EconomyListing {
  id: string;
  itemId: string;
  name: string;
  type: string;
  rarity: string;
  prices: Record<string, number>;
  stockAvailable: number | null;
  perAccountLimit: number | null;
  restockIntervalSeconds: number | null;
  restockAmount: number | null;
  enabled: boolean;
}

export interface EconomyItem {
  id: string; name: string; type: string; rarity: string;
  stats: string; asset_key: string | null; enabled: boolean;
}

export interface EconomyRecipePart { kind: 'item' | 'currency' | 'resource'; id: string; quantity: number }
export interface EconomyRecipe {
  id: string; name: string; inputs: string; outputs: string;
  craft_seconds: number; enabled: boolean;
}
export interface EconomyAuditRow {
  id: number; delta: number; reason: string; occurred_at: string;
  asset_id?: string; balance_after?: number; stock_after?: number;
  menu_source?: string; admin_actor_subject?: string | null;
}

export interface EconomyBalance {
  assetId: string;
  kind: string;
  amount: number;
}

export interface EconomyAuctionLot {
  id: string;
  itemId: string;
  openingBid: number;
  durationSeconds: number;
  enabled: boolean;
}

export type MeshNodeStatus = 'Active' | 'Draining' | 'Offline';
export interface MeshNode {
  nodeId: string; instanceName: string; httpEndpoint: string;
  worldTcpHost: string; worldTcpPort: number; status: MeshNodeStatus;
  startedAtUtc: string; lastHeartbeatUtc: string; activePilots: number;
  leasedMapIds: number[]; processCpuPercent: number; workingSetBytes: number;
  simulated: boolean;
}
export interface MapLease { mapId: number; nodeId: string; epoch: number; expiresAtUtc: string }
export interface MeshSnapshot { nodes: MeshNode[]; leases: MapLease[]; desiredReplicas: number; maxMapsPerNode: number }
export interface MeshTopology {
  desiredWorkers: number; desiredReplicas: number; maxReplicas: number; maxMapsPerNode: number;
  activeNodeCount: number; totalOnlinePilots: number; mapLeaseDistribution: MapLease[];
  nodes: MeshNode[]; aspireDashboardLink: string;
}
export interface WorldPilot {
  userId: number; userName: string; company: string; mapId: number;
  hangarSlot: number; shipId: string; x: number; y: number;
}
export interface WorldOverview { maps: { mapId: number; ownerNodeId: string | null; onlinePilots: number }[]; pilots: WorldPilot[] }
export interface TeleportRequest { mapId: number; x: number; y: number; repair: boolean }
export interface RuntimeVariable {
  key: string; category: string; valueType: string; defaultValue: string; currentValue: string;
}
export type AdminSurface = 'ServerManagement' | 'AdminCms' | 'InGameAdmin';
export interface RuntimeVariableChange {
  changeId: number; variableKey: string; category: string; previousValue: string;
  newValue: string; defaultValue: string; valueType: string; surface: AdminSurface;
  changedBy: string; nodeId: string; reason: string; rawCommand: string | null;
  changedAtUtc: string;
}
export interface AdminCommandResult { command: string; data: unknown; auditId: number | null }

@Injectable({ providedIn: 'root' })
export class BackofficeApiService {
  private readonly http = inject(HttpClient);
  private readonly settings = inject(APP_SETTINGS);

  private url(path: string): string {
    return `${this.settings.apiBaseUrl}/backoffice/${path}`;
  }

  private economyUrl(path: string): string {
    return `${this.settings.apiBaseUrl}/economy/${path}`;
  }

  listClientLanguages(): Observable<ClientLanguage[]> { return this.http.get<ClientLanguage[]>(this.url('cockpit/languages')); }
  putClientLanguage(item: ClientLanguage): Observable<void> { return this.http.put<void>(this.url(`cockpit/languages/${encodeURIComponent(item.code)}`), item); }
  listClientServers(): Observable<ClientServer[]> { return this.http.get<ClientServer[]>(this.url('cockpit/servers')); }
  putClientServer(item: ClientServer): Observable<void> { return this.http.put<void>(this.url(`cockpit/servers/${encodeURIComponent(item.code)}`), item); }
  listCatalogShips(): Observable<CatalogShip[]> { return this.http.get<CatalogShip[]>(this.url('cockpit/ships')); }
  putCatalogShip(item: CatalogShip): Observable<void> { return this.http.put<void>(this.url(`cockpit/ships/${encodeURIComponent(item.shipId)}`), item); }
  listHangarAssignments(accountId: string): Observable<HangarAssignment[]> { return this.http.get<HangarAssignment[]>(this.url(`cockpit/accounts/${encodeURIComponent(accountId)}/hangars`)); }
  putHangarAssignment(item: HangarAssignment): Observable<void> { return this.http.put<void>(this.url(`cockpit/accounts/${encodeURIComponent(item.accountId)}/hangars/${item.slotNumber}`), item); }
  listLocaleOverrides(): Observable<LocaleOverride[]> { return this.http.get<LocaleOverride[]>(this.url('cockpit/locale-overrides')); }
  putLocaleOverride(item: LocaleOverride): Observable<void> { return this.http.put<void>(this.url(`cockpit/locale-overrides/${encodeURIComponent(item.languageCode)}/${encodeURIComponent(item.category)}/${encodeURIComponent(item.key)}`), item); }

  getActiveHangar(accountId: string): Observable<ActiveHangarState> { return this.http.get<ActiveHangarState>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/hangar`)); }
  setActiveHangar(accountId: string, slot: number): Observable<void> { return this.http.put<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/hangar`), { slot }); }
  getSkylabState(accountId: string): Observable<SkylabState> { return this.http.get<SkylabState>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/skylab`)); }
  setCollectorReadyAt(accountId: string, collectorId: string, readyAt: string): Observable<void> { return this.http.put<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/skylab/collectors/${encodeURIComponent(collectorId)}`), { readyAt }); }
  setRefineryReadyAt(accountId: string, readyAt: string): Observable<void> { return this.http.put<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/skylab/refinery`), { readyAt }); }
  completeCollectorTimer(accountId: string, collectorId: string): Observable<void> { return this.http.post<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/skylab/collectors/${encodeURIComponent(collectorId)}/complete-timer`), {}); }
  completeRefineryTimer(accountId: string): Observable<void> { return this.http.post<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/skylab/refinery/complete-timer`), {}); }
  getGateStates(accountId: string): Observable<GateState[]> { return this.http.get<GateState[]>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/gates`)); }
  setGateState(accountId: string, gate: GateState): Observable<void> { return this.http.put<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/gates/${encodeURIComponent(gate.gateId)}`), gate); }
  resetGateWave(accountId: string, gateId: string): Observable<void> { return this.http.post<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/gates/${encodeURIComponent(gateId)}/reset-wave`), {}); }
  completeGateWave(accountId: string, gateId: string): Observable<void> { return this.http.post<void>(this.url(`progression/accounts/${encodeURIComponent(accountId)}/gates/${encodeURIComponent(gateId)}/complete-wave`), {}); }
  getStation(clanId: string): Observable<StationModule[]> { return this.http.get<StationModule[]>(this.url(`progression/clans/${encodeURIComponent(clanId)}/station`)); }
  setStation(clanId: string, module: StationModule): Observable<void> { return this.http.put<void>(this.url(`progression/clans/${encodeURIComponent(clanId)}/station/${encodeURIComponent(module.moduleId)}`), module); }
  getClanRelations(clanId: string): Observable<ClanRelation[]> { return this.http.get<ClanRelation[]>(this.url(`progression/clans/${encodeURIComponent(clanId)}/relations`)); }
  setClanRelation(clanId: string, relation: ClanRelation): Observable<void> { return this.http.put<void>(this.url(`progression/clans/${encodeURIComponent(clanId)}/relations/${encodeURIComponent(relation.otherClanId)}`), relation); }

  getMeshTopology(): Observable<MeshTopology> {
    return this.http.get<MeshTopology>(this.url('mesh/topology'));
  }

  scaleMesh(desiredReplicas: number, maxMapsPerNode: number): Observable<MeshSnapshot> {
    return this.http.put<MeshSnapshot>(this.url('mesh/scale'), { desiredReplicas, maxMapsPerNode });
  }

  drainMeshNode(nodeId: string): Observable<MeshSnapshot> {
    return this.http.post<MeshSnapshot>(this.url(`mesh/nodes/${encodeURIComponent(nodeId)}/drain`), {});
  }

  activateMeshNode(nodeId: string): Observable<MeshSnapshot> {
    return this.http.post<MeshSnapshot>(this.url(`mesh/nodes/${encodeURIComponent(nodeId)}/activate`), {});
  }

  rebalanceMesh(): Observable<MeshSnapshot> {
    return this.http.post<MeshSnapshot>(this.url('mesh/rebalance'), {});
  }

  getWorldOverview(): Observable<WorldOverview> {
    return this.http.get<WorldOverview>(this.url('world/overview'));
  }

  teleportPilot(userId: number, request: TeleportRequest): Observable<WorldPilot> {
    return this.http.post<WorldPilot>(this.url(`world/pilots/${encodeURIComponent(userId)}/teleport`), request);
  }

  listVariables(): Observable<RuntimeVariable[]> {
    return this.http.get<RuntimeVariable[]>(this.url('runtime-vars'));
  }

  setVariable(key: string, value: string, reason: string): Observable<RuntimeVariableChange> {
    return this.http.put<RuntimeVariableChange>(this.url(`runtime-vars/${encodeURIComponent(key)}`), { value, reason });
  }

  resetVariable(key: string, reason: string): Observable<RuntimeVariableChange> {
    return this.http.post<RuntimeVariableChange>(this.url(`runtime-vars/${encodeURIComponent(key)}/reset`), { reason });
  }

  getVariableHistory(beforeChangeId?: number): Observable<RuntimeVariableChange[]> {
    return this.http.get<RuntimeVariableChange[]>(this.url('runtime-vars/history'),
      { params: beforeChangeId === undefined ? { limit: 100 } : { limit: 100, beforeChangeId } });
  }

  rollbackVariable(changeId: number, reason: string): Observable<RuntimeVariableChange> {
    return this.http.post<RuntimeVariableChange>(this.url(`runtime-vars/history/${changeId}/rollback`), { reason });
  }

  executeAdminCommand(text: string): Observable<AdminCommandResult> {
    return this.http.post<AdminCommandResult>(this.url('admin/command'), { text, surface: 'AdminCms' });
  }

  listEconomyAssets(): Observable<EconomyAsset[]> {
    return this.http.get<EconomyAsset[]>(this.economyUrl('assets'));
  }

  listEconomyItems(): Observable<EconomyItem[]> {
    return this.http.get<EconomyItem[]>(this.economyUrl('items'));
  }

  putEconomyItem(item: EconomyItem): Observable<void> {
    return this.http.put<void>(this.economyUrl(`items/${encodeURIComponent(item.id)}`),
      { id: item.id, name: item.name, type: item.type, rarity: item.rarity,
        statsJson: item.stats, assetKey: item.asset_key, enabled: item.enabled });
  }

  listEconomyRecipes(): Observable<EconomyRecipe[]> {
    return this.http.get<EconomyRecipe[]>(this.economyUrl('recipes'));
  }

  putEconomyRecipe(recipe: EconomyRecipe): Observable<void> {
    return this.http.put<void>(this.economyUrl(`recipes/${encodeURIComponent(recipe.id)}`),
      { id: recipe.id, name: recipe.name, inputsJson: recipe.inputs, outputsJson: recipe.outputs,
        craftSeconds: recipe.craft_seconds, enabled: recipe.enabled });
  }

  putEconomyListing(listing: EconomyListing): Observable<void> {
    return this.http.put<void>(this.economyUrl(`listings/${encodeURIComponent(listing.id)}`),
      { id: listing.id, itemId: listing.itemId, pricesJson: JSON.stringify(listing.prices),
        stockAvailable: listing.stockAvailable, perAccountLimit: listing.perAccountLimit,
        restockIntervalSeconds: listing.restockIntervalSeconds, restockAmount: listing.restockAmount,
        enabled: listing.enabled });
  }

  getEconomyLedger(accountId: string, filters: { assetId?: string; reason?: string; beforeId?: number } = {}): Observable<EconomyAuditRow[]> {
    return this.http.get<EconomyAuditRow[]>(this.economyUrl(`accounts/${encodeURIComponent(accountId)}/ledger`),
      { params: { ...filters, limit: 50 } });
  }

  getEconomyStockAudit(listingId: string, filters: { reason?: string; beforeId?: number } = {}): Observable<EconomyAuditRow[]> {
    return this.http.get<EconomyAuditRow[]>(this.economyUrl(`listings/${encodeURIComponent(listingId)}/stock-audit`),
      { params: { ...filters, limit: 50 } });
  }

  listEconomyListings(): Observable<EconomyListing[]> {
    return this.http.get<EconomyListing[]>(this.economyUrl('listings'));
  }

  listEconomyAuctionLots(): Observable<EconomyAuctionLot[]> {
    return this.http.get<EconomyAuctionLot[]>(this.economyUrl('auction-lots'));
  }

  putEconomyAuctionLot(lot: EconomyAuctionLot): Observable<void> {
    return this.http.put<void>(this.economyUrl(`auction-lots/${encodeURIComponent(lot.id)}`), lot);
  }

  getEconomyBalances(accountId: string): Observable<EconomyBalance[]> {
    return this.http.get<EconomyBalance[]>(this.economyUrl(`accounts/${encodeURIComponent(accountId)}/balances`));
  }

  putEconomyAsset(asset: EconomyAsset): Observable<void> {
    return this.http.put<void>(this.economyUrl(`assets/${encodeURIComponent(asset.id)}`), asset);
  }

  adjustEconomyStock(listingId: string, delta: number, reason: string): Observable<void> {
    return this.http.post<void>(this.economyUrl(`listings/${encodeURIComponent(listingId)}/stock`), { delta, reason });
  }

  adjustEconomyBalance(accountId: string, assetId: string, delta: number, reason: string, idempotencyKey: string): Observable<void> {
    return this.http.post<void>(this.economyUrl(`accounts/${encodeURIComponent(accountId)}/balances/${encodeURIComponent(assetId)}`),
      { delta, reason, menuSource: 'cms', idempotencyKey });
  }

  getPermissions(): Observable<{
    username: string;
    permissions: string[];
    realmRoles: string[];
    roles: string[];
  }> {
    return this.http.get<{
      username: string;
      permissions: string[];
      realmRoles: string[];
      roles: string[];
    }>(this.url('auth/permissions'));
  }

  listIdentityUsers(): Observable<{ users: IdentityUserRow[] }> {
    return this.http.get<{ users: IdentityUserRow[] }>(this.url('identity/users'));
  }

  getIdentityUserCount(): Observable<{ count: number }> {
    return this.http.get<{ count: number }>(this.url('identity/users/count'));
  }

  deleteAllIdentityUsers(): Observable<{ deleted: number }> {
    return this.http.delete<{ deleted: number }>(this.url('identity/users'));
  }

  listKeycloakUsers(): Observable<{ users: KeycloakUserRow[] }> {
    return this.http.get<{ users: KeycloakUserRow[] }>(this.url('keycloak/users'));
  }

  clearCache(): Observable<{ cleared: boolean }> {
    return this.http.post<{ cleared: boolean }>(this.url('operations/cache/clear'), {});
  }

  getOverview(): Observable<BackofficeOverview> {
    return this.http.get<BackofficeOverview>(this.url('operations/overview'));
  }

  startActivityTrackingSession(request: {
    subjectId: string;
    reason: string;
  }): Observable<{ session: ActivityTrackingSession }> {
    return this.http.post<{ session: ActivityTrackingSession }>(
      this.url('activity-tracking/sessions'),
      request
    );
  }

  appendActivityTrackingEvent(
    sessionId: string,
    request: { type: string; source: string; details: string }
  ): Observable<{ event: ActivityTrackingEvent }> {
    return this.http.post<{ event: ActivityTrackingEvent }>(
      this.url(`activity-tracking/sessions/${sessionId}/events`),
      request
    );
  }

  getActivityTrackingSnapshot(sessionId: string): Observable<ActivityTrackingSnapshot> {
    return this.http.get<ActivityTrackingSnapshot>(
      this.url(`activity-tracking/sessions/${sessionId}`)
    );
  }
}
