import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiClientError } from '../../../core/api/api-error';
import { ApiErrorMessagePipe } from '../../../shared/ui/api-error-message.pipe';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { AuthService } from '../auth/auth.service';

@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink, TranslateModule, PageCardComponent, ApiErrorMessagePipe],
  templateUrl: './register.component.html'
})
export class RegisterComponent {
  private readonly auth = inject(AuthService);
  private readonly fb = inject(FormBuilder);
  private readonly translate = inject(TranslateService);

  readonly errorCode = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly loading = signal(false);

  readonly form = this.fb.nonNullable.group({
    username: ['', [Validators.required, Validators.minLength(3)]],
    email: ['', Validators.email],
    password: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', Validators.required]
  });

  async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { username, email, password, confirmPassword } = this.form.getRawValue();
    if (password !== confirmPassword) {
      this.errorCode.set(null);
      this.errorMessage.set(this.translate.instant('auth.register.passwordMismatch'));
      return;
    }

    this.loading.set(true);
    this.errorCode.set(null);
    this.errorMessage.set(null);

    try {
      await this.auth.register(username, password, email || undefined);
    } catch (error) {
      this.errorCode.set(ApiClientError.fromHttp(error).code);
    } finally {
      this.loading.set(false);
    }
  }
}
