import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { APP_SETTINGS } from '../../../core/config/app-settings';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const settings = inject(APP_SETTINGS);
  const token = auth.getToken();
  const apiPrefix = settings.apiBaseUrl;

  const isIdentityAnonymous =
    req.url.includes(`${apiPrefix}/identity/login`) ||
    req.url.includes(`${apiPrefix}/identity/register`);

  if (!token || isIdentityAnonymous) {
    return next(req);
  }

  return next(
    req.clone({
      setHeaders: {
        Authorization: `Bearer ${token}`
      }
    })
  );
};
