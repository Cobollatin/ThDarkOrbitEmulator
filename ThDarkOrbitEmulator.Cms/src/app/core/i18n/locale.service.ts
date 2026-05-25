import { DOCUMENT } from '@angular/common';
import { inject, Injectable, signal } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { APP_SETTINGS, AppLocale } from '../config/app-settings';

const LOCALE_STORAGE_KEY = 'cms_locale';

@Injectable({ providedIn: 'root' })
export class LocaleService {
  private readonly translate = inject(TranslateService);
  private readonly document = inject(DOCUMENT);
  private readonly settings = inject(APP_SETTINGS);

  private readonly activeLocaleSignal = signal<AppLocale>(this.settings.defaultLocale);

  readonly activeLocale = this.activeLocaleSignal.asReadonly();
  readonly supportedLocales = this.settings.supportedLocales;

  async init(): Promise<void> {
    const stored = this.readStoredLocale();
    const locale = stored ?? this.settings.defaultLocale;
    await this.setLocale(locale, false);
  }

  async setLocale(locale: AppLocale, persist = true): Promise<void> {
    if (!this.supportedLocales.includes(locale)) {
      return;
    }

    await this.translate.use(locale);
    this.activeLocaleSignal.set(locale);
    this.document.documentElement.lang = locale;

    if (persist) {
      localStorage.setItem(LOCALE_STORAGE_KEY, locale);
    }
  }

  private readStoredLocale(): AppLocale | null {
    const stored = localStorage.getItem(LOCALE_STORAGE_KEY);
    if (stored && this.supportedLocales.includes(stored as AppLocale)) {
      return stored as AppLocale;
    }

    return null;
  }
}
