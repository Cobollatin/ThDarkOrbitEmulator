import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { APP_SETTINGS } from '../../../core/config/app-settings';

export interface IdentityUserRow {
  id: string;
  username: string;
  email: string;
  gameUserId: number;
}

export interface KeycloakUserRow {
  id: string;
  username: string;
  email: string;
  enabled: boolean;
}

export interface BackofficeOverview {
  services: { name: string; role: string; status: string }[];
  links: { label: string; url: string }[];
}

@Injectable({ providedIn: 'root' })
export class BackofficeApiService {
  private readonly http = inject(HttpClient);
  private readonly settings = inject(APP_SETTINGS);

  private url(path: string): string {
    return `${this.settings.apiBaseUrl}/api/v1/backoffice/${path}`;
  }

  getPermissions(): Observable<{
    username: string;
    permissions: string[];
    realmRoles: string[];
    roles: string[];
  }> {
    return this.http.get<{
      username: string;
      permissions: string[];
      realmRoles: string[];
      roles: string[];
    }>(this.url('auth/permissions'));
  }

  listIdentityUsers(): Observable<{ users: IdentityUserRow[] }> {
    return this.http.get<{ users: IdentityUserRow[] }>(this.url('identity/users'));
  }

  getIdentityUserCount(): Observable<{ count: number }> {
    return this.http.get<{ count: number }>(this.url('identity/users/count'));
  }

  deleteAllIdentityUsers(): Observable<{ deleted: number }> {
    return this.http.delete<{ deleted: number }>(this.url('identity/users'));
  }

  listKeycloakUsers(): Observable<{ users: KeycloakUserRow[] }> {
    return this.http.get<{ users: KeycloakUserRow[] }>(this.url('keycloak/users'));
  }

  clearCache(): Observable<{ cleared: boolean }> {
    return this.http.post<{ cleared: boolean }>(this.url('operations/cache/clear'), {});
  }

  getOverview(): Observable<BackofficeOverview> {
    return this.http.get<BackofficeOverview>(this.url('operations/overview'));
  }
}
