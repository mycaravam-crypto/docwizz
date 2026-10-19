using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

// EF Designer EDMX read-only facts. No builds, network calls, generated code or domain inference (#262 P0).
static class EdmxParser
{
    public const string Schema = "docwizz.edmx/v1";
    public const long MaxFileBytes = 20 * 1024 * 1024;
    const int MaxElements = 100_000;

    public record Evidence(string File, int Line);
    public record Property(string Name, string Type, bool Nullable, bool Key, Evidence Source);
    public record Entity(string Id, string? BaseType, List<Property> Properties, Evidence Source);
    public record EntitySet(string Id, string EntityType, string? Table, string? DbSchema, Evidence Source);
    public record AssociationEnd(string Role, string EntityType, string Multiplicity, Evidence Source);
    public record Association(string Id, List<AssociationEnd> Ends, Evidence Source);
    public record AssociationSet(string Name, string Association, List<string> EntitySets, Evidence Source);
    public record Navigation(string Owner, string Name, string Relationship, string FromRole, string ToRole, Evidence Source);
    public record ScalarMapping(string Property, string Column, Evidence Source);
    public record Mapping(string ConceptualSet, string ConceptualType, string StorageSet,
        List<ScalarMapping> Properties, Evidence Source);
    public record AssociationMapping(string Name, string StorageSet, List<ScalarMapping> Properties, Evidence Source);
    public record FunctionImport(string Id, string? ReturnType, string? StoreFunction, Evidence Source);
    public record Finding(string Code, string Severity, string Message, Evidence Source);
    public record Report(string Schema, string File, List<Entity> Entities, List<EntitySet> ConceptualSets,
        List<Entity> StorageEntities, List<EntitySet> StorageSets, List<Association> Associations,
        List<AssociationSet> AssociationSets, List<Navigation> Navigations, List<Mapping> Mappings,
        List<AssociationMapping> AssociationMappings, List<FunctionImport> FunctionImports, List<Finding> Findings);

    static IEnumerable<XElement> Children(XContainer e, string name) =>
        e.Elements().Where(x => x.Name.LocalName == name);
    static IEnumerable<XElement> Schemas(XDocument doc, string container) =>
        doc.Descendants().Where(e => e.Name.LocalName == container).SelectMany(e => Children(e, "Schema"));
    static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";
    static Evidence At(XElement e, string file) => new(file, ((IXmlLineInfo)e).LineNumber);
    static string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    static string TypeName(string type) => type.StartsWith("IsTypeOf(", StringComparison.Ordinal) && type.EndsWith(')')
        ? type[9..^1] : type;

    static (string Full, string Relative) CheckedFile(string root, string file)
    {
        var basePath = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(basePath, file));
        var rel = Path.GetRelativePath(basePath, full).Replace('\\', '/');
        if (rel is ".." or "." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            throw new ArgumentException("EDMX file must be inside the selected root");
        // File and parent directory links must not allow an in-root path to read an out-of-root target.
        var p = full;
        while (true)
        {
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("EDMX path contains a symbolic link or reparse point");
            if (string.Equals(p, basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
            p = Path.GetDirectoryName(p) ?? throw new IOException("EDMX path has no parent");
        }
        if (!string.Equals(Path.GetExtension(full), ".edmx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected an .edmx file");
        if (new FileInfo(full).Length > MaxFileBytes)
            throw new InvalidDataException("EDMX file exceeds 20 MiB safety limit");
        return (full, rel);
    }

    public static Report Parse(string root, string file)
    {
        var (full, rel) = CheckedFile(root, file);
        var entities = new List<Entity>();
        var sets = new List<EntitySet>();
        var storage = new List<Entity>();
        var storageSets = new List<EntitySet>();
        var associations = new List<Association>();
        var associationSets = new List<AssociationSet>();
        var navigations = new List<Navigation>();
        var mappings = new List<Mapping>();
        var associationMappings = new List<AssociationMapping>();
        var functions = new List<FunctionImport>();
        var findings = new List<Finding>();
        void Warn(string code, string message, Evidence source) =>
            findings.Add(new(code, "warning", message, source));
        void Error(string code, string message, Evidence source) =>
            findings.Add(new(code, "error", message, source));
        Report Result() => new(Schema, rel,
            [.. entities.OrderBy(x => x.Id, StringComparer.Ordinal)],
            [.. sets.OrderBy(x => x.Id, StringComparer.Ordinal)],
            [.. storage.OrderBy(x => x.Id, StringComparer.Ordinal)],
            [.. storageSets.OrderBy(x => x.Id, StringComparer.Ordinal)],
            [.. associations.OrderBy(x => x.Id, StringComparer.Ordinal)],
            [.. associationSets.OrderBy(x => x.Name, StringComparer.Ordinal)],
            [.. navigations.OrderBy(x => x.Owner, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)],
            [.. mappings.OrderBy(x => x.ConceptualSet, StringComparer.Ordinal).ThenBy(x => x.ConceptualType, StringComparer.Ordinal).ThenBy(x => x.StorageSet, StringComparer.Ordinal)],
            [.. associationMappings.OrderBy(x => x.Name, StringComparer.Ordinal)],
            [.. functions.OrderBy(x => x.Id, StringComparer.Ordinal)],
            [.. findings.OrderBy(x => x.Source.File, StringComparer.Ordinal).ThenBy(x => x.Source.Line).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal)]);

        XDocument doc;
        try
        {
            using var stream = File.OpenRead(full);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 25_000_000,
                IgnoreComments = true
            });
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            Error("EDMX_XML_INVALID", e.Message, new(rel, e.LineNumber));
            return Result();
        }

        if (doc.Root?.Name.LocalName != "Edmx" || !Schemas(doc, "ConceptualModels").Any())
        {
            Error("EDMX_STRUCTURE", "Expected an Edmx document with a ConceptualModels/Schema section", new(rel, 1));
            return Result();
        }
        if (doc.Descendants().Take(MaxElements + 1).Count() > MaxElements)
        {
            Error("EDMX_SIZE", "EDMX element count exceeds safety limit", new(rel, 1));
            return Result();
        }

        static List<Entity> ReadEntities(IEnumerable<XElement> schemas, string file)
        {
            return [.. schemas.SelectMany(s =>
            {
                var ns = Attr(s, "Namespace");
                return Children(s, "EntityType").Select(e =>
                {
                    var keys = Children(e, "Key").SelectMany(k => Children(k, "PropertyRef"))
                        .Select(x => Attr(x, "Name")).ToHashSet(StringComparer.Ordinal);
                    return new Entity(Qualified(ns, Attr(e, "Name")),
                        (string?)e.Attribute("BaseType"),
                        [.. Children(e, "Property").Select(p =>
                            new Property(Attr(p, "Name"), Attr(p, "Type"),
                                Attr(p, "Nullable") != "false", keys.Contains(Attr(p, "Name")), At(p, file)))],
                        At(e, file));
                });
            })];
        }
        static List<EntitySet> ReadSets(IEnumerable<XElement> schemas, string file, bool store)
        {
            return [.. schemas.SelectMany(s =>
                Children(s, "EntityContainer").SelectMany(c => Children(c, "EntitySet")
                    .Select(e => new EntitySet(Attr(e, "Name"), Attr(e, "EntityType"),
                        store ? (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Table") : null,
                        store ? (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Schema") : null,
                        At(e, file)))))];
        }

        var conceptual = Schemas(doc, "ConceptualModels").ToList();
        var storeSchemas = Schemas(doc, "StorageModels").ToList();
        entities.AddRange(ReadEntities(conceptual, rel));
        storage.AddRange(ReadEntities(storeSchemas, rel));
        sets.AddRange(ReadSets(conceptual, rel, false));
        storageSets.AddRange(ReadSets(storeSchemas, rel, true));

        foreach (var s in conceptual)
        {
            var ns = Attr(s, "Namespace");
            foreach (var a in Children(s, "Association"))
                associations.Add(new(Qualified(ns, Attr(a, "Name")),
                    [.. Children(a, "End").Select(e => new AssociationEnd(Attr(e, "Role"),
                        Attr(e, "Type"), Attr(e, "Multiplicity"), At(e, rel)))], At(a, rel)));
            foreach (var e in Children(s, "EntityType"))
                foreach (var n in Children(e, "NavigationProperty"))
                    navigations.Add(new(Qualified(ns, Attr(e, "Name")), Attr(n, "Name"),
                        Attr(n, "Relationship"), Attr(n, "FromRole"), Attr(n, "ToRole"), At(n, rel)));
            foreach (var c in Children(s, "EntityContainer"))
            {
                foreach (var a in Children(c, "AssociationSet"))
                    associationSets.Add(new(Attr(a, "Name"), Attr(a, "Association"),
                        [.. Children(a, "End").Select(x => Attr(x, "EntitySet"))], At(a, rel)));
                foreach (var f in Children(c, "FunctionImport"))
                    functions.Add(new(Qualified(ns, Attr(f, "Name")), (string?)f.Attribute("ReturnType"), null, At(f, rel)));
            }
        }

        foreach (var s in doc.Descendants().Where(e => e.Name.LocalName == "Mappings").SelectMany(e => Children(e, "Mapping")))
        {
            foreach (var esm in s.Descendants().Where(e => e.Name.LocalName == "EntitySetMapping"))
                foreach (var etm in Children(esm, "EntityTypeMapping"))
                {
                    var type = TypeName(Attr(etm, "TypeName"));
                    if (type.Length == 0)
                        Warn("EDMX_MAPPING_UNSUPPORTED", "EntityTypeMapping without TypeName", At(etm, rel));
                    foreach (var fragment in Children(etm, "MappingFragment"))
                    {
                        if (fragment.Descendants().Any(x => x.Name.LocalName is "ComplexProperty" or "Condition"))
                            Warn("EDMX_MAPPING_PARTIAL", "ComplexProperty/Condition requires further mapping interpretation", At(fragment, rel));
                        mappings.Add(new(Attr(esm, "Name"), type, Attr(fragment, "StoreEntitySet"),
                            [.. Children(fragment, "ScalarProperty").Select(p => new ScalarMapping(Attr(p, "Name"),
                                Attr(p, "ColumnName"), At(p, rel)))], At(fragment, rel)));
                    }
                }
            foreach (var asm in s.Descendants().Where(e => e.Name.LocalName == "AssociationSetMapping"))
                associationMappings.Add(new(Attr(asm, "Name"), Attr(asm, "StoreEntitySet"),
                    [.. asm.Descendants().Where(e => e.Name.LocalName == "ScalarProperty")
                        .Select(p => new ScalarMapping(Attr(p, "Name"), Attr(p, "ColumnName"), At(p, rel)))], At(asm, rel)));
            foreach (var fim in s.Descendants().Where(e => e.Name.LocalName == "FunctionImportMapping"))
            {
                var name = Attr(fim, "FunctionImportName");
                var matches = functions.Where(f => f.Id.EndsWith("." + name, StringComparison.Ordinal)).ToList();
                if (matches.Count == 1)
                {
                    var index = functions.IndexOf(matches[0]);
                    functions[index] = matches[0] with { StoreFunction = Attr(fim, "FunctionName") };
                }
                else Warn("EDMX_REFERENCE", "FunctionImportMapping does not resolve uniquely: " + name, At(fim, rel));
            }
        }

        // Report unresolved references, never create guessed edges.
        static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key) =>
            items.GroupBy(key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var conceptualTypes = Index(entities, x => x.Id);
        var storageTypes = Index(storage, x => x.Id);
        var cs = Index(sets, x => x.Id);
        var ss = Index(storageSets, x => x.Id);
        var assoc = Index(associations, x => x.Id);
        foreach (var e in entities.Concat(storage))
        {
            var typeIndex = entities.Contains(e) ? conceptualTypes : storageTypes;
            if (e.BaseType is { Length: > 0 } b && !typeIndex.ContainsKey(b))
                Error("EDMX_REFERENCE", "Missing base entity type: " + b, e.Source);
            foreach (var p in e.Properties.Where(p => p.Key && p.Name.Length == 0))
                Error("EDMX_REFERENCE", "Empty key property", p.Source);
        }
        foreach (var x in sets)
            if (!conceptualTypes.ContainsKey(x.EntityType))
                Error("EDMX_REFERENCE", "Missing conceptual entity: " + x.EntityType, x.Source);
        foreach (var x in storageSets)
            if (!storageTypes.ContainsKey(x.EntityType))
                Error("EDMX_REFERENCE", "Missing storage entity: " + x.EntityType, x.Source);
        foreach (var a in associations)
        {
            if (a.Ends.Count != 2)
                Warn("EDMX_ASSOCIATION_UNSUPPORTED", "Expected exactly two association ends: " + a.Id, a.Source);
            foreach (var end in a.Ends)
            {
                if (!conceptualTypes.ContainsKey(end.EntityType))
                    Error("EDMX_REFERENCE", "Missing association entity: " + end.EntityType, end.Source);
                if (end.Multiplicity is not ("1" or "0..1" or "*"))
                    Warn("EDMX_ASSOCIATION_UNSUPPORTED", "Unsupported multiplicity: " + end.Multiplicity, end.Source);
            }
        }
        foreach (var a in associationSets)
        {
            if (!assoc.ContainsKey(a.Association))
                Error("EDMX_REFERENCE", "Missing association: " + a.Association, a.Source);
            foreach (var name in a.EntitySets)
                if (!cs.ContainsKey(name))
                    Error("EDMX_REFERENCE", "Missing association entity set: " + name, a.Source);
        }
        foreach (var n in navigations)
        {
            if (!assoc.TryGetValue(n.Relationship, out var a) ||
                !a.Ends.Any(e => e.Role == n.FromRole) || !a.Ends.Any(e => e.Role == n.ToRole))
                Error("EDMX_REFERENCE", "Unresolved navigation: " + n.Owner + "." + n.Name, n.Source);
        }
        foreach (var m in mappings)
        {
            if (!cs.ContainsKey(m.ConceptualSet) || !conceptualTypes.ContainsKey(m.ConceptualType) || !ss.ContainsKey(m.StorageSet))
                Error("EDMX_REFERENCE", "Unresolved entity mapping: " + m.ConceptualSet + " -> " + m.StorageSet, m.Source);
            if (ss.TryGetValue(m.StorageSet, out var target) && storageTypes.TryGetValue(target.EntityType, out var store))
                foreach (var p in m.Properties)
                    if (!store.Properties.Any(x => x.Name == p.Column))
                        Error("EDMX_REFERENCE", "Missing storage column: " + p.Column, p.Source);
            if (conceptualTypes.TryGetValue(m.ConceptualType, out var source))
                foreach (var p in m.Properties)
                    if (!source.Properties.Any(x => x.Name == p.Property) &&
                        !(source.BaseType is { } b && conceptualTypes.TryGetValue(b, out var parent) && parent.Properties.Any(x => x.Name == p.Property)))
                        Error("EDMX_REFERENCE", "Missing conceptual property: " + p.Property, p.Source);
        }
        foreach (var a in associationMappings)
        {
            if (!associationSets.Any(x => x.Name == a.Name) || !ss.ContainsKey(a.StorageSet))
                Error("EDMX_REFERENCE", "Unresolved association mapping: " + a.Name, a.Source);
        }
        return Result();
    }

    public static string Json(Report report) => JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    { WriteIndented = true }) + "\n";

    public static string Text(Report report)
    {
        var b = new StringBuilder();
        b.AppendLine("EDMX " + report.File + ": " + report.Entities.Count + " conceptual entities, " +
            report.Associations.Count + " associations, " + report.Mappings.Count + " entity mapping fragments");
        foreach (var f in report.Findings)
            b.AppendLine(f.Severity + " " + f.Code + " " + f.Source.File + ":" + f.Source.Line + ": " + f.Message);
        return b.ToString();
    }
}
