import { Pipe, PipeTransform, inject } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { apiErrorTranslationKey } from '../../core/api/api-error';

@Pipe({
  name: 'apiErrorMessage',
  pure: false
})
export class ApiErrorMessagePipe implements PipeTransform {
  private readonly translate = inject(TranslateService);

  transform(code: string | null | undefined): string {
    if (!code) {
      return '';
    }

    const key = apiErrorTranslationKey(code);
    const translated = this.translate.instant(key);
    return translated === key ? this.translate.instant('errors.common.unknown') : translated;
  }
}
