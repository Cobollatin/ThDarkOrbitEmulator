import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { ActivityTrackingEvent, BackofficeApiService } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-operations',
  imports: [FormsModule, PageCardComponent, TranslateModule],
  templateUrl: './operations.component.html'
})
export class OperationsComponent {
  private readonly api = inject(BackofficeApiService);
  readonly message = signal<string | null>(null);
  readonly activityEvents = signal<ActivityTrackingEvent[]>([]);

  subjectId = '';
  reason = '';
  sessionId = '';
  eventType = 'manual.note';
  eventSource = 'operator';
  eventDetails = '';

  async clearCache(): Promise<void> {
    if (!confirm('Clear all Redis keys (FLUSHALL)?')) {
      return;
    }

    await firstValueFrom(this.api.clearCache());
    this.message.set('backoffice.operations.cacheCleared');
  }

  async startTracking(): Promise<void> {
    const response = await firstValueFrom(
      this.api.startActivityTrackingSession({
        subjectId: this.subjectId.trim(),
        reason: this.reason.trim()
      })
    );

    this.sessionId = response.session.id;
    this.activityEvents.set([]);
    this.message.set('backoffice.operations.trackingStarted');
  }

  async appendActivityEvent(): Promise<void> {
    const response = await firstValueFrom(
      this.api.appendActivityTrackingEvent(this.sessionId.trim(), {
        type: this.eventType.trim(),
        source: this.eventSource.trim(),
        details: this.eventDetails.trim()
      })
    );

    this.activityEvents.update((events) => [...events, response.event]);
    this.eventDetails = '';
    this.message.set('backoffice.operations.eventRecorded');
  }

  async refreshActivitySnapshot(): Promise<void> {
    const snapshot = await firstValueFrom(this.api.getActivityTrackingSnapshot(this.sessionId.trim()));
    this.activityEvents.set(snapshot.events);
  }
}
