import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { RbacService } from '../../../core/auth/rbac.service';

@Component({
  selector: 'app-backoffice-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslateModule],
  templateUrl: './backoffice-shell.component.html'
})
export class BackofficeShellComponent {
  readonly rbac = inject(RbacService);
}
