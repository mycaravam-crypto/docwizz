<!-- Form for requesting a new material. -->
<script setup lang="ts">
import { ref } from 'vue'
import { useMaterialStore } from '../stores/materialStore'

const props = defineProps<{ defaultQuantity: number }>()
const emit = defineEmits<{ created: [id: number] }>()
const store = useMaterialStore()
const name = ref('')

async function submit() {
  emit('created', await store.create(name.value, props.defaultQuantity))
}
</script>

<template>
  <form @submit.prevent="submit"><input v-model="name" /><button>Create</button></form>
</template>
