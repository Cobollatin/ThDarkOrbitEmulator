import { Component, inject, OnInit, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { BackofficeApiService, KeycloakUserRow } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-keycloak-users',
  imports: [PageCardComponent, TranslateModule],
  templateUrl: './keycloak-users.component.html'
})
export class KeycloakUsersComponent implements OnInit {
  private readonly api = inject(BackofficeApiService);
  readonly users = signal<KeycloakUserRow[]>([]);

  async ngOnInit(): Promise<void> {
    const list = await firstValueFrom(this.api.listKeycloakUsers());
    this.users.set(list.users);
  }
}
