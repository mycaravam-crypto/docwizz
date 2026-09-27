import { createRouter, createMemoryHistory } from 'vue-router'
import MaterialTable from '../components/MaterialTable.vue'

// A test router with a stub route that shares the real route's path.
export const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/materials', component: { template: '<div/>' } }] })
export const table = MaterialTable
