import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { APP_SETTINGS } from '../../../core/config/app-settings';
import { BackofficeApiService, EconomyListing } from './backoffice-api.service';

describe('BackofficeApiService economy contracts', () => {
  let api: BackofficeApiService;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(),
      { provide: APP_SETTINGS, useValue: { apiBaseUrl: '/api/v1' } }] });
    api = TestBed.inject(BackofficeApiService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('serializes recipe parts into the admin request contract', () => {
    api.putEconomyRecipe({ id: 'r1', name: 'Recipe', inputs: '[{"kind":"item","id":"i1","quantity":1}]',
      outputs: '[{"kind":"resource","id":"ore","quantity":2}]', craft_seconds: 30, enabled: true }).subscribe();
    const request = http.expectOne('/api/v1/economy/recipes/r1');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.inputsJson).toContain('i1');
    expect(request.request.body.craftSeconds).toBe(30);
    request.flush(null);
  });

  it('sends listing restock rules and audit filters', () => {
    const listing: EconomyListing = { id: 'offer', itemId: 'i1', name: '', type: '', rarity: '',
      prices: { credits: 5 }, stockAvailable: 10, perAccountLimit: 2,
      restockIntervalSeconds: 3600, restockAmount: 3, enabled: true };
    api.putEconomyListing(listing).subscribe();
    const put = http.expectOne('/api/v1/economy/listings/offer');
    expect(put.request.body.pricesJson).toBe('{"credits":5}');
    expect(put.request.body.restockAmount).toBe(3);
    put.flush(null);
    api.getEconomyStockAudit('offer', { reason: 'admin_restock', beforeId: 42 }).subscribe();
    const audit = http.expectOne(r => r.url === '/api/v1/economy/listings/offer/stock-audit');
    expect(audit.request.params.get('beforeId')).toBe('42');
    expect(audit.request.params.get('reason')).toBe('admin_restock');
    audit.flush([]);
  });

  it('uses mesh and world control contracts', () => {
    api.getMeshTopology().subscribe();
    const topology = http.expectOne('/api/v1/backoffice/mesh/topology');
    expect(topology.request.method).toBe('GET'); topology.flush({});
    api.scaleMesh(4, 8).subscribe();
    const scale = http.expectOne('/api/v1/backoffice/mesh/scale');
    expect(scale.request.method).toBe('PUT');
    expect(scale.request.body).toEqual({ desiredReplicas: 4, maxMapsPerNode: 8 }); scale.flush({});
    api.drainMeshNode('node/1').subscribe();
    const drain = http.expectOne('/api/v1/backoffice/mesh/nodes/node%2F1/drain');
    expect(drain.request.method).toBe('POST'); drain.flush({});
    api.activateMeshNode('node/1').subscribe();
    const activate = http.expectOne('/api/v1/backoffice/mesh/nodes/node%2F1/activate');
    expect(activate.request.method).toBe('POST'); activate.flush({});
    api.rebalanceMesh().subscribe();
    const rebalance = http.expectOne('/api/v1/backoffice/mesh/rebalance');
    expect(rebalance.request.method).toBe('POST'); rebalance.flush({});
    api.getWorldOverview().subscribe();
    const world = http.expectOne('/api/v1/backoffice/world/overview');
    expect(world.request.method).toBe('GET'); world.flush({ maps: [], pilots: [] });
    api.teleportPilot(42, { mapId: 5, x: 10, y: 20, repair: true }).subscribe();
    const teleport = http.expectOne('/api/v1/backoffice/world/pilots/42/teleport');
    expect(teleport.request.method).toBe('POST');
    expect(teleport.request.body).toEqual({ mapId: 5, x: 10, y: 20, repair: true }); teleport.flush({});
  });

  it('uses CMS runtime variable, history, rollback and command contracts', () => {
    api.listVariables().subscribe();
    const list = http.expectOne('/api/v1/backoffice/runtime-vars');
    expect(list.request.method).toBe('GET'); list.flush([]);
    api.setVariable('world.movement.speed_multiplier', '2', 'event').subscribe();
    const set = http.expectOne('/api/v1/backoffice/runtime-vars/world.movement.speed_multiplier');
    expect(set.request.method).toBe('PUT');
    expect(set.request.body).toEqual({ value: '2', reason: 'event' }); set.flush({});
    api.resetVariable('world.tick_rate_ms', 'reset').subscribe();
    const reset = http.expectOne('/api/v1/backoffice/runtime-vars/world.tick_rate_ms/reset');
    expect(reset.request.body).toEqual({ reason: 'reset' }); reset.flush({});
    api.getVariableHistory(42).subscribe();
    const history = http.expectOne(r => r.url === '/api/v1/backoffice/runtime-vars/history');
    expect(history.request.params.get('beforeChangeId')).toBe('42'); history.flush([]);
    api.rollbackVariable(42, 'undo').subscribe();
    const rollback = http.expectOne('/api/v1/backoffice/runtime-vars/history/42/rollback');
    expect(rollback.request.body).toEqual({ reason: 'undo' }); rollback.flush({});
    api.executeAdminCommand('var list').subscribe();
    const command = http.expectOne('/api/v1/backoffice/admin/command');
    expect(command.request.body).toEqual({ text: 'var list', surface: 'AdminCms' }); command.flush({});
  });
});
