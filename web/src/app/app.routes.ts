import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './core/auth.service';
import { Shell } from './shell';

export const routes: Routes = [
  { path: 'sign-in', loadComponent: () => import('./pages/sign-in').then(m => m.SignIn), title: 'Sign in · Wings Git Manager' },
  {
    path: '', component: Shell, canActivate: [authGuard], canActivateChild: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./pages/dashboard').then(m => m.Dashboard), title: 'Dashboard · Wings Git Manager' },
      { path: 'repositories', loadComponent: () => import('./pages/repositories').then(m => m.Repositories), title: 'Repositories · Wings Git Manager' },
      { path: 'repositories/:id', loadComponent: () => import('./repo/repo-detail').then(m => m.RepoDetail), title: 'Repository · Wings Git Manager' },
      { path: 'sessions', loadComponent: () => import('./pages/sessions').then(m => m.AllSessions), title: 'AI sessions · Wings Git Manager' },
      { path: 'sync', loadComponent: () => import('./pages/sync').then(m => m.SyncMonitor), title: 'Sync monitor · Wings Git Manager' },
      { path: 'alerts', loadComponent: () => import('./pages/alerts').then(m => m.Alerts), title: 'Alerts · Wings Git Manager' },
      { path: 'system', loadComponent: () => import('./pages/system').then(m => m.SystemPage), title: 'System · Wings Git Manager' },
      { path: 'audit', loadComponent: () => import('./pages/audit').then(m => m.AuditPage), title: 'Audit log · Wings Git Manager' },
      {
        path: 'admin', canActivate: [adminGuard],
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'companies' },
          { path: 'companies', loadComponent: () => import('./admin/companies').then(m => m.Companies), title: 'Companies · Wings Git Manager' },
          { path: 'users', loadComponent: () => import('./admin/users').then(m => m.Users), title: 'Users · Wings Git Manager' },
          { path: 'tokens', loadComponent: () => import('./admin/tokens').then(m => m.Tokens), title: 'API tokens · Wings Git Manager' },
          { path: 'policies', loadComponent: () => import('./admin/policies').then(m => m.Policies), title: 'Policies · Wings Git Manager' },
        ],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
