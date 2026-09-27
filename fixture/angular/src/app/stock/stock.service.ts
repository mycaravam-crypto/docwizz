import { Injectable, inject } from '@angular/core'
import { HttpClient } from '@angular/common/http'

/** Stock levels from the backend. */
@Injectable({ providedIn: 'root' })
export class StockService {
  private http = inject(HttpClient)

  level(sku: string) {
    return this.http.get<string>(`/api/stock/${sku}`)
  }
}
