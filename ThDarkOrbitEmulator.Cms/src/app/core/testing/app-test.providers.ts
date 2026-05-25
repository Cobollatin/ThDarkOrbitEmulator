import { provideHttpClient } from '@angular/common/http';
import { EnvironmentProviders, importProvidersFrom, Provider } from '@angular/core';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { environment } from '../../../environments/environment';
import { APP_SETTINGS } from '../config/app-settings';

/** Shared providers for component/unit tests. */
export function provideAppTestConfig(): Array<Provider | EnvironmentProviders> {
  return [
    provideNoopAnimations(),
    provideHttpClient(),
    provideRouter([]),
    { provide: APP_SETTINGS, useValue: environment },
    importProvidersFrom(TranslateModule.forRoot())
  ];
}
