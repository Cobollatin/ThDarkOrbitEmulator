import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { RbacService } from '../../../core/auth/rbac.service';
import { BackofficeApiService, MeshTopology, WorldOverview } from '../data/backoffice-api.service';
import { MeshComponent } from './mesh.component';

describe('MeshComponent', () => {
  const topology: MeshTopology = {
    desiredWorkers: 2, desiredReplicas: 2, maxReplicas: 16, maxMapsPerNode: 4,
    activeNodeCount: 2, totalOnlinePilots: 1, aspireDashboardLink: 'https://localhost:17081',
    mapLeaseDistribution: [{ mapId: 1, nodeId: 'node-a', epoch: 1, expiresAtUtc: '2999-01-01T00:00:00Z' }],
    nodes: [{ nodeId: 'node-a', instanceName: 'api-game', httpEndpoint: 'http://api-game',
      worldTcpHost: '127.0.0.1', worldTcpPort: 60954, status: 'Active',
      startedAtUtc: '', lastHeartbeatUtc: '', activePilots: 1, leasedMapIds: [1],
      processCpuPercent: 5, workingSetBytes: 104857600, simulated: false }]
  };
  const world: WorldOverview = { maps: [{ mapId: 1, ownerNodeId: 'node-a', onlinePilots: 1 }],
    pilots: [{ userId: 42, userName: 'Pilot', company: 'MMO', mapId: 1,
      hangarSlot: 1, shipId: 'ship-1', x: 2000, y: 1300 }] };
  const api = {
    getMeshTopology: vi.fn(() => of(topology)), getWorldOverview: vi.fn(() => of(world)),
    scaleMesh: vi.fn(() => of({})), rebalanceMesh: vi.fn(() => of({})),
    drainMeshNode: vi.fn(() => of({})), activateMeshNode: vi.fn(() => of({})),
    teleportPilot: vi.fn(() => of(world.pilots[0]))
  };
  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({ imports: [MeshComponent], providers: [
      { provide: BackofficeApiService, useValue: api },
      { provide: RbacService, useValue: { hasPermission: () => true } }
    ] }).compileComponents();
  });

  it('renders live topology and pilot position', async () => {
    const fixture = TestBed.createComponent(MeshComponent);
    await fixture.componentInstance.refresh(); fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('2 / 2');
    expect(fixture.componentInstance.activeMapCount(topology)).toBe(1);
    expect(text).toContain('node-a');
    expect(text).toContain('1-1');
    expect(text).toContain('(2000, 1300)');
    fixture.destroy();
  });

  it('validates scaling and teleports the selected pilot', async () => {
    const fixture = TestBed.createComponent(MeshComponent);
    const component = fixture.componentInstance;
    await component.refresh();
    component.desiredReplicas = 17;
    await component.scale();
    expect(api.scaleMesh).not.toHaveBeenCalled();
    component.desiredReplicas = 4;
    await component.scale();
    expect(api.scaleMesh).toHaveBeenCalledWith(4, 4);
    component.selectPilot(42);
    component.targetX = 3000;
    await component.teleport();
    expect(api.teleportPilot).toHaveBeenCalledWith(42,
      { mapId: 1, x: 3000, y: 1300, repair: false });
    fixture.destroy();
  });
});
