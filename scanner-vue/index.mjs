// Scans .vue/.ts files into docwizz model nodes/edges.
// usage: node index.mjs <root>   (stdin: absolute file paths, one per line; stdout: {nodes, edges})
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import ts from 'typescript'
import { parse as parseSfc } from '@vue/compiler-sfc'

const root = path.resolve(process.argv[2] ?? '.')
const files = fs.readFileSync(0, 'utf8').split('\n').map(s => s.trim()).filter(Boolean)
const nodes = []
const edges = []
const rel = f => path.relative(root, f).split(path.sep).join('/')
const fileId = f => (f.endsWith('.vue') ? 'vue:' : 'ts:') + rel(f)
const hash = s => crypto.createHash('sha256').update(s).digest('hex').slice(0, 12)
const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
const edge = (from, to, kind, label) => edges.push(label ? { from, to, kind, label } : { from, to, kind })

const DECISIONS = new Set([
  ts.SyntaxKind.IfStatement, ts.SyntaxKind.ConditionalExpression, ts.SyntaxKind.ForStatement,
  ts.SyntaxKind.ForInStatement, ts.SyntaxKind.ForOfStatement, ts.SyntaxKind.WhileStatement,
  ts.SyntaxKind.DoStatement, ts.SyntaxKind.CatchClause, ts.SyntaxKind.CaseClause,
])
const LOGICAL = new Set([
  ts.SyntaxKind.AmpersandAmpersandToken, ts.SyntaxKind.BarBarToken, ts.SyntaxKind.QuestionQuestionToken,
])

function complexity(node) {
  let n = 1
  const walk = x => {
    if (DECISIONS.has(x.kind) || (ts.isBinaryExpression(x) && LOGICAL.has(x.operatorToken.kind))) n++
    ts.forEachChild(x, walk)
  }
  ts.forEachChild(node, walk)
  return n
}

// JSDoc → the same XML shape C# docs use, so the analyzer treats both alike.
function jsDoc(node, extraParams = []) {
  const docs = ts.getJSDocCommentsAndTags(node).filter(ts.isJSDoc)
  if (!docs.length && !extraParams.length) return null
  const parts = []
  const summary = docs.map(d => ts.getTextOfJSDocComment(d.comment) ?? '').join(' ').trim()
  if (summary) parts.push(`<summary>${esc(summary)}</summary>`)
  for (const d of docs)
    for (const t of d.tags ?? []) {
      const text = esc(ts.getTextOfJSDocComment(t.comment) ?? '')
      if (ts.isJSDocParameterTag(t)) parts.push(`<param name="${esc(t.name.getText())}">${text}</param>`)
      else if (t.tagName.text === 'returns' || t.tagName.text === 'return') parts.push(`<returns>${text}</returns>`)
      else if (t.tagName.text === 'throws') parts.push(`<exception>${text}</exception>`)
    }
  parts.push(...extraParams)
  return parts.length ? `<member>${parts.join('')}</member>` : null
}

function resolveImport(from, spec) {
  let base
  if (spec.startsWith('.')) base = path.resolve(path.dirname(from), spec)
  else if (spec.startsWith('@/')) {
    // ponytail: '@/' assumed to be <nearest package.json dir>/src; read tsconfig paths if a repo differs
    let dir = path.dirname(from)
    while (dir !== path.dirname(dir) && !fs.existsSync(path.join(dir, 'package.json'))) dir = path.dirname(dir)
    base = path.join(dir, 'src', spec.slice(2))
  } else return null
  return [base, base + '.ts', base + '.vue', path.join(base, 'index.ts')].find(f => fs.existsSync(f) && fs.statSync(f).isFile()) ?? null
}

// `/api/x/${id}?q=${n}` → '/api/x/{}'
function urlOf(arg) {
  if (!arg) return null
  let url
  if (ts.isStringLiteral(arg) || ts.isNoSubstitutionTemplateLiteral(arg)) url = arg.text
  else if (ts.isTemplateExpression(arg)) url = arg.head.text + arg.templateSpans.map(s => '{}' + s.literal.text).join('')
  else return null
  return url.split('?')[0]
}

const VERBS = new Set(['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'])
const joinUrl = (base, url) => !base || url.includes('://') ? url : base.replace(/\/+$/, '') + '/' + url.replace(/^\/+/, '')

// fetch(url, { method }), axios.get(url), and through wrappers (ctx): an axios.create({ baseURL }) instance's
// api.get(url), or a helper like request('POST', url) that passes its parameters on to one of those.
// `param` marks a URL/method that is one of the enclosing helper's parameters instead of a literal.
function httpCall(call, ctx, param = () => null) {
  const callee = call.expression
  const [first, second] = call.arguments
  if (ts.isIdentifier(callee) && callee.text === 'fetch') {
    let method = 'GET'
    if (second && ts.isObjectLiteralExpression(second))
      for (const p of second.properties) {
        if (p.name?.getText() !== 'method') continue
        const v = ts.isShorthandPropertyAssignment(p) ? p.name : p.initializer
        if (v && ts.isStringLiteralLike(v)) method = v.text.toUpperCase()
        else if (v && param(v) !== null) method = { param: param(v).index }
      }
    return urlOrParam(first, param, method)
  }
  if (ts.isPropertyAccessExpression(callee) && ts.isIdentifier(callee.expression)) {
    const obj = callee.expression.text
    const base = obj === 'axios' ? '' : ctx.instances.get(obj)
    const method = callee.name.text.toUpperCase()
    if (base !== undefined && VERBS.has(method)) {
      const r = urlOrParam(first, param, method)
      return r && (typeof r.url === 'string' ? { ...r, url: joinUrl(base, r.url) } : { ...r, url: { ...r.url, prefix: joinUrl(base, r.url.prefix) } })
    }
  }
  const helper = ts.isIdentifier(callee) && ctx.helpers.get(callee.text)
  if (helper) {
    const at = i => call.arguments[i]
    const m = typeof helper.method === 'string' ? helper.method : at(helper.method.param)
    const method = typeof m === 'string' ? m : m && ts.isStringLiteralLike(m) ? m.text.toUpperCase() : 'GET'
    const url = urlOf(at(helper.url.param))
    return url && VERBS.has(method) && { method, url: joinUrl(helper.url.prefix, url) }
  }
  return null
}

// A literal URL, or one built from a helper parameter: `url` / `/api${url}` → { param, prefix }.
function urlOrParam(arg, param, method) {
  if (arg && param(arg)) return { method, url: { param: param(arg).index, prefix: '' } }
  // ponytail: `/api/x/${id}` (prefix ends in '/') is a path segment, `/api${path}` a URL parameter; naive but cheap
  if (arg && ts.isTemplateExpression(arg) && arg.templateSpans.length === 1 && !arg.templateSpans[0].literal.text
      && !arg.head.text.endsWith('/') && param(arg.templateSpans[0].expression))
    return { method, url: { param: param(arg.templateSpans[0].expression).index, prefix: arg.head.text } }
  const url = urlOf(arg)
  return url && { method, url }
}

// Pre-pass over every file: axios instances and request helpers, and what each file exports, so wrappers resolve
// across imports. instances: local name → baseURL; helpers: local name → { url: { param, prefix }, method }.
const wrappers = new Map()
const wrapperCalls = new Set() // `file:pos` of the call inside a helper: its URL is the helper's parameter, not a call site
function scriptOf(file) {
  const src = fs.readFileSync(file, 'utf8')
  if (!file.endsWith('.vue')) return { sf: ts.createSourceFile(file, src, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS), lineOffset: 0 }
  const { descriptor } = parseSfc(src, { filename: file })
  const block = descriptor.scriptSetup ?? descriptor.script
  return block && { sf: ts.createSourceFile(file, block.content, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS), lineOffset: block.loc.start.line - 1 }
}
function wrapperContext(file, imports) {
  const own = wrappers.get(file) ?? { instances: new Map(), helpers: new Map(), exports: new Map() }
  const ctx = { instances: new Map(own.instances), helpers: new Map(own.helpers) }
  for (const [local, { target, name }] of imports) {
    const w = wrappers.get(target)
    const as = w?.exports.get(name)
    if (as === undefined) continue
    if (w.instances.has(as)) ctx.instances.set(local, w.instances.get(as))
    if (w.helpers.has(as)) ctx.helpers.set(local, w.helpers.get(as))
  }
  return ctx
}
function scanWrappers(files) {
  const parsed = files.map(file => { try { return [file, scriptOf(file)?.sf] } catch { return [file, null] } }).filter(([, sf]) => sf)
  const isCreate = e => e && ts.isCallExpression(e) && e.expression.getText() === 'axios.create'
  const baseOf = call => {
    const o = call.arguments[0]
    const p = o && ts.isObjectLiteralExpression(o) && o.properties.find(p => ts.isPropertyAssignment(p) && p.name.getText() === 'baseURL')
    return p && ts.isStringLiteralLike(p.initializer) ? p.initializer.text : ''
  }
  for (const [file, sf] of parsed) {
    const w = { instances: new Map(), helpers: new Map(), exports: new Map() }
    for (const st of sf.statements) {
      const exported = ts.canHaveModifiers(st) && ts.getModifiers(st)?.some(m => m.kind === ts.SyntaxKind.ExportKeyword)
      if (ts.isVariableStatement(st))
        for (const d of st.declarationList.declarations)
          if (ts.isIdentifier(d.name)) {
            if (isCreate(d.initializer)) w.instances.set(d.name.text, baseOf(d.initializer))
            if (exported) w.exports.set(d.name.text, d.name.text)
          }
      if (ts.isFunctionDeclaration(st) && st.name && exported) w.exports.set(st.name.text, st.name.text)
      if (ts.isExportAssignment(st)) {
        if (ts.isIdentifier(st.expression)) w.exports.set('default', st.expression.text)
        else if (isCreate(st.expression)) { w.instances.set('*default', baseOf(st.expression)); w.exports.set('default', '*default') }
      }
      if (ts.isExportDeclaration(st) && !st.moduleSpecifier && st.exportClause && ts.isNamedExports(st.exportClause))
        for (const el of st.exportClause.elements) w.exports.set(el.name.text, (el.propertyName ?? el.name).text)
    }
    wrappers.set(file, w)
  }
  // Helpers second, so they can wrap an instance from another file.
  for (const [file, sf] of parsed) {
    const ctx = wrapperContext(file, importsOf(file, sf, false))
    const w = wrappers.get(file)
    for (const st of sf.statements) {
      let name, fn
      if (ts.isFunctionDeclaration(st) && st.name && st.body) [name, fn] = [st.name.text, st]
      else if (ts.isVariableStatement(st))
        for (const d of st.declarationList.declarations)
          if (ts.isIdentifier(d.name) && d.initializer && (ts.isArrowFunction(d.initializer) || ts.isFunctionExpression(d.initializer)))
            [name, fn] = [d.name.text, d.initializer]
      if (!fn) continue
      const params = fn.parameters.map(p => p.name.getText(sf))
      const param = e => ts.isIdentifier(e) && params.includes(e.text) ? { index: params.indexOf(e.text) } : null
      const find = n => {
        const h = ts.isCallExpression(n) && httpCall(n, ctx, param)
        if (h && typeof h.url === 'object') { wrapperCalls.add(`${file}:${n.pos}`); return h }
        return ts.forEachChild(n, find)
      }
      const h = find(fn.body ?? fn)
      if (h) w.helpers.set(name, { url: h.url, method: h.method })
    }
  }
}

// Walks one script: imports, exported functions/stores, calls, HTTP, props/emits, routes.
// Local name → { target file, imported name } for the repo's own modules; `owner` also gets `imports` edges.
function importsOf(file, sf, owner) {
  const imports = new Map()
  for (const st of sf.statements) {
    if (!ts.isImportDeclaration(st) || st.importClause?.isTypeOnly) continue
    const target = resolveImport(file, st.moduleSpecifier.text)
    if (!target) continue
    if (owner) edge(owner, fileId(target), 'imports')
    const c = st.importClause
    if (c?.name) imports.set(c.name.text, { target, name: 'default' })
    if (c?.namedBindings && ts.isNamedImports(c.namedBindings))
      for (const el of c.namedBindings.elements)
        if (!el.isTypeOnly) imports.set(el.name.text, { target, name: (el.propertyName ?? el.name).text })
  }
  return imports
}

const LIFECYCLE = new Set(['onBeforeMount', 'onMounted', 'onBeforeUpdate', 'onUpdated', 'onBeforeUnmount', 'onUnmounted',
  'onErrorCaptured', 'onActivated', 'onDeactivated', 'onServerPrefetch'])
const TYPE_KINDS = [[ts.isClassDeclaration, 'class'], [ts.isInterfaceDeclaration, 'interface'], [ts.isTypeAliasDeclaration, 'type'], [ts.isEnumDeclaration, 'enum']]

function scanScript(file, sf, owner, lineOffset, component) {
  const imports = importsOf(file, sf, owner)
  const ctx = wrapperContext(file, imports)

  const line = n => sf.getLineAndCharacterOfPosition(n.getStart(sf)).line + 1 + lineOffset
  const endLine = n => sf.getLineAndCharacterOfPosition(n.getEnd()).line + 1 + lineOffset
  const exported = n => ts.getCombinedModifierFlags(n) & ts.ModifierFlags.Export

  // Exported functions, arrow consts, stores, classes (and their public methods), interfaces, types and enums
  // become their own nodes; functions and methods own their calls.
  const owners = new Map() // AST node → owner id
  for (const st of sf.statements) {
    const typeKind = TYPE_KINDS.find(([is]) => is(st))?.[1]
    if (typeKind && st.name && exported(st)) {
      const id = `${fileId(file)}#${st.name.text}`
      nodes.push({
        id, kind: typeKind, name: st.name.text, file: rel(file), line: line(st), endLine: endLine(st), visibility: 'public',
        doc: jsDoc(st), hash: hash(st.getText(sf)),
      })
      edge(owner, id, 'contains')
      if (typeKind === 'class') {
        owners.set(st, id)
        for (const m of st.members) {
          if (!(ts.isMethodDeclaration(m) || ts.isConstructorDeclaration(m)) || !m.body) continue
          const hidden = ts.getCombinedModifierFlags(m) & (ts.ModifierFlags.Private | ts.ModifierFlags.Protected) || (m.name && ts.isPrivateIdentifier(m.name))
          const name = ts.isConstructorDeclaration(m) ? 'constructor' : m.name.getText(sf)
          const mid = `${id}.${name}`
          nodes.push({
            id: mid, kind: ts.isConstructorDeclaration(m) ? 'constructor' : 'method', name, file: rel(file), line: line(m),
            endLine: endLine(m), visibility: hidden ? 'private' : 'public', doc: jsDoc(m), complexity: complexity(m),
            params: m.parameters.length, hash: hash(m.getText(sf)),
          })
          edge(id, mid, 'contains')
          owners.set(m, mid)
        }
      }
      continue
    }
    let decl, fn, name
    if (ts.isFunctionDeclaration(st) && st.name && exported(st)) [decl, fn, name] = [st, st, st.name.text]
    else if (ts.isVariableStatement(st) && exported(st))
      for (const d of st.declarationList.declarations)
        if (ts.isIdentifier(d.name) && d.initializer) [decl, fn, name] = [d, d.initializer, d.name.text]
    if (!decl) continue

    const isStore = ts.isCallExpression(fn) && fn.expression.getText(sf) === 'defineStore'
    if (!isStore && !ts.isFunctionLike(fn)) continue
    const id = `${fileId(file)}#${name}`
    const tags = isStore ? ['store'] : name.startsWith('use') ? ['composable'] : undefined
    nodes.push({
      id, kind: isStore ? 'store' : 'function', name, file: rel(file), line: line(decl),
      endLine: sf.getLineAndCharacterOfPosition(decl.getEnd()).line + 1 + lineOffset, visibility: 'public',
      doc: jsDoc(decl), complexity: complexity(fn), params: isStore ? undefined : fn.parameters.length,
      hash: hash(fn.getText(sf)), tags,
    })
    edge(owner, id, 'contains')
    owners.set(fn, id)
  }

  let props = null
  const visit = (n, cur) => {
    cur = owners.get(n) ?? cur
    if (ts.isCallExpression(n)) {
      const callee = n.expression.getText(sf)
      const http = !wrapperCalls.has(`${file}:${n.pos}`) && httpCall(n, ctx)
      if (http) edge(cur, `http:${http.method} ${http.url}`, 'http')
      // Lifecycle hooks and composables (useX(), local or from a library) a component uses.
      if (component && ts.isIdentifier(n.expression) && (LIFECYCLE.has(callee) || /^use[A-Z]/.test(callee)))
        component.hooks.push(LIFECYCLE.has(callee) ? callee : `${callee}()`)
      const imp = ts.isIdentifier(n.expression) && imports.get(n.expression.text)
      if (imp && imp.target.endsWith('.ts')) edge(cur, `${fileId(imp.target)}#${imp.name}`, 'calls')
      if (component && callee === 'defineProps') props = propsOf(n, sf)
      if (component && callee === 'defineEmits') component.emits = emitsOf(n, sf)
      // Component state: `const x = ref(..)` / reactive / computed, and what it watches.
      if (component && ['ref', 'shallowRef', 'reactive', 'computed'].includes(callee)
          && ts.isVariableDeclaration(n.parent) && ts.isIdentifier(n.parent.name))
        component.state.push(callee === 'computed' ? `${n.parent.name.text} (computed)` : n.parent.name.text)
      if (component && callee === 'watch' && n.arguments[0]) component.state.push(`watch ${n.arguments[0].getText(sf)}`)
      if (callee === 'createRouter' || callee === 'createWebHistory') scanRoutes(file, sf, imports, line)
    }
    ts.forEachChild(n, c => visit(c, cur))
  }
  visit(sf, owner)
  return { imports, props }
}

// defineProps<{ /** doc */ a: T }>(), defineProps<Props>() or defineProps({ a: String })
function propsOf(call, sf) {
  let members = []
  const typeArg = call.typeArguments?.[0]
  if (typeArg && ts.isTypeLiteralNode(typeArg)) members = typeArg.members
  else if (typeArg && ts.isTypeReferenceNode(typeArg)) {
    const name = typeArg.typeName.getText(sf)
    const decl = sf.statements.find(s => (ts.isInterfaceDeclaration(s) || ts.isTypeAliasDeclaration(s)) && s.name.text === name)
    members = decl ? (ts.isInterfaceDeclaration(decl) ? decl.members : decl.type.members ?? []) : []
  } else if (call.arguments[0] && ts.isObjectLiteralExpression(call.arguments[0])) members = call.arguments[0].properties
  return members.filter(m => m.name).map(m => {
    const docs = ts.getJSDocCommentsAndTags(m).filter(ts.isJSDoc)
    const type = m.type?.getText(sf) ?? (ts.isPropertyAssignment(m) ? m.initializer.getText(sf) : null)
    return { name: m.name.getText(sf), type, doc: docs.map(d => ts.getTextOfJSDocComment(d.comment) ?? '').join(' ').trim() }
  })
}

// defineEmits<{ created: [id: number] }>(), defineEmits<{ (e: 'created', id: number): void }>() or defineEmits(['created'])
function emitsOf(call, sf) {
  const typeArg = call.typeArguments?.[0]
  const names = []
  if (typeArg && ts.isTypeLiteralNode(typeArg))
    for (const m of typeArg.members) {
      if (m.name) names.push(m.name.getText(sf).replace(/['"]/g, ''))
      else if (ts.isCallSignatureDeclaration(m) && m.parameters[0]?.type && ts.isLiteralTypeNode(m.parameters[0].type))
        names.push(m.parameters[0].type.literal.text)
    }
  const arg = call.arguments[0]
  if (arg && ts.isArrayLiteralExpression(arg)) for (const e of arg.elements) if (ts.isStringLiteralLike(e)) names.push(e.text)
  if (arg && ts.isObjectLiteralExpression(arg)) for (const p of arg.properties) if (p.name) names.push(p.name.getText(sf).replace(/['"]/g, ''))
  return names
}

let routesScanned = new Set()
function scanRoutes(file, sf, imports, line) {
  if (routesScanned.has(file)) return
  routesScanned.add(file)
  const walk = n => {
    if (ts.isObjectLiteralExpression(n)) {
      const prop = k => n.properties.find(p => ts.isPropertyAssignment(p) && p.name.getText(sf) === k)?.initializer
      const p = prop('path')
      const c = prop('component')
      if (p && ts.isStringLiteralLike(p)) {
        const id = `route:${p.text}`
        nodes.push({ id, kind: 'route', name: p.text, file: rel(file), line: line(n), route: p.text })
        let target = null
        if (c && ts.isIdentifier(c)) target = imports.get(c.text)?.target
        else if (c && ts.isArrowFunction(c)) {
          const imp = c.body.getText(sf).match(/import\(\s*['"`]([^'"`]+)['"`]\s*\)/)
          if (imp) target = resolveImport(file, imp[1])
        }
        if (target) edge(id, fileId(target), 'routes-to')
      }
    }
    ts.forEachChild(n, walk)
  }
  walk(sf)
}

function scanTemplate(owner, template, imports) {
  let decisions = 0
  const pascal = s => s.replace(/(^|-)(\w)/g, (_, __, c) => c.toUpperCase())
  const walk = n => {
    if (n.type === 1) { // element
      const imp = imports.get(n.tag) ?? imports.get(pascal(n.tag))
      if (imp?.target.endsWith('.vue')) {
        edge(owner, fileId(imp.target), 'renders')
        // <Child @created="..."> / v-on:created: the parent subscribes to the child's event.
        for (const p of n.props)
          if (p.type === 7 && p.name === 'on' && p.arg?.isStatic) edge(owner, fileId(imp.target), 'subscribes', p.arg.content)
      }
      decisions += n.props.filter(p => p.type === 7 && ['if', 'else-if', 'for'].includes(p.name)).length
    }
    n.children?.forEach(walk)
    if (n.branches) n.branches.forEach(walk)
  }
  if (template?.ast) walk(template.ast)
  return decisions
}

scanWrappers(files)
for (const file of files) {
  const src = fs.readFileSync(file, 'utf8')
  const id = fileId(file)
  try {
    if (file.endsWith('.vue')) {
      const { descriptor } = parseSfc(src, { filename: file })
      const block = descriptor.scriptSetup ?? descriptor.script
      const component = { emits: null, state: [], hooks: [] }
      const node = {
        id, kind: 'component', name: path.basename(file, '.vue'), file: rel(file), line: 1,
        endLine: src.split('\n').length, visibility: 'public',
        hash: hash(src), tags: rel(file).includes('/views/') ? ['view'] : undefined,
      }
      nodes.push(node)
      let imports = new Map()
      let complexityScore = 1
      let props = null
      let sf = null
      if (block) {
        sf = ts.createSourceFile(file, block.content, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS)
        ;({ imports, props } = scanScript(file, sf, id, block.loc.start.line - 1, component))
        complexityScore = complexity(sf)
      }
      complexityScore += scanTemplate(id, descriptor.template, imports)

      // Component docs: an HTML comment before the first block, or a leading comment in the script.
      const html = src.match(/^\s*<!--([\s\S]*?)-->/)?.[1]
      // Leading comment of the first statement, or of the first one after the imports.
      const leadText = sf && [sf.statements[0], sf.statements.find(s => !ts.isImportDeclaration(s))]
        .filter(Boolean)
        .map(s => (ts.getLeadingCommentRanges(sf.text, s.getFullStart()) ?? [])
          .map(r => sf.text.slice(r.pos, r.end).replace(/^\/\*\*?|\*\/$|^\s*\/\/ ?|^\s*\* ?/gm, '').trim()).join(' ').trim())
        .find(Boolean)
      const summary = (html || leadText || '').trim()
      const params = (props ?? []).filter(p => p.doc).map(p => `<param name="${esc(p.name)}">${esc(p.doc)}</param>`)
      if (summary || params.length)
        node.doc = `<member>${summary ? `<summary>${esc(summary)}</summary>` : ''}${params.join('')}</member>`
      node.complexity = complexityScore
      node.params = props?.length ?? 0
      if (props?.length) node.parameters = props.map(p => p.type ? `${p.name}: ${p.type}` : p.name)
      if (component.state.length) node.state = component.state
      if (component.hooks.length) node.hooks = [...new Set(component.hooks)]
      if (component.emits) {
        node.tags = [...(node.tags ?? []), 'emits']
        if (component.emits.length) node.events = component.emits
      }
    } else {
      const sf = ts.createSourceFile(file, src, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS)
      nodes.push({ id, kind: 'module', name: path.basename(file), file: rel(file), line: 1, hash: hash(src) })
      scanScript(file, sf, id, 0, null)
    }
  } catch (e) {
    process.stderr.write(`docwizz: skipped ${rel(file)}: ${e.message}\n`)
  }
}

process.stdout.write(JSON.stringify({ nodes, edges }))
