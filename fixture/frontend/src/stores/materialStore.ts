import { defineStore } from 'pinia'
import { getMaterial, createMaterial } from '../api/materialApi'

export const useMaterialStore = defineStore('material', {
  state: () => ({ current: null as any }),
  actions: {
    async load(id: number) { this.current = await getMaterial(id) },
    async create(name: string, quantity: number) { return createMaterial(name, quantity) },
  },
})
