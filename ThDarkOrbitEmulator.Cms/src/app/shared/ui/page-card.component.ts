import { Component, input } from '@angular/core';

@Component({
  selector: 'app-page-card',
  template: `
    <article
      class="w-full rounded-xl border border-slate-700 bg-slate-900/80 p-6 shadow-xl shadow-black/20"
      [class.max-w-md]="!wide()"
      [class.max-w-2xl]="wide()"
    >
      <header class="mb-5 flex items-start justify-between gap-4">
        <div>
          <h1 class="text-xl font-semibold text-slate-100">
            <ng-content select="[cardTitle]" />
          </h1>
          <p class="mt-1 text-sm text-slate-400">
            <ng-content select="[cardSubtitle]" />
          </p>
        </div>
        <div class="shrink-0">
          <ng-content select="[cardActions]" />
        </div>
      </header>
      <ng-content />
    </article>
  `
})
export class PageCardComponent {
  readonly wide = input(false);
}
