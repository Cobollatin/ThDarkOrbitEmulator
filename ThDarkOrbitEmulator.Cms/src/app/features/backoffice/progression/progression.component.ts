import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { BackofficeApiService, ActiveHangarState, SkylabState, GateState, StationModule, ClanRelation } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-progression',
  imports: [FormsModule],
  template: `
    <div class="space-y-6 text-slate-100">
      <h2 class="text-xl font-semibold">Player and clan state</h2>
      <p class="text-sm text-slate-400">Space Emulator CMS operator controls. Enter an account UUID or clan ID to inspect persisted state.</p>
      @if (message()) { <p role="status" class="rounded border border-slate-600 p-3">{{ message() }}</p> }
      <section class="rounded border border-slate-700 p-4 space-y-3">
        <h3 class="font-semibold">Player</h3>
        <input aria-label="Account UUID" class="bg-slate-800 p-2" [(ngModel)]="accountId" placeholder="Account UUID">
        <button class="rounded bg-blue-600 px-3 py-2" (click)="loadPlayer()">Load player</button>
        @if (hangar(); as state) {
          <h4>Active hangar: slot {{ state.activeSlot }}</h4>
          @for (item of state.assignments; track item.slotNumber) {
            <div>Slot {{ item.slotNumber }} · {{ item.shipId }} · {{ item.hangarId }}
              <button class="ml-2 text-sky-300 underline" (click)="activate(item.slotNumber)">Set active</button></div>
          }
        }
        @if (skylab(); as state) {
          <h4>Skylab jobs</h4>
          @for (job of state.collectors; track job.collectorId) {
            <div class="flex flex-wrap items-center gap-2">{{ job.collectorId }} · level {{ job.expectedLevel }}
              <input [attr.aria-label]="job.collectorId + ' ready at'" type="datetime-local" class="bg-slate-800 p-2" [ngModel]="toLocal(job.readyAt)" (ngModelChange)="job.readyAt = fromLocal($event)">
              <button class="text-sky-300 underline" (click)="saveCollector(job.collectorId, job.readyAt)">Set deadline</button></div>
          }
          @if (state.refinery; as refinery) {
            <div class="flex flex-wrap items-center gap-2">Refinery
              <input aria-label="Refinery ready at" type="datetime-local" class="bg-slate-800 p-2" [ngModel]="toLocal(refinery.readyAt)" (ngModelChange)="refinery.readyAt = fromLocal($event)">
              <button class="text-sky-300 underline" (click)="saveRefinery(refinery.readyAt)">Set deadline</button></div>
          }
        }
        <h4>Galaxy Gate runs</h4>
        @for (gate of gates(); track gate.gateId) {
          <div class="rounded border border-slate-700 p-3 space-x-2">{{ gate.gateId }} · wave {{ gate.wave }}/{{ gate.totalWaves }}
            @if (gate.completedAt) { <span>Completed {{ gate.completedAt }}</span> }
            @else {
              <label>Wave <input aria-label="Gate wave" type="number" min="0" max="9" class="w-16 bg-slate-800" [(ngModel)]="gate.wave"></label>
              <label>Kills <input aria-label="Gate kills" type="number" min="0" class="w-16 bg-slate-800" [(ngModel)]="gate.kills"></label>
              <label>NPC HP <input aria-label="Gate NPC HP" type="number" min="1" max="1000" class="w-20 bg-slate-800" [(ngModel)]="gate.npcHp"></label>
              <label>Ship HP <input aria-label="Gate ship HP" type="number" min="0" max="1000" class="w-20 bg-slate-800" [(ngModel)]="gate.shipHp"></label>
              <label>Lives <input aria-label="Gate lives" type="number" min="0" max="5" class="w-16 bg-slate-800" [(ngModel)]="gate.lives"></label>
              <button class="text-sky-300 underline" (click)="saveGate(gate)">Save run</button>
            }
          </div>
        }
      </section>
      <section class="rounded border border-slate-700 p-4 space-y-3">
        <h3 class="font-semibold">Clan station and diplomacy</h3>
        <input aria-label="Clan ID" class="bg-slate-800 p-2" [(ngModel)]="clanId" placeholder="Clan ID">
        <button class="rounded bg-blue-600 px-3 py-2" (click)="loadClan()">Load clan</button>
        @for (module of station(); track module.moduleId) {
          <div class="space-x-2">{{ module.moduleId }}
            <label>Level <input aria-label="Station level" type="number" min="0" max="3" class="w-16 bg-slate-800" [(ngModel)]="module.level"></label>
            <input [attr.aria-label]="module.moduleId + ' ready at'" type="datetime-local" class="bg-slate-800 p-2" [ngModel]="toLocal(module.readyAt)" (ngModelChange)="module.readyAt = $event ? fromLocal($event) : null">
            <button class="text-sky-300 underline" (click)="saveStation(module)">Save module</button>
          </div>
        }
        @for (relation of relations(); track relation.otherClanId) {
          <div class="space-x-2">{{ relation.otherClanId }} · proposed by {{ relation.proposer }}
            <select aria-label="Relation status" class="bg-slate-800 p-2" [(ngModel)]="relation.status"><option value="pending">Pending</option><option value="allied">Allied</option><option value="war">War</option></select>
            <button class="text-sky-300 underline" (click)="saveRelation(relation)">Save relation</button>
          </div>
        }
      </section>
    </div>`
})
export class ProgressionComponent {
  private readonly api = inject(BackofficeApiService);
  readonly message = signal(''); readonly hangar = signal<ActiveHangarState | null>(null);
  readonly skylab = signal<SkylabState | null>(null); readonly gates = signal<GateState[]>([]);
  readonly station = signal<StationModule[]>([]); readonly relations = signal<ClanRelation[]>([]);
  accountId = ''; clanId = '';
  toLocal(value: string | null): string {
    if (!value) return '';
    const date = new Date(value);
    return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
  }
  fromLocal(value: string): string { return new Date(value).toISOString(); }
  async loadPlayer(): Promise<void> {
    try {
      const [hangar, skylab, gates] = await Promise.all([firstValueFrom(this.api.getActiveHangar(this.accountId)), firstValueFrom(this.api.getSkylabState(this.accountId)), firstValueFrom(this.api.getGateStates(this.accountId))]);
      this.hangar.set(hangar); this.skylab.set(skylab); this.gates.set(gates); this.message.set('Player state loaded.');
    } catch { this.message.set('Could not load player state. Check the account UUID.'); }
  }
  async loadClan(): Promise<void> {
    try { const [station, relations] = await Promise.all([firstValueFrom(this.api.getStation(this.clanId)), firstValueFrom(this.api.getClanRelations(this.clanId))]);
      this.station.set(station); this.relations.set(relations); this.message.set('Clan state loaded.');
    } catch { this.message.set('Could not load clan state.'); }
  }
  async activate(slot: number): Promise<void> { await this.change(() => firstValueFrom(this.api.setActiveHangar(this.accountId, slot)), () => this.loadPlayer()); }
  async saveCollector(id: string, readyAt: string): Promise<void> { await this.change(() => firstValueFrom(this.api.setCollectorReadyAt(this.accountId, id, readyAt)), () => this.loadPlayer()); }
  async saveRefinery(readyAt: string): Promise<void> { await this.change(() => firstValueFrom(this.api.setRefineryReadyAt(this.accountId, readyAt)), () => this.loadPlayer()); }
  async saveGate(gate: GateState): Promise<void> { await this.change(() => firstValueFrom(this.api.setGateState(this.accountId, gate)), () => this.loadPlayer()); }
  async saveStation(module: StationModule): Promise<void> { await this.change(() => firstValueFrom(this.api.setStation(this.clanId, module)), () => this.loadClan()); }
  async saveRelation(relation: ClanRelation): Promise<void> { await this.change(() => firstValueFrom(this.api.setClanRelation(this.clanId, relation)), () => this.loadClan()); }
  private async change(save: () => Promise<void>, reload: () => Promise<void>): Promise<void> {
    try { await save(); await reload(); this.message.set('Override saved.'); }
    catch { this.message.set('Override failed. Check the current record and field limits.'); }
  }
}
