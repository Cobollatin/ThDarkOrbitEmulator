import { Routes } from '@angular/router';
import { backofficeGuard, permissionGuard } from './auth/permission.guard';

export const backofficeRoutes: Routes = [
  {
    path: 'backoffice',
    canActivate: [backofficeGuard],
    loadComponent: () =>
      import('./shell/backoffice-shell.component').then((m) => m.BackofficeShellComponent),
    children: [
      { path: '', redirectTo: 'overview', pathMatch: 'full' },
      {
        path: 'overview',
        canActivate: [permissionGuard('backoffice.ops.read')],
        loadComponent: () =>
          import('./overview/overview.component').then((m) => m.OverviewComponent)
      },
      {
        path: 'identity',
        canActivate: [permissionGuard('backoffice.identity.read')],
        loadComponent: () =>
          import('./identity/identity-users.component').then((m) => m.IdentityUsersComponent)
      },
      {
        path: 'keycloak',
        canActivate: [permissionGuard('backoffice.keycloak.read')],
        loadComponent: () =>
          import('./keycloak/keycloak-users.component').then((m) => m.KeycloakUsersComponent)
      },
      {
        path: 'operations',
        canActivate: [permissionGuard('backoffice.cache.write')],
        loadComponent: () =>
          import('./operations/operations.component').then((m) => m.OperationsComponent)
      }
    ]
  }
];
