import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { BackofficeApiService, CatalogShip, ClientLanguage, ClientServer, HangarAssignment, LocaleOverride } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-cockpit',
  imports: [FormsModule],
  template: `
    <div class="space-y-6 text-slate-100">
      <h2 class="text-xl font-semibold">Unity cockpit catalog</h2>
      <p class="text-sm text-slate-400">These records supply Unity login and the local server descriptors. Player hangars are stored per account.</p>
      @if (message()) { <p role="status" class="rounded border border-slate-600 p-3">{{ message() }}</p> }
      <section class="rounded border border-slate-700 p-4 space-y-3"><h3 class="font-semibold">Languages</h3>
        <p class="text-sm text-slate-400">Enable and name installed client languages; choose one default.</p>
        @for (item of languages(); track item.code) {
          <button class="mr-2 text-sky-300 underline" (click)="language = { ...item }">{{ item.name }} ({{ item.code }})</button>
        }
        <div class="flex flex-wrap gap-2"><input aria-label="Language code" class="bg-slate-800 p-2" [(ngModel)]="language.code" placeholder="Code">
          <input aria-label="Language name" class="bg-slate-800 p-2" [(ngModel)]="language.name" placeholder="Name">
          <label><input type="checkbox" [(ngModel)]="language.enabled"> Enabled</label>
          <label><input type="checkbox" [(ngModel)]="language.isDefault"> Default</label>
          <button class="rounded bg-blue-600 px-3" (click)="saveLanguage()">Save language</button></div>
      </section>
      <section class="rounded border border-slate-700 p-4 space-y-3"><h3 class="font-semibold">Server list</h3>
        @for (item of servers(); track item.code) {
          <button class="mr-2 text-sky-300 underline" (click)="server = { ...item }">{{ item.instanceName }} ({{ item.code }})</button>
        }
        <div class="flex flex-wrap gap-2"><input aria-label="Server code" class="bg-slate-800 p-2" [(ngModel)]="server.code" placeholder="Code">
          <input aria-label="Instance name" class="bg-slate-800 p-2" [(ngModel)]="server.instanceName" placeholder="Instance name">
          <input aria-label="Target host" class="bg-slate-800 p-2" [(ngModel)]="server.host" placeholder="Host">
          <input aria-label="Map ID" class="w-24 bg-slate-800 p-2" type="number" [(ngModel)]="server.mapId">
          <select aria-label="Server status" class="bg-slate-800 p-2" [(ngModel)]="server.status"><option value="online">Online</option><option value="maintenance">Maintenance</option><option value="offline">Offline</option></select>
          <label><input type="checkbox" [(ngModel)]="server.enabled"> Enabled</label>
          <label><input type="checkbox" [(ngModel)]="server.isDefault"> Default</label>
          <button class="rounded bg-blue-600 px-3" (click)="saveServer()">Save server</button></div>
      </section>
      <section class="rounded border border-slate-700 p-4 space-y-3"><h3 class="font-semibold">Ship catalog</h3>
        @for (item of ships(); track item.shipId) {
          <button class="mr-2 text-sky-300 underline" (click)="ship = { ...item }">{{ item.name }} ({{ item.shipId }})</button>
        }
        <div class="flex flex-wrap gap-2"><input aria-label="Ship ID" class="bg-slate-800 p-2" [(ngModel)]="ship.shipId" placeholder="ship_id">
          <input aria-label="Ship name" class="bg-slate-800 p-2" [(ngModel)]="ship.name" placeholder="Name">
          <input aria-label="Model ID" class="w-24 bg-slate-800 p-2" type="number" [(ngModel)]="ship.modelId">
          <input aria-label="Icon path" class="bg-slate-800 p-2" [(ngModel)]="ship.iconPath" placeholder="/do_img/...">
          <label><input type="checkbox" [(ngModel)]="ship.enabled"> Enabled</label>
          <button class="rounded bg-blue-600 px-3" (click)="saveShip()">Save ship</button></div>
      </section>
      <section class="rounded border border-slate-700 p-4 space-y-3"><h3 class="font-semibold">Player hangars</h3>
        <p class="text-sm text-slate-400">Copy the account UUID from Identity. Opening a player shows assigned ships and slots.</p>
        <input aria-label="Account UUID" class="bg-slate-800 p-2" [(ngModel)]="accountId" placeholder="Account UUID">
        <button class="rounded bg-blue-600 px-3 py-2" (click)="loadHangars()">Load hangars</button>
        @for (item of hangars(); track item.slotNumber) { <button class="ml-2 text-sky-300 underline" (click)="assignment = { ...item }">Slot {{ item.slotNumber }}: {{ item.shipId }}</button> }
        <div class="flex flex-wrap gap-2"><input aria-label="Slot" class="w-20 bg-slate-800 p-2" type="number" [(ngModel)]="assignment.slotNumber">
          <select aria-label="Assigned ship" class="bg-slate-800 p-2" [(ngModel)]="assignment.shipId">
            @for (item of ships(); track item.shipId) { <option [value]="item.shipId">{{ item.name }}</option> }
          </select>
          <input aria-label="Hangar ID" class="bg-slate-800 p-2" [(ngModel)]="assignment.hangarId" placeholder="Six digit hangar ID">
          <button class="rounded bg-blue-600 px-3" (click)="saveHangar()">Save assignment</button></div>
      </section>
      <section class="rounded border border-slate-700 p-4 space-y-3"><h3 class="font-semibold">Localization overrides</h3>
        <p class="text-sm text-slate-400">Stored overrides are available for future client localization routes. This Unity build currently reads its installed asset bundle.</p>
        @for (item of overrides(); track item.languageCode + item.category + item.key) {
          <button class="mr-2 text-sky-300 underline" (click)="override = { ...item }">{{ item.languageCode }}/{{ item.category }}/{{ item.key }}</button>
        }
        <div class="flex flex-wrap gap-2"><input aria-label="Override language" class="bg-slate-800 p-2" [(ngModel)]="override.languageCode" placeholder="Language">
          <input aria-label="Override category" class="bg-slate-800 p-2" [(ngModel)]="override.category" placeholder="Category">
          <input aria-label="Override key" class="bg-slate-800 p-2" [(ngModel)]="override.key" placeholder="Key">
          <input aria-label="Override value" class="bg-slate-800 p-2" [(ngModel)]="override.value" placeholder="Value">
          <button class="rounded bg-blue-600 px-3" (click)="saveOverride()">Save override</button></div>
      </section>
    </div>`
})
export class CockpitComponent implements OnInit {
  private readonly api = inject(BackofficeApiService);
  readonly languages = signal<ClientLanguage[]>([]); readonly servers = signal<ClientServer[]>([]);
  readonly ships = signal<CatalogShip[]>([]); readonly hangars = signal<HangarAssignment[]>([]);
  readonly overrides = signal<LocaleOverride[]>([]); readonly message = signal('');
  accountId = '';
  language: ClientLanguage = { code: '', name: '', enabled: true, isDefault: false };
  server: ClientServer = { code: '', instanceName: '', host: '127.0.0.1', mapId: 1, status: 'online', enabled: true, isDefault: false };
  ship: CatalogShip = { shipId: '', name: '', modelId: 1, iconPath: '/do_img/', enabled: true };
  assignment: HangarAssignment = { accountId: '', slotNumber: 1, shipId: '', hangarId: '' };
  override: LocaleOverride = { languageCode: 'en', category: 'resource_breakingNews', key: '', value: '' };
  async ngOnInit(): Promise<void> { await this.reload(); }
  async reload(): Promise<void> {
    try {
      const [languages, servers, ships, overrides] = await Promise.all([
        firstValueFrom(this.api.listClientLanguages()), firstValueFrom(this.api.listClientServers()),
        firstValueFrom(this.api.listCatalogShips()), firstValueFrom(this.api.listLocaleOverrides())]);
      this.languages.set(languages); this.servers.set(servers); this.ships.set(ships); this.overrides.set(overrides);
    } catch { this.message.set('Could not load cockpit catalog.'); }
  }
  async saveLanguage(): Promise<void> { await this.save(() => firstValueFrom(this.api.putClientLanguage(this.language)), 'Language saved.'); }
  async saveServer(): Promise<void> { await this.save(() => firstValueFrom(this.api.putClientServer(this.server)), 'Server saved.'); }
  async saveShip(): Promise<void> { await this.save(() => firstValueFrom(this.api.putCatalogShip(this.ship)), 'Ship saved.'); }
  async saveOverride(): Promise<void> { await this.save(() => firstValueFrom(this.api.putLocaleOverride(this.override)), 'Override saved.'); }
  async loadHangars(): Promise<void> {
    try { this.hangars.set(await firstValueFrom(this.api.listHangarAssignments(this.accountId))); this.message.set('Hangars loaded.'); }
    catch { this.message.set('Could not load player hangars.'); }
  }
  async saveHangar(): Promise<void> {
    this.assignment.accountId = this.accountId;
    try {
      await firstValueFrom(this.api.putHangarAssignment(this.assignment));
      this.hangars.set(await firstValueFrom(this.api.listHangarAssignments(this.accountId)));
      this.message.set('Hangar saved.');
    } catch { this.message.set('Could not save this hangar. Check account UUID, ship and six digit hangar ID.'); }
  }
  private async save(action: () => Promise<void>, success: string): Promise<void> {
    try { await action(); await this.reload(); this.message.set(success); }
    catch { this.message.set('Save failed. Check the fields and permissions.'); }
  }
}
