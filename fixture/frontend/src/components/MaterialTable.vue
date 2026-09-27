<script setup lang="ts">
// ARCH-002 violation: component calls HTTP directly instead of via store/api
import axios from 'axios'
import { onMounted, ref } from 'vue'
import MaterialForm from './MaterialForm.vue'

const rows = ref<any[]>([])
onMounted(async () => { rows.value = (await axios.get('/api/materials')).data })
</script>

<template>
  <MaterialForm :default-quantity="1" @created="rows.push($event)" />
  <table><tr v-for="r in rows" :key="r.id"><td>{{ r.name }}</td></tr></table>
</template>
