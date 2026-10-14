import { cpSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { basename, dirname, join } from 'node:path'

// Stages index.mjs, java.mjs and their npm ci'd dependencies in dist/ for the .NET tool package (DocWizz.csproj runs
// this on pack). Consumers need only Node.
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
// .bin holds CLI links that would point back into this checkout; the scanners never run them.
cpSync(modules, join(dist, 'node_modules'), { recursive: true, verbatimSymlinks: true,
  filter: src => basename(src) !== '.bin' })
console.log('staged scanner-vue/dist')
