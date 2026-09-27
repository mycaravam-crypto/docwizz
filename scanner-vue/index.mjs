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
const edge = (from, to, kind) => edges.push({ from, to, kind })

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

function httpCall(call) {
  const callee = call.expression
  const [first, second] = call.arguments
  if (ts.isIdentifier(callee) && callee.text === 'fetch') {
    let method = 'GET'
    if (second && ts.isObjectLiteralExpression(second))
      for (const p of second.properties)
        if (ts.isPropertyAssignment(p) && p.name.getText() === 'method' && ts.isStringLiteralLike(p.initializer))
          method = p.initializer.text.toUpperCase()
    const url = urlOf(first)
    return url && { method, url }
  }
  if (ts.isPropertyAccessExpression(callee) && ts.isIdentifier(callee.expression) && callee.expression.text === 'axios') {
    const url = urlOf(first)
    return url && { method: callee.name.text.toUpperCase(), url }
  }
  return null
}

// Walks one script: imports, exported functions/stores, calls, HTTP, props/emits, routes.
function scanScript(file, sf, owner, lineOffset, component) {
  const imports = new Map() // local name → { target, name }
  for (const st of sf.statements) {
    if (!ts.isImportDeclaration(st) || st.importClause?.isTypeOnly) continue
    const target = resolveImport(file, st.moduleSpecifier.text)
    if (!target) continue
    edge(owner, fileId(target), 'imports')
    const c = st.importClause
    if (c?.name) imports.set(c.name.text, { target, name: 'default' })
    if (c?.namedBindings && ts.isNamedImports(c.namedBindings))
      for (const el of c.namedBindings.elements)
        if (!el.isTypeOnly) imports.set(el.name.text, { target, name: (el.propertyName ?? el.name).text })
  }

  const line = n => sf.getLineAndCharacterOfPosition(n.getStart(sf)).line + 1 + lineOffset
  const exported = n => ts.getCombinedModifierFlags(n) & ts.ModifierFlags.Export

  // Exported functions, arrow consts and stores become their own nodes and own their calls.
  const owners = new Map() // AST node → owner id
  for (const st of sf.statements) {
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
      const http = httpCall(n)
      if (http) edge(cur, `http:${http.method} ${http.url}`, 'http')
      const imp = ts.isIdentifier(n.expression) && imports.get(n.expression.text)
      if (imp && imp.target.endsWith('.ts')) edge(cur, `${fileId(imp.target)}#${imp.name}`, 'calls')
      if (component && callee === 'defineProps') props = propsOf(n, sf)
      if (component && callee === 'defineEmits') component.emits = true
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
    return { name: m.name.getText(sf), doc: docs.map(d => ts.getTextOfJSDocComment(d.comment) ?? '').join(' ').trim() }
  })
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
        if (target) edge(id, fileId(target), 'routes')
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
      if (imp?.target.endsWith('.vue')) edge(owner, fileId(imp.target), 'renders')
      decisions += n.props.filter(p => p.type === 7 && ['if', 'else-if', 'for'].includes(p.name)).length
    }
    n.children?.forEach(walk)
    if (n.branches) n.branches.forEach(walk)
  }
  if (template?.ast) walk(template.ast)
  return decisions
}

for (const file of files) {
  const src = fs.readFileSync(file, 'utf8')
  const id = fileId(file)
  try {
    if (file.endsWith('.vue')) {
      const { descriptor } = parseSfc(src, { filename: file })
      const block = descriptor.scriptSetup ?? descriptor.script
      const component = { emits: false }
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
      if (component.emits) node.tags = [...(node.tags ?? []), 'emits']
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
