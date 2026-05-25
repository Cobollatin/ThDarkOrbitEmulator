import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withViewTransitions } from '@angular/router';
import { environment } from '../environments/environment';
import { APP_SETTINGS } from './core/config/app-settings';
import { provideI18n } from './core/i18n/provide-i18n';
import { authInterceptor } from './features/identity/auth/auth.interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideRouter(routes, withComponentInputBinding(), withViewTransitions()),
    provideI18n(),
    { provide: APP_SETTINGS, useValue: environment }
  ]
};
