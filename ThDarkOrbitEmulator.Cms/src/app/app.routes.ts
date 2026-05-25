import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadChildren: () =>
      import('./features/identity/identity.routes').then((m) => m.identityRoutes)
  },
  {
    path: '',
    loadChildren: () =>
      import('./features/backoffice/backoffice.routes').then((m) => m.backofficeRoutes)
  },
  { path: '**', redirectTo: '' }
];
