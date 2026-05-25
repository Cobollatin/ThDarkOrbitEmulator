import { Component, inject } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { LocaleService } from '../i18n/locale.service';
import { AppLocale } from '../config/app-settings';

@Component({
  selector: 'app-locale-switcher',
  imports: [TranslateModule],
  template: `
    <label class="flex items-center gap-2 text-sm text-slate-400">
      <span class="sr-only">{{ 'locale.label' | translate }}</span>
      <select
        class="rounded-md border border-slate-600 bg-slate-900 px-2 py-1 text-slate-200"
        [value]="locale.activeLocale()"
        (change)="onLocaleChange($any($event.target).value)"
        [attr.aria-label]="'locale.label' | translate"
      >
        @for (code of locale.supportedLocales; track code) {
          <option [value]="code">{{ 'locale.' + code | translate }}</option>
        }
      </select>
    </label>
  `
})
export class LocaleSwitcherComponent {
  protected readonly locale = inject(LocaleService);

  onLocaleChange(value: AppLocale): void {
    void this.locale.setLocale(value);
  }
}
