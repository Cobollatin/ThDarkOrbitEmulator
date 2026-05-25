import { Component, inject, OnInit, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { BackofficeApiService, BackofficeOverview } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-overview',
  imports: [PageCardComponent, TranslateModule],
  templateUrl: './overview.component.html'
})
export class OverviewComponent implements OnInit {
  private readonly api = inject(BackofficeApiService);
  readonly overview = signal<BackofficeOverview | null>(null);
  readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.overview.set(await firstValueFrom(this.api.getOverview()));
    } catch {
      this.error.set('backoffice.error.load');
    }
  }
}
