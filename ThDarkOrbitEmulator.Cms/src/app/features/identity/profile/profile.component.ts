import { Component, inject, OnInit, signal } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiClientError } from '../../../core/api/api-error';
import { ApiErrorMessagePipe } from '../../../shared/ui/api-error-message.pipe';
import { PageCardComponent } from '../../../shared/ui/page-card.component';
import { AuthService } from '../auth/auth.service';

@Component({
  selector: 'app-profile',
  imports: [TranslateModule, PageCardComponent, ApiErrorMessagePipe],
  templateUrl: './profile.component.html'
})
export class ProfileComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly title = inject(Title);
  private readonly translate = inject(TranslateService);

  readonly loading = signal(true);
  readonly errorCode = signal<string | null>(null);
  readonly profile = this.auth.profile;

  ngOnInit(): void {
    this.translate.get('auth.profile.title').subscribe((v) => this.title.setTitle(v));
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.errorCode.set(null);
    try {
      await this.auth.loadProfile();
    } catch (error) {
      this.errorCode.set(ApiClientError.fromHttp(error).code);
      this.auth.logout();
    } finally {
      this.loading.set(false);
    }
  }

  logout(): void {
    this.auth.logout();
  }
}
