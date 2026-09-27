import { Component, OnInit } from '@angular/core'
import { StockService } from './stock.service'

/** Stock overview. */
@Component({ selector: 'app-stock', templateUrl: './stock.component.html' })
export class StockComponent implements OnInit {
  level = ''

  constructor(private stock: StockService) {}

  ngOnInit() {
    this.stock.level('A-1').subscribe(l => (this.level = l))
  }
}
