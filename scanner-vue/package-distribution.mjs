import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

// Run only while producing the artifact, after npm ci. Consumers need only Node.
const root = dirname(fileURLToPath(import.meta.url))
const modules = join(root, 'node_modules')
for (const file of ['typescript', '@vue/compiler-sfc', 'web-tree-sitter', 'tree-sitter-java/tree-sitter-java.wasm']) {
  if (!existsSync(join(modules, file))) throw new Error(`missing ${file}: run npm ci before packaging`)
}
const dist = join(root, 'dist')
rmSync(dist, { recursive: true, force: true })
mkdirSync(dist, { recursive: true })
for (const name of ['index.mjs', 'java.mjs', 'package.json', 'package-lock.json'])
  cpSync(join(root, name), join(dist, name))
cpSync(modules, join(dist, 'node_modules'), { recursive: true })
console.log('staged scanner-vue/dist')
