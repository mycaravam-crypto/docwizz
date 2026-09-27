import { Component, EventEmitter, Input, Output } from '@angular/core'

/** One stock row. */
@Component({ selector: 'app-item-row', template: '<span>{{ sku }}</span>' })
export class ItemRowComponent {
  @Input() sku!: string
  @Output() picked = new EventEmitter<string>()
}
