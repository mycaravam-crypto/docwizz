import axios from 'axios'

/** The backend client: every call goes below /api. */
export const api = axios.create({ baseURL: '/api' })

/** Calls the backend with any method; `path` is below /api. */
export async function request(method: string, path: string) {
  return (await fetch(`/api${path}`, { method })).json()
}
