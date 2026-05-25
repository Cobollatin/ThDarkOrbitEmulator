import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { LocaleSwitcherComponent } from './locale-switcher.component';
import { AuthService } from '../../features/identity/auth/auth.service';
import { RbacService } from '../auth/rbac.service';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslateModule, LocaleSwitcherComponent],
  templateUrl: './app-shell.component.html'
})
export class AppShellComponent {
  protected readonly auth = inject(AuthService);
  protected readonly rbac = inject(RbacService);
}
