// Java syntax for docwizz: parses each file with tree-sitter (tree-sitter-java, run as WebAssembly, so nothing is
// compiled or executed) and prints, per file, the declarations the C# side turns into model nodes and edges
// (JavaScanner.cs): types with their annotations, Javadoc, supertypes and members; methods with parameters, return
// type, throws, complexity and the calls in their bodies. Only syntax: names are resolved on the C# side.
// stdin: one absolute path per line; stdout: {"files": [...]}. Deterministic: same files, same output.
import { Parser, Language } from 'web-tree-sitter'
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
await Parser.init()
const parser = new Parser()
parser.setLanguage(await Language.load(require.resolve('tree-sitter-java/tree-sitter-java.wasm')))

const TYPES = {
  class_declaration: 'class', interface_declaration: 'interface', enum_declaration: 'enum',
  record_declaration: 'record', annotation_type_declaration: '@interface',
}
// Decision points for cyclomatic complexity, as the C# scanner counts them.
const BRANCHES = new Set(['if_statement', 'for_statement', 'enhanced_for_statement', 'while_statement', 'do_statement',
  'catch_clause', 'ternary_expression'])

const line = n => n.startPosition.row + 1
const endLine = n => n.endPosition.row + 1
const hash = text => createHash('sha256').update(text.replace(/\s+/g, ' ').trim()).digest('hex').slice(0, 12)
const compact = text => text.replace(/\s+/g, '')

function* walk(node, stop) {
  for (const c of node.namedChildren) {
    yield c
    if (!stop?.(c)) yield* walk(c, stop)
  }
}

// `@Name`, `@Name(args)`: the simple name and the raw text between the parentheses.
function annotations(modifiers) {
  if (!modifiers) return []
  return modifiers.namedChildren.filter(c => c.type === 'annotation' || c.type === 'marker_annotation').map(a => {
    const args = a.childForFieldName('arguments')
    return { name: a.childForFieldName('name').text.split('.').pop(), args: args ? args.text.slice(1, -1) : '' }
  })
}

const modifierWords = modifiers => modifiers ? modifiers.children.filter(c => !c.isNamed || c.type === 'modifier').map(c => c.text) : []

// The Javadoc right before a declaration (comments are siblings in tree-sitter's tree).
function javadoc(node) {
  for (let p = node.previousSibling; p && (p.type === 'block_comment' || p.type === 'line_comment'); p = p.previousSibling)
    if (p.type === 'block_comment') return p.text.startsWith('/**') ? p.text : null
  return null
}

// Where the declaration starts after its annotations: the line a reader looks for (`public Order get(...)`).
function declarationLine(node) {
  const modifiers = node.namedChildren.find(c => c.type === 'modifiers')
  const keyword = modifiers?.children.find(c => c.type !== 'annotation' && c.type !== 'marker_annotation' && !c.type.endsWith('comment'))
  if (keyword) return line(keyword)
  const after = node.children.find(c => c !== modifiers && !c.type.endsWith('comment'))
  return after ? line(after) : line(node)
}

function complexity(body) {
  if (!body) return 1
  let n = 1
  for (const c of walk(body)) {
    if (BRANCHES.has(c.type)) n++
    else if (c.type === 'switch_label' && c.text.startsWith('case')) n++
    else if (c.type === 'binary_expression' && ['&&', '||'].includes(c.childForFieldName('operator')?.text)) n++
  }
  return n
}

function parameters(list) {
  if (!list) return []
  return list.namedChildren.filter(p => p.type === 'formal_parameter' || p.type === 'spread_parameter').map(p => {
    const modifiers = p.namedChildren.find(c => c.type === 'modifiers')
    const type = p.childForFieldName('type') ?? p.namedChildren.find(c => c !== modifiers && c.type !== 'variable_declarator' && c.type !== 'identifier')
    const name = p.childForFieldName('name') ?? p.namedChildren.find(c => c.type === 'variable_declarator')?.childForFieldName('name')
      ?? p.namedChildren.filter(c => c.type === 'identifier').pop()
    const dims = p.childForFieldName('dimensions')?.text ?? ''
    return { name: name?.text ?? '', type: compact((type?.text ?? '') + dims) + (p.type === 'spread_parameter' ? '...' : ''), annotations: annotations(modifiers) }
  })
}

// Calls in a body: receiver (`repo`, `this.repo`, a type name, or null for an unqualified call), name, argument count.
// Lambdas belong to the method they are written in; anonymous and local classes are their own code, not this method's.
function calls(body) {
  if (!body) return { calls: [], locals: [] }
  const result = [], locals = []
  const local = n => n.type === 'class_body' || n.type === 'class_declaration'
  for (const c of walk(body, local)) {
    if (c.type === 'method_invocation') {
      const object = c.childForFieldName('object')
      let receiver = null
      if (object?.type === 'identifier') receiver = object.text
      else if (object?.type === 'field_access' && object.childForFieldName('object')?.type === 'this') receiver = 'this.' + object.childForFieldName('field').text
      else if (object?.type === 'this') receiver = 'this'
      else if (object) continue   // chains and other expressions: their type isn't known from syntax
      result.push({ receiver, name: c.childForFieldName('name').text, args: c.childForFieldName('arguments').namedChildren.filter(a => !a.type.endsWith('comment')).length, line: line(c) })
    } else if (c.type === 'local_variable_declaration') {
      const type = c.childForFieldName('type')
      for (const d of c.namedChildren.filter(x => x.type === 'variable_declarator')) {
        let t = type.text
        const value = d.childForFieldName('value')
        if (t === 'var' && value?.type === 'object_creation_expression') t = value.childForFieldName('type').text   // var x = new T()
        if (t !== 'var') locals.push({ name: d.childForFieldName('name').text, type: compact(t) })
      }
    }
  }
  return { calls: result, locals }
}

function method(node, typeName) {
  const ctor = node.type === 'constructor_declaration' || node.type === 'compact_constructor_declaration'
  const modifiers = node.namedChildren.find(c => c.type === 'modifiers')
  const body = node.childForFieldName('body')
  const throwsNode = node.namedChildren.find(c => c.type === 'throws')
  const { calls: found, locals } = calls(body)
  return {
    name: node.childForFieldName('name')?.text ?? typeName,
    constructor: ctor,
    compact: node.type === 'compact_constructor_declaration',   // a record's canonical constructor: its parameters are the components
    modifiers: modifierWords(modifiers),
    annotations: annotations(modifiers),
    javadoc: javadoc(node),
    returns: ctor ? null : compact(node.childForFieldName('type').text + (node.childForFieldName('dimensions')?.text ?? '')),
    parameters: node.type === 'compact_constructor_declaration' ? [] : parameters(node.childForFieldName('parameters')),
    throws: throwsNode ? throwsNode.namedChildren.map(t => t.text.split('.').pop()) : [],
    abstract: !body,
    line: declarationLine(node), endLine: endLine(node), hash: hash(node.text), complexity: complexity(body),
    calls: found, locals,
  }
}

function field(node) {
  const modifiers = node.namedChildren.find(c => c.type === 'modifiers')
  return {
    names: node.namedChildren.filter(c => c.type === 'variable_declarator').map(d => d.childForFieldName('name').text),
    type: compact(node.childForFieldName('type').text),
    modifiers: modifierWords(modifiers),
    annotations: annotations(modifiers),
  }
}

// Supertypes by simple name, generic arguments dropped: `extends JpaRepository<Item, Long>` → JpaRepository.
const simple = t => t.text.replace(/<[\s\S]*$/, '').split('.').pop().trim()
function supertypes(node) {
  const out = { extends: [], implements: [] }
  const sup = node.childForFieldName('superclass')
  if (sup) out.extends.push(...sup.namedChildren.map(simple))
  const ifaces = node.childForFieldName('interfaces') ?? node.namedChildren.find(c => c.type === 'super_interfaces')
  if (ifaces) for (const list of ifaces.namedChildren) out.implements.push(...(list.type === 'type_list' ? list.namedChildren : [list]).map(simple))
  const ext = node.namedChildren.find(c => c.type === 'extends_interfaces')   // interface X extends Y, Z
  if (ext) for (const list of ext.namedChildren) out.extends.push(...(list.type === 'type_list' ? list.namedChildren : [list]).map(simple))
  return out
}

function file(path) {
  const text = readFileSync(path, 'utf8')
  const tree = parser.parse(text)
  const root = tree.rootNode
  const types = []
  function type(node, parent) {
    const modifiers = node.namedChildren.find(c => c.type === 'modifiers')
    const name = node.childForFieldName('name').text
    const keyword = node.children.find(c => !c.isNamed && ['class', 'interface', 'enum', 'record', '@interface'].includes(c.type))
    const entry = {
      name, kind: TYPES[node.type], parent, modifiers: modifierWords(modifiers), annotations: annotations(modifiers),
      javadoc: javadoc(node), ...supertypes(node),
      line: keyword ? line(keyword) : line(node), endLine: endLine(node), hash: hash(node.text),
      components: node.type === 'record_declaration' ? parameters(node.childForFieldName('parameters')) : [],
      fields: [], methods: [],
    }
    const index = types.push(entry) - 1
    let body = node.childForFieldName('body')
    const members = body ? [...body.namedChildren] : []
    // An enum's members come after its constants, in enum_body_declarations.
    for (const m of [...members]) if (m.type === 'enum_body_declarations') members.push(...m.namedChildren)
    for (const m of members) {
      if (TYPES[m.type]) type(m, index)
      else if (m.type === 'field_declaration' || m.type === 'constant_declaration') entry.fields.push(field(m))
      else if (['method_declaration', 'constructor_declaration', 'compact_constructor_declaration'].includes(m.type)) entry.methods.push(method(m, name))
    }
  }
  for (const c of root.namedChildren) if (TYPES[c.type]) type(c, null)
  const pkg = root.namedChildren.find(c => c.type === 'package_declaration')
  return {
    path,
    package: pkg ? pkg.namedChildren.find(c => c.type === 'scoped_identifier' || c.type === 'identifier')?.text ?? null : null,
    imports: root.namedChildren.filter(c => c.type === 'import_declaration')
      .map(i => i.namedChildren.find(c => c.type === 'scoped_identifier' || c.type === 'identifier')?.text).filter(Boolean),
    errors: root.hasError,
    types,
  }
}

const paths = readFileSync(0, 'utf8').split('\n').map(s => s.trim()).filter(Boolean)
process.stdout.write(JSON.stringify({ files: paths.map(file) }))
