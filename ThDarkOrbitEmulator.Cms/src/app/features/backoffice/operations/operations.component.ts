import { Component, inject, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { BackofficeApiService } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-operations',
  imports: [PageCardComponent, TranslateModule],
  templateUrl: './operations.component.html'
})
export class OperationsComponent {
  private readonly api = inject(BackofficeApiService);
  readonly message = signal<string | null>(null);

  async clearCache(): Promise<void> {
    if (!confirm('Clear all Redis keys (FLUSHALL)?')) {
      return;
    }

    await firstValueFrom(this.api.clearCache());
    this.message.set('backoffice.operations.cacheCleared');
  }
}
