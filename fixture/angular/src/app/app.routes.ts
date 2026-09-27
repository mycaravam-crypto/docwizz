import { Routes } from '@angular/router'
import { StockComponent } from './stock/stock.component'

export const routes: Routes = [
  { path: 'stock', component: StockComponent },
  { path: 'stock/:sku', loadComponent: () => import('./stock/item-row.component').then(m => m.ItemRowComponent) },
]
