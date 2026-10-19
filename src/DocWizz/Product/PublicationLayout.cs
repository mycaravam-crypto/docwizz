using System.IO.Compression;
using System.Xml.Linq;

// OOXML-only postprocessing of Pandoc's DOCX: keeps the controlled cover separate from
// document-control/contents and adds dynamic PAGE / NUMPAGES fields to every page.
internal static class PublicationLayout
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";

    static XDocument Read(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new IOException($"DOCX part missing: {name}");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    static void Write(ZipArchive archive, string name, XDocument document)
    {
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        document.Save(stream);
    }

    // Only derived, unsigned layout files are transformed. The original, signed Markdown,
    // evidence report and control YAML are never changed.
    internal static void Apply(string docx)
    {
        using var archive = ZipFile.Open(docx, ZipArchiveMode.Update);
        var document = Read(archive, "word/document.xml");
        var paragraphs = document.Descendants(W + "p").ToList();
        // Headings from the known controlled-document contract, not an arbitrary Markdown parser.
        var controlHeader = paragraphs.FirstOrDefault(p =>
            string.Concat(p.Descendants(W + "t").Select(t => t.Value)).Trim() == ControlledDocument.DocumentControlHeading);
        if (controlHeader is null)
            throw new IOException("document-control heading missing from generated DOCX");
        var properties = controlHeader.Element(W + "pPr");
        if (properties is null)
        {
            properties = new XElement(W + "pPr");
            controlHeader.AddFirst(properties);
        }
        if (properties.Element(W + "pageBreakBefore") is null)
            properties.Add(new XElement(W + "pageBreakBefore"));

        var section = document.Descendants(W + "sectPr").LastOrDefault()
            ?? throw new IOException("DOCX has no section properties for page-number footer");
        const string id = "rIdDocWizzFooter";
        if (section.Elements(W + "footerReference").Any(e => (string?)e.Attribute(R + "id") == id))
            throw new IOException("DOCX footer relationship already exists");
        section.AddFirst(new XElement(W + "footerReference",
            new XAttribute(W + "type", "default"), new XAttribute(R + "id", id)));
        Write(archive, "word/document.xml", document);

        var rels = Read(archive, "word/_rels/document.xml.rels");
        if (rels.Root is null || rels.Root.Elements(PR + "Relationship")
            .Any(r => (string?)r.Attribute("Id") == id))
            throw new IOException("DOCX footer relationship ID collision");
        rels.Root.Add(new XElement(PR + "Relationship",
            new XAttribute("Id", id),
            new XAttribute("Type",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer"),
            new XAttribute("Target", "footerDocWizz.xml")));
        Write(archive, "word/_rels/document.xml.rels", rels);

        var types = Read(archive, "[Content_Types].xml");
        if (types.Root is null) throw new IOException("invalid DOCX content types");
        if (!types.Root.Elements(CT + "Override").Any(x =>
            (string?)x.Attribute("PartName") == "/word/footerDocWizz.xml"))
            types.Root.Add(new XElement(CT + "Override",
                new XAttribute("PartName", "/word/footerDocWizz.xml"),
                new XAttribute("ContentType",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml")));
        Write(archive, "[Content_Types].xml", types);

        var footer = new XDocument(
            new XElement(W + "ftr",
                new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
                new XElement(W + "p",
                    new XElement(W + "pPr", new XElement(W + "jc", new XAttribute(W + "val", "right"))),
                    new XElement(W + "r", new XElement(W + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"), "Seite ")),
                    new XElement(W + "fldSimple", new XAttribute(W + "instr", "PAGE")),
                    new XElement(W + "r", new XElement(W + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"), " von ")),
                    new XElement(W + "fldSimple", new XAttribute(W + "instr", "NUMPAGES")))));
        Write(archive, "word/footerDocWizz.xml", footer);
    }
}
