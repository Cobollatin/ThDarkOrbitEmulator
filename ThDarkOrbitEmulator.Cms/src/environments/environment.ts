import { AppSettings } from '../app/core/config/app-settings';

export const environment: AppSettings & { production: boolean } = {
  production: false,
  apiBaseUrl: '/api/v1',
  defaultLocale: 'en',
  supportedLocales: ['en', 'es'],
  tokenStorageKey: 'cms_access_token'
};
