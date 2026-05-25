import { TestBed } from '@angular/core/testing';
import { APP_SETTINGS } from '../config/app-settings';
import { RbacService } from './rbac.service';

function b64urlJson(value: object): string {
  return btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

describe('RbacService', () => {
  const storageKey = 'test-token';

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        RbacService,
        {
          provide: APP_SETTINGS,
          useValue: { apiBaseUrl: 'http://localhost', tokenStorageKey: storageKey }
        }
      ]
    });
  });

  it('reads client permissions from JWT', () => {
    const header = b64urlJson({ alg: 'none' });
    const payload = b64urlJson({
      resource_access: {
        'thdarkorbit-api': { roles: ['backoffice.identity.read'] }
      }
    });
    sessionStorage.setItem(storageKey, `${header}.${payload}.sig`);

    const rbac = TestBed.inject(RbacService);
    expect(rbac.hasPermission('backoffice.identity.read')).toBe(true);
    expect(rbac.hasPermission('backoffice.identity.delete')).toBe(false);
  });

  it('grants all permissions for emulator-admin', () => {
    const header = b64urlJson({ alg: 'none' });
    const payload = b64urlJson({ realm_access: { roles: ['emulator-admin'] } });
    sessionStorage.setItem(storageKey, `${header}.${payload}.sig`);

    const rbac = TestBed.inject(RbacService);
    expect(rbac.canAccessBackoffice()).toBe(true);
    expect(rbac.hasPermission('backoffice.cache.write')).toBe(true);
  });
});
