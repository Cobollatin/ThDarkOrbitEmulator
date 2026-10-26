import { inject, Injectable } from '@angular/core';
import { APP_SETTINGS } from '../config/app-settings';

export interface AccessTokenClaims {
  preferred_username?: string;
  realm_access?: { roles?: string[] };
  resource_access?: Record<string, { roles?: string[] }>;
}

@Injectable({ providedIn: 'root' })
export class RbacService {
  private readonly settings = inject(APP_SETTINGS);
  private readonly apiClientId = 'thdarkorbit-api';

  getClaims(): AccessTokenClaims | null {
    const token = sessionStorage.getItem(this.settings.tokenStorageKey);
    if (!token) {
      return null;
    }

    const parts = token.split('.');
    if (parts.length < 2) {
      return null;
    }

    try {
      const payload = parts[1].replace(/-/g, '+').replace(/_/g, '/');
      const json = decodeURIComponent(
        atob(payload)
          .split('')
          .map((c) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
          .join('')
      );
      return JSON.parse(json) as AccessTokenClaims;
    } catch {
      return null;
    }
  }

  getRealmRoles(): string[] {
    return this.getClaims()?.realm_access?.roles ?? [];
  }

  getPermissions(): string[] {
    const claims = this.getClaims();
    const clientRoles = claims?.resource_access?.[this.apiClientId]?.roles ?? [];
    if (this.getRealmRoles().includes('emulator-admin')) {
      return [
        'backoffice.identity.read',
        'backoffice.identity.write',
        'backoffice.identity.delete',
        'backoffice.keycloak.read',
        'backoffice.cache.write',
        'backoffice.ops.read',
        'backoffice.ops.write'
      ];
    }

    return clientRoles;
  }

  hasPermission(permission: string): boolean {
    return this.getPermissions().includes(permission);
  }

  canAccessBackoffice(): boolean {
    return this.getPermissions().some((p) => p.startsWith('backoffice.'));
  }
}
