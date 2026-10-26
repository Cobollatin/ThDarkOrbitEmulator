import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { RbacService } from '../../../core/auth/rbac.service';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { BackofficeApiService, RuntimeVariable, RuntimeVariableChange } from '../data/backoffice-api.service';

@Component({
  selector: 'app-runtime-vars',
  imports: [FormsModule, PageCardComponent],
  templateUrl: './runtime-vars.component.html'
})
export class RuntimeVarsComponent implements OnInit {
  private readonly api = inject(BackofficeApiService);
  readonly canWrite = inject(RbacService).hasPermission('backoffice.ops.write');
  readonly categories = ['All', 'World', 'Movement', 'Combat', 'Spawns', 'Collectibles', 'Economy', 'ServerMesh'];
  readonly variables = signal<RuntimeVariable[]>([]);
  readonly history = signal<RuntimeVariableChange[]>([]);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly output = signal('');
  readonly hasMore = signal(false);
  category = 'All';
  search = '';
  command = '';
  reason = '';
  values: Record<string, string> = {};

  ngOnInit(): void { void this.refresh(); }

  filteredVariables(): RuntimeVariable[] {
    return this.variables().filter(variable =>
      (this.category === 'All' || variable.category === this.category) &&
      variable.key.toLowerCase().includes(this.search.trim().toLowerCase()));
  }

  async refresh(): Promise<void> {
    try {
      const [variables, history] = await Promise.all([
        firstValueFrom(this.api.listVariables()), firstValueFrom(this.api.getVariableHistory())
      ]);
      this.variables.set(variables);
      this.values = Object.fromEntries(variables.map(variable => [variable.key, variable.currentValue]));
      this.history.set(history);
      this.hasMore.set(history.length === 100);
      this.error.set('');
    } catch { this.error.set('Could not load runtime variables or change history.'); }
  }

  async moreHistory(): Promise<void> {
    const last = this.history().at(-1);
    if (!last || this.busy()) return;
    this.busy.set(true);
    try {
      const page = await firstValueFrom(this.api.getVariableHistory(last.changeId));
      this.history.update(rows => [...rows, ...page]);
      this.hasMore.set(page.length === 100);
    } catch { this.error.set('Could not load older changes.'); }
    finally { this.busy.set(false); }
  }

  useCommand(text: string): void { this.command = text; }

  async execute(): Promise<void> {
    const text = this.command.trim();
    if (!this.canWrite || this.busy() || !text) return;
    await this.mutate(async () => {
      const result = await firstValueFrom(this.api.executeAdminCommand(text));
      this.output.set(JSON.stringify(result, null, 2));
      this.notice.set('Command executed.');
    });
  }

  async save(variable: RuntimeVariable): Promise<void> {
    if (!this.canWrite || this.busy() || !this.requireReason()) return;
    await this.mutate(async () => {
      await firstValueFrom(this.api.setVariable(variable.key, this.values[variable.key], this.reason.trim()));
      this.notice.set(`${variable.key} applied live.`);
    });
  }

  async reset(variable: RuntimeVariable): Promise<void> {
    if (!this.canWrite || this.busy() || !this.requireReason()) return;
    await this.mutate(async () => {
      await firstValueFrom(this.api.resetVariable(variable.key, this.reason.trim()));
      this.notice.set(`${variable.key} reset to default.`);
    });
  }

  async rollback(change: RuntimeVariableChange): Promise<void> {
    if (!this.canWrite || this.busy() || !this.requireReason()) return;
    await this.mutate(async () => {
      await firstValueFrom(this.api.rollbackVariable(change.changeId, this.reason.trim()));
      this.notice.set(`Change #${change.changeId} rolled back.`);
    });
  }

  private requireReason(): boolean {
    if (this.reason.trim()) return true;
    this.error.set('Enter a reason before changing a variable.');
    return false;
  }

  private async mutate(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try { await action(); await this.refresh(); }
    catch (error: unknown) {
      const response = error as { status?: number; error?: { error?: string } };
      if (response.status === 409) await this.refresh();
      this.error.set(response.error?.error || 'Admin action failed. Check the value and permissions.');
    } finally { this.busy.set(false); }
  }
}
