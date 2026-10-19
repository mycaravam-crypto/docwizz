using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Publishing is a distinct operation from generating a draft. Two signatures bound to the exact
// source bytes are required; self-declared "approved" metadata alone can never pass this gate.
internal static class ProductPublication
{
    static readonly string[] RequiredRoles = ["quality", "security"];
    internal sealed record SigningKey(string Id, string Role, string PemPath);
    internal sealed record Approval(string Role, string KeyId, string SignatureBase64);

    internal static string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    // Release paths must remain in the checked-out project. Reject existing symbolic-link
    // ancestors as well, so protected CI cannot be redirected outside its checkout.
    internal static string ConfinedPath(string root, string path)
    {
        var project = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(project, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(project + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("publication path escapes the project repository");
        var cursor = project;
        foreach (var segment in Path.GetRelativePath(project, full).Split(Path.DirectorySeparatorChar))
        {
            cursor = Path.Combine(cursor, segment);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("publication path contains a symbolic link");
        }
        return full;
    }

    static JsonElement Property(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) ? value : throw new ArgumentException($"missing {name}");
    static string Str(JsonElement obj, string name) =>
        Property(obj, name).ValueKind == JsonValueKind.String
            ? Property(obj, name).GetString()!
            : throw new ArgumentException($"{name} must be a string");
    static string SafeRelative(string root, string rel)
    {
        if (Path.IsPathRooted(rel) || rel.Split(['/', '\\']).Any(x => x is ".." or "" or "."))
            throw new ArgumentException("unsafe relative release key path");
        return ConfinedPath(root, rel);
    }

    // Verify the detached PKCS#1 v1.5 / SHA-256 signatures over the exact decoded payload bytes.
    // The trust store must come from a protected, reviewed branch; a user-supplied store alone is
    // not an authority. Re-using a public key across the two roles is explicitly rejected.
    internal static void Verify(string root, string policyPath, string approvalPath,
        ControlledDocument control, string draft, string evidence, string gitCommit)
    {
        using var policy = JsonDocument.Parse(File.ReadAllText(policyPath));
        using var approval = JsonDocument.Parse(File.ReadAllText(approvalPath));
        if (Property(policy.RootElement, "schema").GetInt32() != 1 ||
            Property(approval.RootElement, "schema").GetInt32() != 1)
            throw new ArgumentException("publication policy and approval schema must be 1");
        var trusted = new Dictionary<string, SigningKey>(StringComparer.Ordinal);
        foreach (var item in Property(policy.RootElement, "keys").EnumerateArray())
        {
            var key = new SigningKey(Str(item, "id"), Str(item, "role"), Str(item, "public_key"));
            if (!RequiredRoles.Contains(key.Role) || !trusted.TryAdd(key.Id, key))
                throw new ArgumentException("unrecognized or duplicate trusted signer");
        }
        var payload = Convert.FromBase64String(Str(approval.RootElement, "payload_base64"));
        if (payload.Length > 16384) throw new ArgumentException("signed publication payload too large");
        using var message = JsonDocument.Parse(payload);
        var subject = message.RootElement;
        if (Str(subject, "status") != "approved" ||
            Str(subject, "document_id") != control.Id ||
            Str(subject, "version") != control.Version ||
            Str(subject, "draft_sha256") != Sha256(draft) ||
            Str(subject, "evidence_sha256") != Sha256(evidence) ||
            Str(subject, "control_sha256") != control.Hash ||
            Str(subject, "source_commit") != gitCommit)
            throw new ArgumentException("signed publication payload does not match document/version, sources or commit");

        var signatures = Property(approval.RootElement, "signatures").EnumerateArray().ToArray();
        if (signatures.Length != RequiredRoles.Length)
            throw new ArgumentException("exactly two independent approvals (quality and security) required");
        var used = new HashSet<string>(StringComparer.Ordinal);
        var usedPublicKeys = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in signatures)
        {
            var role = Str(signature, "role");
            var id = Str(signature, "key_id");
            if (!trusted.TryGetValue(id, out var key) || key.Role != role ||
                !used.Add(id) || !roles.Add(role))
                throw new ArgumentException("untrusted, repeated or wrong-role approval");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(SafeRelative(root, key.PemPath)));
            var publicFingerprint = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
            if (!usedPublicKeys.Add(publicFingerprint))
                throw new ArgumentException("different approval roles must use independent public keys");
            if (!rsa.VerifyData(payload, Convert.FromBase64String(Str(signature, "signature_base64")),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new ArgumentException($"invalid signature for role {role}");
        }
        if (RequiredRoles.Any(role => !roles.Contains(role)))
            throw new ArgumentException("independent quality and security signatures required");
    }

    static string Run(string executable, string workDir, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable) {
            WorkingDirectory = workDir, UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new IOException($"unable to launch {executable}");
        // Drain both pipes concurrently; synchronous sequential reads can deadlock a noisy renderer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000)) { process.Kill(entireProcessTree: true); throw new IOException($"{executable} timed out"); }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException($"{executable} failed ({process.ExitCode}): {stderr[..Math.Min(800, stderr.Length)]}");
        return stdout.Trim();
    }

    // This method intentionally accepts only paths and trusted role keys. It never interprets
    // the declared approval rows in the control YAML as a cryptographic authorization.
    internal static void Publish(string root, ProductTemplate template, ControlledDocument control,
        string draft, string evidence, string policy, string approval, string output)
    {
        // Defense in depth: the publishing API itself must not trust even its CLI caller.
        draft = ConfinedPath(root, draft);
        evidence = ConfinedPath(root, evidence);
        policy = ConfinedPath(root, policy);
        approval = ConfinedPath(root, approval);
        output = ConfinedPath(root, output);
        if (!File.Exists(draft) || !File.Exists(evidence) || !File.Exists(policy) || !File.Exists(approval))
            throw new ArgumentException("draft, evidence, trust store and signed approval are all required");
        if (!File.ReadLines(draft).Take(2).Contains("<!-- generated by docwizz product -->"))
            throw new ArgumentException("only DocWizz-generated controlled drafts can be published");

        using (var json = JsonDocument.Parse(File.ReadAllText(evidence)))
        {
            var report = json.RootElement;
            if (Str(report, "product") != template.ProductId ||
                !report.TryGetProperty("document_control", out var dc) ||
                Str(dc, "document_id") != control.Id || Str(dc, "sha256") != control.Hash ||
                Property(Property(report, "required"), "open").GetArrayLength() != 0)
                throw new ArgumentException("draft evidence is incomplete, stale or belongs to another document");
        }
        var head = Run("git", root, "rev-parse", "HEAD");
        if (!Regex.IsMatch(head, "^[0-9a-f]{40}$"))
            throw new ArgumentException("publication requires a Git commit baseline");
        Verify(root, policy, approval, control, draft, evidence, head);

        var finalDir = Path.GetFullPath(output);
        if (Directory.Exists(finalDir) || File.Exists(finalDir))
            throw new IOException("published output already exists; revisions are immutable and must not be overwritten");
        var parent = Path.GetDirectoryName(finalDir)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".docwizz-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var source = Path.Combine(staging, "source.md");
            var markdown = File.ReadAllText(draft);
            // Preserve the already generated clickable contents. Pandoc's --toc creates an
            // empty index on the LibreOffice PDF conversion path unless fields are updated by UNO.
            // Changing signed source text happens exclusively in this ephemeral render copy.
            markdown = markdown.Replace("Gelenkter **Entwurf**", "Gelenkte **Veröffentlichung**", StringComparison.Ordinal)
                .Replace("Entwurf – Genehmigung nicht verifiziert", "Signierte Freigabe durch QM und ISMS geprüft", StringComparison.Ordinal)
                .Replace("Entwurf – keine V-Modell-XT-Konformitäts- oder Freigabeaussage.",
                    "Nach Signaturprüfung veröffentlicht – keine V-Modell-XT-Konformitätsaussage.", StringComparison.Ordinal)
                .Replace("Entwurf – keine Aussage zur betrieblichen Einsatzbereitschaft oder Freigabe.",
                    "Nach Signaturprüfung veröffentlicht – keine Aussage zur betrieblichen Einsatzbereitschaft.", StringComparison.Ordinal)
                .Replace("DocWizz bestätigt keine Freigabe oder Normkonformität.",
                    "Signierte QM- und ISMS-Freigaben geprüft; keine ISO-Zertifizierungsaussage.", StringComparison.Ordinal);
            File.WriteAllText(source, markdown);
            var prefix = control.Id + "-" + control.Version;
            var docx = Path.Combine(staging, prefix + ".docx");
            Run("pandoc", root, "--from=gfm", "--to=docx", "--standalone",
                "--output", docx, source);
            if (!File.Exists(docx) || new FileInfo(docx).Length < 512)
                throw new IOException("DOCX conversion produced no document");
            PublicationLayout.Apply(docx); // cover page, physical page numbers and stable links
            // Isolated LO profile avoids shared-user locks on concurrent CI runners.
            var loProfile = new Uri(Path.Combine(staging, "libreoffice-profile")).AbsoluteUri;
            Run("libreoffice", root, "-env:UserInstallation=" + loProfile, "--headless",
                "--convert-to", "pdf:writer_pdf_Export", "--outdir", staging, docx);
            var pdf = Path.ChangeExtension(docx, ".pdf");
            if (!File.Exists(pdf) || new FileInfo(pdf).Length < 512)
                throw new IOException("PDF conversion produced no document");
            if (!File.ReadAllBytes(pdf).AsSpan(0, 4).SequenceEqual("%PDF"u8))
                throw new IOException("PDF conversion produced invalid header");
            File.Delete(source);
            var manifest = new {
                schema = 1, document_id = control.Id, version = control.Version,
                status = "published-from-signed-sources", source_commit = head,
                source_sha256 = Sha256(draft), evidence_sha256 = Sha256(evidence),
                control_sha256 = control.Hash, approval_sha256 = Sha256(approval),
                docx_sha256 = Sha256(docx), pdf_sha256 = Sha256(pdf),
                independent_approvals = RequiredRoles,
                note = "Signatures verified against governed keys; ISO 9001/27001 certification is not implied."
            };
            File.WriteAllText(Path.Combine(staging, prefix + ".publication.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Directory.Delete(Path.Combine(staging, "libreoffice-profile"), recursive: true);
            Directory.Move(staging, finalDir);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
