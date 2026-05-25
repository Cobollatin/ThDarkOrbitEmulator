import { HttpClient } from '@angular/common/http';
import {
  EnvironmentProviders,
  inject,
  importProvidersFrom,
  makeEnvironmentProviders,
  provideAppInitializer
} from '@angular/core';
import { TranslateLoader, TranslateModule, TranslateService } from '@ngx-translate/core';
import { TranslateHttpLoader } from '@ngx-translate/http-loader';
import { firstValueFrom } from 'rxjs';
import { APP_SETTINGS } from '../config/app-settings';
import { LocaleService } from './locale.service';

export function createTranslateLoader(http: HttpClient): TranslateHttpLoader {
  return new TranslateHttpLoader(http, '/assets/i18n/', '.json');
}

export function provideI18n(): EnvironmentProviders {
  return makeEnvironmentProviders([
    importProvidersFrom(
      TranslateModule.forRoot({
        defaultLanguage: 'en',
        loader: {
          provide: TranslateLoader,
          useFactory: createTranslateLoader,
          deps: [HttpClient]
        }
      })
    ),
    provideAppInitializer(async () => {
      const locale = inject(LocaleService);
      const settings = inject(APP_SETTINGS);
      const translate = inject(TranslateService);
      await firstValueFrom(translate.use(settings.defaultLocale));
      await locale.init();
    })
  ]);
}
