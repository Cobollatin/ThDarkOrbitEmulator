import { InjectionToken } from '@angular/core';

export type AppLocale = 'en' | 'es';

export interface AppSettings {
  apiBaseUrl: string;
  defaultLocale: AppLocale;
  supportedLocales: readonly AppLocale[];
  tokenStorageKey: string;
}

export const APP_SETTINGS = new InjectionToken<AppSettings>('APP_SETTINGS');
