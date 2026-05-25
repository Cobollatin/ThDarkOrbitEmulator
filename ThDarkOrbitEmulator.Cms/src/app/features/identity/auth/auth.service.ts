import { inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClientError } from '../../../core/api/api-error';
import { ApiErrorCode } from '../../../core/api/api-error-code';
import { APP_SETTINGS } from '../../../core/config/app-settings';
import { IdentityApiService } from '../data/identity-api.service';
import { UserProfile } from './auth.models';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(IdentityApiService);
  private readonly router = inject(Router);
  private readonly settings = inject(APP_SETTINGS);
  private readonly profileSignal = signal<UserProfile | null>(null);

  readonly profile = this.profileSignal.asReadonly();

  isAuthenticated(): boolean {
    return !!this.getToken();
  }

  getToken(): string | null {
    return sessionStorage.getItem(this.settings.tokenStorageKey);
  }

  async login(username: string, password: string): Promise<void> {
    try {
      const response = await firstValueFrom(this.api.login({ username, password }));

      if (!response.authenticated || !response.token) {
        throw new ApiClientError(ApiErrorCode.IdentityInvalidCredentials, 401);
      }

      sessionStorage.setItem(this.settings.tokenStorageKey, response.token);
      await this.loadProfile();
      await this.router.navigate(['/']);
    } catch (error) {
      throw ApiClientError.fromHttp(error);
    }
  }

  async register(username: string, password: string, email?: string): Promise<void> {
    try {
      const response = await firstValueFrom(
        this.api.register({ username, password, email: email ?? null })
      );

      if (!response.created) {
        throw new ApiClientError(ApiErrorCode.CommonUnknown, 500);
      }

      await this.login(username, password);
    } catch (error) {
      sessionStorage.removeItem(this.settings.tokenStorageKey);
      this.profileSignal.set(null);
      throw ApiClientError.fromHttp(error);
    }
  }

  async loadProfile(): Promise<UserProfile | null> {
    const token = this.getToken();
    if (!token) {
      this.profileSignal.set(null);
      return null;
    }

    try {
      const profile = await firstValueFrom(this.api.getProfile());
      this.profileSignal.set(profile);
      return profile;
    } catch (error) {
      sessionStorage.removeItem(this.settings.tokenStorageKey);
      this.profileSignal.set(null);
      throw ApiClientError.fromHttp(error);
    }
  }

  logout(): void {
    sessionStorage.removeItem(this.settings.tokenStorageKey);
    this.profileSignal.set(null);
    void this.router.navigate(['/login']);
  }
}
