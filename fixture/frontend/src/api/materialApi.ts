import axios from 'axios'

export async function getMaterial(id: number) {
  return (await axios.get(`/api/materials/${id}`)).data
}

export async function createMaterial(name: string, quantity: number) {
  return (await axios.post('/api/materials', { name, quantity })).data
}

export async function getRates() {
  return (await fetch('https://rates.example.org/latest')).json()
}
