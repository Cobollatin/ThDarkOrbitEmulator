import { Component, input } from '@angular/core';

@Component({
  selector: 'app-page-card',
  host: {
    class: 'block w-full flex justify-center'
  },
  template: `
    <article
      class="w-full rounded-xl border border-slate-700/80 bg-slate-900/90 p-6 shadow-xl shadow-black/30"
      [class.max-w-md]="!wide() && !full() && !title()"
      [class.max-w-3xl]="wide() && !full() && !title()"
      [class.max-w-none]="full() || !!title()"
    >
      <header class="mb-5 flex flex-wrap items-start justify-between gap-4 border-b border-slate-800/80 pb-4">
        <div>
          <h1 class="text-xl font-semibold tracking-tight text-slate-100">
            @if (title()) {
              {{ title() }}
            } @else {
              <ng-content select="[cardTitle]" />
            }
          </h1>
          <p class="mt-1 text-sm text-slate-400">
            @if (subtitle()) {
              {{ subtitle() }}
            } @else {
              <ng-content select="[cardSubtitle]" />
            }
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
  readonly full = input(false);
  readonly title = input<string>('');
  readonly subtitle = input<string>('');
}