import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { RbacService } from '../../../core/auth/rbac.service';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { BackofficeApiService, MeshNode, MeshTopology, WorldOverview } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-mesh',
  imports: [FormsModule, PageCardComponent],
  templateUrl: './mesh.component.html'
})
export class MeshComponent implements OnInit, OnDestroy {
  private readonly api = inject(BackofficeApiService);
  readonly canWrite = inject(RbacService).hasPermission('backoffice.ops.write');
  readonly topology = signal<MeshTopology | null>(null);
  readonly world = signal<WorldOverview | null>(null);
  readonly error = signal('');
  readonly notice = signal('');
  readonly busy = signal(false);
  desiredReplicas = 1;
  maxMapsPerNode = 4;
  selectedPilotId: number | null = null;
  targetMapId = 1;
  targetX = 2000;
  targetY = 1300;
  repair = false;
  private timer?: ReturnType<typeof setInterval>;
  private loading = false;

  ngOnInit(): void {
    void this.refresh();
    this.timer = setInterval(() => { if (!this.busy()) void this.refresh(); }, 10000);
  }

  ngOnDestroy(): void { if (this.timer) clearInterval(this.timer); }

  async refresh(): Promise<void> {
    if (this.loading) return;
    this.loading = true;
    try {
      const [topology, world] = await Promise.all([
        firstValueFrom(this.api.getMeshTopology()), firstValueFrom(this.api.getWorldOverview())
      ]);
      if (!this.topology()) {
        this.desiredReplicas = topology.desiredReplicas;
        this.maxMapsPerNode = topology.maxMapsPerNode;
      }
      this.topology.set(topology);
      this.world.set(world);
      this.error.set('');
    } catch { this.error.set('Could not load mesh and world status.'); }
    finally { this.loading = false; }
  }

  mapLabel(mapId: number): string {
    return `${Math.floor((mapId - 1) / 4) + 1}-${((mapId - 1) % 4) + 1}`;
  }

  memoryMiB(node: MeshNode): number { return Math.round(node.workingSetBytes / 1048576); }

  activeMapCount(mesh: MeshTopology): number {
    return mesh.mapLeaseDistribution.filter(lease => Date.parse(lease.expiresAtUtc) > Date.now()).length;
  }

  async runAction(action: () => ReturnType<BackofficeApiService['rebalanceMesh']>, notice: string): Promise<void> {
    if (!this.canWrite || this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      await firstValueFrom(action());
      this.notice.set(notice);
      await this.refresh();
    } catch { this.error.set('Operation failed. Check node status and permissions.'); }
    finally { this.busy.set(false); }
  }

  scale(): Promise<void> {
    const desired = Number(this.desiredReplicas), maps = Number(this.maxMapsPerNode);
    if (!Number.isInteger(desired) || desired < 1 || desired > 16 ||
        !Number.isInteger(maps) || maps < 1 || maps > 256) {
      this.error.set('Instances must be 1-16 and maps per node 1-256.');
      return Promise.resolve();
    }
    return this.runAction(() => this.api.scaleMesh(desired, maps), 'Scale target saved. Check Aspire for actual replicas.');
  }

  rebalance(): Promise<void> { return this.runAction(() => this.api.rebalanceMesh(), 'Map leases rebalanced.'); }
  drain(node: MeshNode): Promise<void> {
    return this.runAction(() => this.api.drainMeshNode(node.nodeId), `Draining ${node.nodeId}.`);
  }
  activate(node: MeshNode): Promise<void> {
    return this.runAction(() => this.api.activateMeshNode(node.nodeId), `Activated ${node.nodeId}.`);
  }

  selectPilot(userId: number): void {
    const pilot = this.world()?.pilots.find(p => p.userId === userId);
    if (!pilot) return;
    this.selectedPilotId = userId;
    this.targetMapId = pilot.mapId;
    this.targetX = pilot.x;
    this.targetY = pilot.y;
  }

  async teleport(): Promise<void> {
    const id = this.selectedPilotId;
    if (!this.canWrite || this.busy() || id === null) return;
    const mapId = Number(this.targetMapId), x = Number(this.targetX), y = Number(this.targetY);
    if (![mapId, x, y].every(Number.isInteger) || mapId < 1 || mapId > 16 ||
        x < 0 || x > 21000 || y < 0 || y > 13100) {
      this.error.set('Map must be 1-16; X 0-21000; Y 0-13100.');
      return;
    }
    this.busy.set(true);
    this.error.set('');
    try {
      await firstValueFrom(this.api.teleportPilot(id, { mapId, x, y, repair: this.repair }));
      this.notice.set(`Teleported pilot ${id}.`);
      await this.refresh();
    } catch { this.error.set('Teleport failed. The pilot may have disconnected.'); }
    finally { this.busy.set(false); }
  }
}
