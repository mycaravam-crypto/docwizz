import { createRouter, createWebHistory } from 'vue-router'
import MaterialTable from '../components/MaterialTable.vue'

export default createRouter({
  history: createWebHistory(),
  routes: [{ path: '/materials', component: MaterialTable }],
})
