import { HttpErrorResponse } from '@angular/common/http';
import { ApiErrorCode, ApiErrorCodeValue } from './api-error-code';

export interface ApiErrorBody {
  code: string;
  message: string;
}

/** Maps API error codes to ngx-translate keys (errors.identity.invalid_credentials). */
export function apiErrorTranslationKey(code: string): string {
  const normalized = code.replace(/\./g, '_');
  if (normalized.startsWith('common_') || normalized.startsWith('identity_')) {
    const [domain, ...rest] = normalized.split('_');
    return `errors.${domain}.${rest.join('_')}`;
  }

  return 'errors.common.unknown';
}

export class ApiClientError extends Error {
  readonly code: string;
  readonly status: number;
  readonly translationKey: string;

  constructor(code: string, status: number, message?: string) {
    super(message ?? code);
    this.name = 'ApiClientError';
    this.code = code;
    this.status = status;
    this.translationKey = apiErrorTranslationKey(code);
  }

  static fromHttp(error: unknown): ApiClientError {
    if (error instanceof ApiClientError) {
      return error;
    }

    if (error instanceof HttpErrorResponse) {
      const body = error.error as ApiErrorBody | null | undefined;
      if (body && typeof body.code === 'string') {
        return new ApiClientError(body.code, error.status, body.message);
      }

      if (error.status === 401) {
        return new ApiClientError(ApiErrorCode.IdentityUnauthorized, error.status);
      }

      if (error.status === 0) {
        return new ApiClientError(ApiErrorCode.IdentityNetwork, 0);
      }
    }

    return new ApiClientError(ApiErrorCode.CommonUnknown, 0);
  }

  static isKnownCode(code: string): code is ApiErrorCodeValue {
    return Object.values(ApiErrorCode).includes(code as ApiErrorCodeValue);
  }
}
