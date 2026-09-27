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
const scriptKind = f => f.endsWith('.tsx') ? ts.ScriptKind.TSX : f.endsWith('.jsx') ? ts.ScriptKind.JSX : ts.ScriptKind.TS
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
  return [base, base + '.ts', base + '.tsx', base + '.jsx', base + '.vue', path.join(base, 'index.ts'), path.join(base, 'index.tsx')].find(f => fs.existsSync(f) && fs.statSync(f).isFile()) ?? null
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
  if (ts.isPropertyAccessExpression(callee) && (ts.isIdentifier(callee.expression) || ts.isPropertyAccessExpression(callee.expression))) {
    const obj = callee.expression.getText()   // axios, api (an axios instance), this.http (Angular HttpClient)
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
const selectors = new Map() // Angular component selector → component id
const wrapperCalls = new Set() // `file:pos` of the call inside a helper: its URL is the helper's parameter, not a call site
function scriptOf(file) {
  const src = fs.readFileSync(file, 'utf8')
  if (!file.endsWith('.vue')) return { sf: ts.createSourceFile(file, src, ts.ScriptTarget.Latest, true, scriptKind(file)), lineOffset: 0 }
  const { descriptor } = parseSfc(src, { filename: file })
  const block = descriptor.scriptSetup ?? descriptor.script
  return block && { sf: ts.createSourceFile(file, block.content, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS), lineOffset: block.loc.start.line - 1 }
}
// The component an import names: a .vue file is one; a TS/TSX export is `<file>#<name>` (default → its declared name).
function componentId(target, name) {
  if (target.endsWith('.vue')) return fileId(target)
  return `${fileId(target)}#${name === 'default' ? wrappers.get(target)?.exports.get('default') ?? name : name}`
}

// @Decorator({ a: 'x', b: \`y\` }) on a class → { a: 'x', b: 'y' } (string properties only); undefined without it.
function decoratorArg(node, name) {
  const d = (ts.canHaveDecorators(node) ? ts.getDecorators(node) : undefined)?.find(d => ts.isCallExpression(d.expression) && d.expression.expression.getText() === name)
  if (!d) return undefined
  const o = d.expression.arguments[0]
  const out = {}
  if (o && ts.isObjectLiteralExpression(o))
    for (const p of o.properties) if (ts.isPropertyAssignment(p) && ts.isStringLiteralLike(p.initializer)) out[p.name.getText()] = p.initializer.text
  return out
}
const hasDecorator = (node, name) => (ts.canHaveDecorators(node) ? ts.getDecorators(node) : undefined)
  ?.some(d => (ts.isCallExpression(d.expression) ? d.expression.expression : d.expression).getText() === name)

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
      if (ts.isFunctionDeclaration(st) && st.name && exported)
        w.exports.set(st.modifiers?.some(m => m.kind === ts.SyntaxKind.DefaultKeyword) ? 'default' : st.name.text, st.name.text)
      if (ts.isClassDeclaration(st) && st.name) {
        if (exported) w.exports.set(st.name.text, st.name.text)
        const selector = decoratorArg(st, 'Component')?.selector
        if (selector) selectors.set(selector, `${fileId(file)}#${st.name.text}`)
      }
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
  // Angular DI: constructor parameter properties and `x = inject(T)` fields. HttpClient ones are HTTP receivers
  // (this.http.get(..)); ones typed by a local class route `this.svc.m()` calls to that class.
  ctx.members = new Map()
  for (const cls of sf.statements.filter(ts.isClassDeclaration)) {
    const injected = [
      ...(cls.members.find(ts.isConstructorDeclaration)?.parameters ?? []).filter(p => p.type && ts.isTypeReferenceNode(p.type)).map(p => [p.name.getText(sf), p.type.typeName.getText(sf)]),
      ...cls.members.filter(m => ts.isPropertyDeclaration(m) && m.initializer && ts.isCallExpression(m.initializer) && m.initializer.expression.getText(sf) === 'inject')
        .map(m => [m.name.getText(sf), m.initializer.arguments[0]?.getText(sf)]),
    ]
    for (const [name, type] of injected) {
      if (type === 'HttpClient') ctx.instances.set(`this.${name}`, '')
      else if (imports.get(type)?.target && !imports.get(type).target.endsWith('.vue')) ctx.members.set(name, componentId(imports.get(type).target, imports.get(type).name))
    }
  }

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
      if (imp && !imp.target.endsWith('.vue')) edge(cur, componentId(imp.target, imp.name), 'calls')
      const through = callee.match(/^this\.(\w+)\.(\w+)$/)
      if (through && ctx.members.has(through[1])) edge(cur, `${ctx.members.get(through[1])}.${through[2]}`, 'calls')
      if (component && callee === 'defineProps') props = propsOf(n, sf)
      if (component && callee === 'defineEmits') component.emits = emitsOf(n, sf)
      // Component state: `const x = ref(..)` / reactive / computed, and what it watches.
      if (component && ['ref', 'shallowRef', 'reactive', 'computed'].includes(callee)
          && ts.isVariableDeclaration(n.parent) && ts.isIdentifier(n.parent.name))
        component.state.push(callee === 'computed' ? `${n.parent.name.text} (computed)` : n.parent.name.text)
      if (component && callee === 'watch' && n.arguments[0]) component.state.push(`watch ${n.arguments[0].getText(sf)}`)
      if (['createRouter', 'createWebHistory', 'createBrowserRouter', 'createHashRouter', 'createMemoryRouter', 'provideRouter', 'RouterModule.forRoot', 'RouterModule.forChild'].includes(callee))
        scanRoutes(file, sf, imports, line)
    }
    // Angular `const routes: Routes = [...]`; React Router <Route path=".." element={<X />} />.
    if (ts.isVariableDeclaration(n) && n.type?.getText(sf) === 'Routes') scanRoutes(file, sf, imports, line)
    if ((ts.isJsxSelfClosingElement(n) || ts.isJsxOpeningElement(n)) && n.tagName.getText(sf) === 'Route') {
      const attr = k => n.attributes.properties.find(a => ts.isJsxAttribute(a) && a.name.getText(sf) === k)?.initializer
      const p = attr('path')
      if (p && ts.isStringLiteral(p)) addRoute(file, p.text, line(n), jsxTarget(attr('element'), imports, file, sf))
    }
    ts.forEachChild(n, c => visit(c, cur))
  }
  visit(sf, owner)
  if (!component) scanComponents(file, sf, imports, line, owner)
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

// Route paths as the router sees them: Angular's are relative ('materials' → /materials).
function addRoute(file, raw, line, target) {
  const route = raw.startsWith('/') ? raw : `/${raw}`
  const id = `route:${route}`
  nodes.push({ id, kind: 'route', name: route, file: rel(file), line, route })
  if (target) edge(id, target, 'routes-to')
}

// element={<X />} → X's component id.
function jsxTarget(init, imports, file, sf) {
  const expr = init && ts.isJsxExpression(init) ? init.expression : init
  const tag = expr && (ts.isJsxSelfClosingElement(expr) ? expr.tagName : ts.isJsxElement(expr) ? expr.openingElement.tagName : null)
  if (!tag || !ts.isIdentifier(tag)) return null
  const imp = imports.get(tag.text)
  return imp ? componentId(imp.target, imp.name) : `${fileId(file)}#${tag.text}`
}

const NG_LIFECYCLE = new Set(['ngOnChanges', 'ngOnInit', 'ngDoCheck', 'ngAfterContentInit', 'ngAfterContentChecked',
  'ngAfterViewInit', 'ngAfterViewChecked', 'ngOnDestroy'])

// React function components and Angular @Component classes in a TS/TSX file: the nodes scanScript made for them
// (exported functions and classes) become components, with props, events, hooks and what they render.
function scanComponents(file, sf, imports, line, owner) {
  const byId = id => nodes.find(n => n.id === id)
  for (const st of sf.statements) {
    // React: a capitalised function (declaration or arrow const) whose body has JSX.
    let fn = null, name = null
    if (ts.isFunctionDeclaration(st) && st.name) [fn, name] = [st, st.name.text]
    else if (ts.isVariableStatement(st))
      for (const d of st.declarationList.declarations)
        if (ts.isIdentifier(d.name) && d.initializer && (ts.isArrowFunction(d.initializer) || ts.isFunctionExpression(d.initializer))) [fn, name] = [d.initializer, d.name.text]
    if (fn && /^[A-Z]/.test(name)) {
      let jsx = false
      const hooks = [], rendered = new Set()
      const walk = n => {
        if (ts.isJsxElement(n) || ts.isJsxSelfClosingElement(n) || ts.isJsxFragment(n)) jsx = true
        if ((ts.isJsxSelfClosingElement(n) || ts.isJsxOpeningElement(n)) && ts.isIdentifier(n.tagName) && /^[A-Z]/.test(n.tagName.text) && n.tagName.text !== 'Route') {
          const imp = imports.get(n.tagName.text)
          if (imp) rendered.add(componentId(imp.target, imp.name))
          else if (sf.statements.some(s => (ts.isFunctionDeclaration(s) && s.name?.text === n.tagName.text)
              || (ts.isVariableStatement(s) && s.declarationList.declarations.some(d => d.name.getText(sf) === n.tagName.text))))
            rendered.add(`${fileId(file)}#${n.tagName.text}`)
        }
        if (ts.isCallExpression(n) && ts.isIdentifier(n.expression) && /^use[A-Z]/.test(n.expression.text)) hooks.push(`${n.expression.text}()`)
        ts.forEachChild(n, walk)
      }
      walk(fn.body ?? fn)
      if (!jsx) continue
      const id = `${fileId(file)}#${name}`
      let node = byId(id)
      if (!node) { // not exported: still a component of this file
        node = { id, name, file: rel(file), line: line(st), visibility: 'public', hash: hash(st.getText(sf)), complexity: complexity(fn), doc: jsDoc(st) }
        nodes.push(node); edge(owner, id, 'contains')
      }
      node.kind = 'component'
      node.tags = [...new Set([...(node.tags ?? []).filter(t => t !== 'composable'), 'react'])]
      const props = reactProps(fn.parameters[0], sf)
      node.params = props.length
      if (props.length) node.parameters = props
      else delete node.parameters
      if (hooks.length) node.hooks = [...new Set(hooks)]
      for (const r of rendered) edge(id, r, 'renders')
      continue
    }
    // Angular: @Component classes (@Injectable ones are services).
    if (!ts.isClassDeclaration(st) || !st.name) continue
    const id = `${fileId(file)}#${st.name.text}`
    const node = byId(id)
    if (!node) continue
    if (hasDecorator(st, 'Injectable')) node.tags = [...new Set([...(node.tags ?? []), 'service'])]
    const meta = decoratorArg(st, 'Component')
    if (!meta) continue
    node.kind = 'component'
    node.tags = [...new Set([...(node.tags ?? []), 'angular'])]
    const inputs = [], outputs = [], hooks = []
    for (const m of st.members) {
      const mname = m.name?.getText(sf)
      const init = ts.isPropertyDeclaration(m) && m.initializer && ts.isCallExpression(m.initializer) ? m.initializer.expression.getText(sf) : ''
      if (hasDecorator(m, 'Input') || /^input(\.required)?$/.test(init)) inputs.push(m.type ? `${mname}: ${m.type.getText(sf)}` : mname)
      if (hasDecorator(m, 'Output') || init === 'output') outputs.push(mname)
      if (ts.isMethodDeclaration(m) && NG_LIFECYCLE.has(mname)) hooks.push(mname)
    }
    node.params = inputs.length
    if (inputs.length) node.parameters = inputs
    if (outputs.length) { node.events = outputs; node.tags.push('emits') }
    if (hooks.length) node.hooks = hooks
    // Child components from the template: <app-item-row> → the component with that selector.
    let template = meta.template ?? ''
    if (meta.templateUrl) try { template = fs.readFileSync(path.resolve(path.dirname(file), meta.templateUrl), 'utf8') } catch {}
    for (const tag of new Set([...template.matchAll(/<([a-z][\w]*-[\w-]+)/g)].map(m => m[1])))
      if (selectors.has(tag)) edge(id, selectors.get(tag), 'renders')
  }
}

// React props: an interface/type in the file, an inline type literal, or destructured names.
function reactProps(param, sf) {
  if (!param) return []
  let members = null
  const t = param.type
  if (t && ts.isTypeLiteralNode(t)) members = t.members
  else if (t && ts.isTypeReferenceNode(t)) {
    const decl = sf.statements.find(s => (ts.isInterfaceDeclaration(s) || ts.isTypeAliasDeclaration(s)) && s.name.text === t.typeName.getText(sf))
    members = decl ? (ts.isInterfaceDeclaration(decl) ? decl.members : decl.type.members ?? []) : null
  }
  if (members) return members.filter(m => m.name).map(m => m.type ? `${m.name.getText(sf)}: ${m.type.getText(sf)}` : m.name.getText(sf))
  if (ts.isObjectBindingPattern(param.name)) return param.name.elements.map(e => e.name.getText(sf))
  return []
}

let routesScanned = new Set()
function scanRoutes(file, sf, imports, line) {
  if (routesScanned.has(file)) return
  routesScanned.add(file)
  const walk = n => {
    if (ts.isObjectLiteralExpression(n)) {
      const prop = k => n.properties.find(p => ts.isPropertyAssignment(p) && p.name.getText(sf) === k)?.initializer
      const p = prop('path')
      const c = prop('component') ?? prop('loadComponent')
      if (p && ts.isStringLiteralLike(p)) {
        let target = null
        if (c && ts.isIdentifier(c) && imports.get(c.text)) target = componentId(imports.get(c.text).target, imports.get(c.text).name)
        else if (c && ts.isArrowFunction(c)) {
          // () => import('./X.vue') / import('./x.component').then(m => m.X)
          const text = c.body.getText(sf)
          const imp = text.match(/import\(\s*['"`]([^'"`]+)['"`]\s*\)/)
          const file2 = imp && resolveImport(file, imp[1])
          if (file2) target = componentId(file2, text.match(/\.then\(\s*\(?\s*\w+\s*\)?\s*=>\s*\w+\.(\w+)/)?.[1] ?? 'default')
        }
        addRoute(file, p.text, line(n), target ?? jsxTarget(prop('element'), imports, file, sf))
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
      const sf = ts.createSourceFile(file, src, ts.ScriptTarget.Latest, true, scriptKind(file))
      nodes.push({ id, kind: 'module', name: path.basename(file), file: rel(file), line: 1, hash: hash(src) })
      scanScript(file, sf, id, 0, null)
    }
  } catch (e) {
    process.stderr.write(`docwizz: skipped ${rel(file)}: ${e.message}\n`)
  }
}

process.stdout.write(JSON.stringify({ nodes, edges }))
