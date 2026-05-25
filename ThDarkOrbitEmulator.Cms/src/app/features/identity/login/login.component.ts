import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { ApiClientError } from '../../../core/api/api-error';
import { ApiErrorMessagePipe } from '../../../shared/ui/api-error-message.pipe';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { AuthService } from '../auth/auth.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink, TranslateModule, PageCardComponent, ApiErrorMessagePipe],
  templateUrl: './login.component.html'
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly fb = inject(FormBuilder);

  readonly errorCode = signal<string | null>(null);
  readonly loading = signal(false);

  readonly form = this.fb.nonNullable.group({
    username: ['', Validators.required],
    password: ['', Validators.required]
  });

  async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    this.errorCode.set(null);

    try {
      const { username, password } = this.form.getRawValue();
      await this.auth.login(username, password);
    } catch (error) {
      this.errorCode.set(ApiClientError.fromHttp(error).code);
    } finally {
      this.loading.set(false);
    }
  }
}
