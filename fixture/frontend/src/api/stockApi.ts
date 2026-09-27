import { api, request } from './http'

/** Stock of one article, as the ERP reports it. */
export interface StockLevel { sku: string; quantity: number }

export type Sku = string

/** Reads stock and materials through the shared HTTP wrappers. */
export class StockClient {
  async level(sku: Sku): Promise<StockLevel> {
    return (await api.get(`/stock/${sku}`)).data
  }

  material(id: number) {
    return request('GET', `/materials/${id}`)
  }
}
