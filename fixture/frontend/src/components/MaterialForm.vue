<!-- Form for requesting a new material. -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useMaterialStore } from '../stores/materialStore'

const props = defineProps<{ defaultQuantity: number }>()
const emit = defineEmits<{ created: [id: number] }>()
const store = useMaterialStore()
const name = ref('')
const valid = computed(() => name.value.length > 0)
watch(name, () => store.touch?.())

async function submit() {
  emit('created', await store.create(name.value, props.defaultQuantity))
}
</script>

<template>
  <form @submit.prevent="submit"><input v-model="name" /><button :disabled="!valid">Create</button></form>
</template>
