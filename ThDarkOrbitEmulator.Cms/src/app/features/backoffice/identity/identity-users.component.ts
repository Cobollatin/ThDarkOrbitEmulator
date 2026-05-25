import { Component, inject, OnInit, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { RbacService } from '../../../core/auth/rbac.service';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { BackofficeApiService, IdentityUserRow } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-identity-users',
  imports: [PageCardComponent, TranslateModule],
  templateUrl: './identity-users.component.html'
})
export class IdentityUsersComponent implements OnInit {
  private readonly api = inject(BackofficeApiService);
  readonly rbac = inject(RbacService);
  readonly users = signal<IdentityUserRow[]>([]);
  readonly count = signal<number | null>(null);
  readonly message = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.reload();
  }

  async reload(): Promise<void> {
    const [list, countRes] = await Promise.all([
      firstValueFrom(this.api.listIdentityUsers()),
      firstValueFrom(this.api.getIdentityUserCount())
    ]);
    this.users.set(list.users);
    this.count.set(countRes.count);
  }

  async deleteAll(): Promise<void> {
    if (!confirm('Delete ALL identity.accounts rows?')) {
      return;
    }

    const result = await firstValueFrom(this.api.deleteAllIdentityUsers());
    this.message.set(`Deleted ${result.deleted} account(s).`);
    await this.reload();
  }
}
