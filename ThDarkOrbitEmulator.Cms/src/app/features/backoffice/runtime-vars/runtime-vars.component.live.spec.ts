import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { APP_SETTINGS } from '../../../core/config/app-settings';
import { RbacService } from '../../../core/auth/rbac.service';
import { authInterceptor } from '../../identity/auth/auth.interceptor';
import { RuntimeVarsComponent } from './runtime-vars.component';

// World-41: drives the real component, BackofficeApiService, auth interceptor and HttpClient against a live host.
// Requests go through jsdom's XMLHttpRequest, so cross-origin CORS applies as in a browser. Skipped unless
// CMS_LIVE_API_BASE (for example http://127.0.0.1:5000/api/v1) is set by the Explicit
// ServerManagementCliLiveSocketTests test, which also watches a WorldServer TCP client on another replica.
const liveBase = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env?.['CMS_LIVE_API_BASE'];
const key = 'world.station.peace_radius';
const pause = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

describe.skipIf(!liveBase)('RuntimeVarsComponent against a live backoffice host', () => {
  beforeEach(async () => {
    sessionStorage.setItem('cms_access_token', 'world-41-live-token');
    await TestBed.configureTestingModule({ imports: [RuntimeVarsComponent], providers: [
      provideHttpClient(withInterceptors([authInterceptor])), provideRouter([]),
      { provide: APP_SETTINGS, useValue: { apiBaseUrl: liveBase, defaultLocale: 'en', supportedLocales: ['en'], tokenStorageKey: 'cms_access_token' } },
      { provide: RbacService, useValue: { hasPermission: () => true } }
    ] }).compileComponents();
  });

  it('applies the peace radius live from the editor row, then rolls it back from the history row', async () => {
    const fixture = TestBed.createComponent(RuntimeVarsComponent);
    const element = fixture.nativeElement as HTMLElement;
    const render = async () => { for (let i = 0; i < 50; i++) { fixture.detectChanges(); await fixture.whenStable(); await pause(20); } };
    const type = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event('input')); };
    const row = (text: string, table: number) => Array.from(element.querySelectorAll('table')[table].querySelectorAll('tbody tr'))
      .find(tr => tr.textContent?.includes(text)) as HTMLTableRowElement;
    const button = (tr: HTMLElement, label: string) => Array.from(tr.querySelectorAll('button'))
      .find(b => b.textContent?.includes(label)) as HTMLButtonElement;
    await render();

    const editorRow = row(key, 0);
    expect(editorRow, element.textContent?.slice(0, 400)).toBeTruthy();
    expect(editorRow.textContent).toContain('1000');
    type(element.querySelector('input[placeholder="Required for changes"]') as HTMLInputElement, 'World-41 CMS live check');
    type(editorRow.querySelector(`input[aria-label="Value for ${key}"]`) as HTMLInputElement, '500');
    await render();
    button(editorRow, 'Save / Apply Live').click();
    await render();
    expect(element.querySelector('[role="status"]')?.textContent).toContain(`${key} applied live.`);
    const setRow = row('1000 → 500', 1);
    expect(setRow.textContent).toContain('AdminCms');
    expect(setRow.textContent).toContain('mesh-node-a');

    // Let the WorldServer client's one-second idle poll on the other replica observe the set.
    await pause(1500);
    button(setRow, 'Rollback').click();
    await render();
    expect(element.querySelector('[role="status"]')?.textContent).toContain('rolled back.');
    expect(row('500 → 1000', 1).textContent).toContain('AdminCms');
    expect((row(key, 0).querySelector('input') as HTMLInputElement).value).toBe('1000');
    button(row('1000 → 500', 1), 'Rollback').click();
    await render();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Rollback conflict:');
    expect((row(key, 0).querySelector('input') as HTMLInputElement).value).toBe('1000');
    expect(row('500 → 1000', 1).textContent).toContain('AdminCms');
    fixture.destroy();
  }, 30000);
});
