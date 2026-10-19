#!/usr/bin/env bash
set -euo pipefail
dll="$1"
root="$(mktemp -d)"
trap 'rm -rf "$root"' EXIT
cd "$root"
git init -q
git config user.name "DocWizz CI"
git config user.email "ci@example.invalid"
mkdir -p docs .github/release-keys keys
echo "Evidence from a fictional project." > docs/source.md
echo "Reviewed fictional document." > docs/review.md
echo "Approved fictional document." > docs/approval.md
echo "public class Fixture {}" > Example.cs
git add . && git commit -q -m "Fixture baseline"
cat > template.yaml <<'YAML'
schema: 1
product_id: ci-release
xt_variant: V-Modell XT
xt_version: "2.4"
tailoring: test-only
sections:
  - id: scope
    title: Prüfgegenstand
    required: true
    sources: [project]
dependencies: []
YAML
cat > context.yaml <<'YAML'
schema: 1
statements:
  - section: scope
    text: Fiktives Prüfobjekt mit belegter Quelle.
    source: docs/source.md
YAML
cat > control.yaml <<'YAML'
schema: 1
document_id: CI-RELEASE
title: Fiktive SW-Spezifikation
version: "0.1"
author: CI-Autor
owner: CI-Verantwortung
created: 2026-10-19
classification: Testdaten
distribution: CI-intern
retention: Nur für den Testlauf
changes:
  - version: "0.1"
    date: 2026-10-19
    author: CI-Autor
    description: Fiktive Erstausgabe
approvals:
  - kind: review
    role: quality
    person: CI-Prüfer
    date: 2026-10-19
    evidence: docs/review.md
  - kind: approval
    role: security
    person: CI-Freigebender
    date: 2026-10-19
    evidence: docs/approval.md
YAML
dotnet "$dll" product "$root" "$root/template.yaml" --context "$root/context.yaml" --control "$root/control.yaml"
dotnet "$dll" product check "$root" "$root/template.yaml" --context "$root/context.yaml" --control "$root/control.yaml"
for role in quality security; do
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "keys/$role.private.pem" 2>/dev/null
  openssl pkey -in "keys/$role.private.pem" -pubout -out ".github/release-keys/$role.pem" 2>/dev/null
done
cat > policy.json <<'JSON'
{"schema":1,"keys":[
  {"id":"quality-ci","role":"quality","public_key":".github/release-keys/quality.pem"},
  {"id":"security-ci","role":"security","public_key":".github/release-keys/security.pem"}
]}
JSON
python3 - <<'PY'
import base64, hashlib, json, pathlib, subprocess
root = pathlib.Path.cwd()
def sha(path): return hashlib.sha256((root / path).read_bytes()).hexdigest()
message = {
    "status":"approved", "document_id":"CI-RELEASE", "version":"0.1",
    "draft_sha256":sha("docs/products/ci-release.md"),
    "evidence_sha256":sha("docs/products/ci-release.evidence.json"),
    "control_sha256":sha("control.yaml"),
    "source_commit":subprocess.check_output(["git","rev-parse","HEAD"],text=True).strip()
}
blob = json.dumps(message, sort_keys=True, separators=(",",":")).encode()
(root / "signed-payload.bin").write_bytes(blob)
signatures = []
for role in ("quality","security"):
    subprocess.run(["openssl","dgst","-sha256","-sign",f"keys/{role}.private.pem",
                    "-out",f"keys/{role}.sig","signed-payload.bin"],check=True)
    signatures.append({
        "role":role, "key_id":f"{role}-ci",
        "signature_base64":base64.b64encode((root / f"keys/{role}.sig").read_bytes()).decode()
    })
approval={"schema":1,"payload_base64":base64.b64encode(blob).decode(),"signatures":signatures}
(root/"signed-approval.json").write_text(json.dumps(approval),encoding="utf8")
bad=json.loads(json.dumps(approval))
bad["signatures"][1]["signature_base64"]=bad["signatures"][0]["signature_base64"]
(root/"bad-approval.json").write_text(json.dumps(bad),encoding="utf8")
PY
publish() {
  dotnet "$dll" product publish "$root" "$root/template.yaml" "$root/output" \
    --context "$root/context.yaml" --control "$root/control.yaml" \
    --trust "$root/policy.json" --approval "$1"
}
if publish "$root/bad-approval.json"; then echo "Invalid signature accepted" >&2; exit 1; fi
# Two role IDs cannot share one actual public key, even if the IDs differ.
python3 - <<'PY'
import json
p=json.load(open("policy.json"))
p["keys"][1]["public_key"]=p["keys"][0]["public_key"]
with open("duplicate-key-policy.json","w") as f: json.dump(p,f)
PY
if dotnet "$dll" product publish "$root" "$root/template.yaml" "$root/duplicate-key" \
  --context "$root/context.yaml" --control "$root/control.yaml" \
  --trust "$root/duplicate-key-policy.json" --approval "$root/signed-approval.json"; then
  echo "Same public key accepted twice" >&2; exit 1
fi
test ! -e duplicate-key
test ! -e output
publish "$root/signed-approval.json"
test -s output/CI-RELEASE-0.1.docx
test -s output/CI-RELEASE-0.1.pdf
test -s output/CI-RELEASE-0.1.publication.json
echo "Fictional PDF conversion summary:"
pdfinfo output/CI-RELEASE-0.1.pdf | grep -E 'Pages:|Page size:'
pdftotext -layout output/CI-RELEASE-0.1.pdf - | head -85
pdfinfo output/CI-RELEASE-0.1.pdf | grep -q '^Pages:'
pdfinfo output/CI-RELEASE-0.1.pdf | awk '/^Pages:/ { if ($2 < 2) exit 1; found=1 } END { if (!found) exit 1 }'
pdftotext output/CI-RELEASE-0.1.pdf - | grep -q 'Inhaltsverzeichnis'
pdftotext output/CI-RELEASE-0.1.pdf - | grep -q 'Seite 1'

pdftotext output/CI-RELEASE-0.1.pdf - | grep -q 'Fiktive'
pdftoppm -f 1 -l 1 -scale-to 1000 -png -singlefile output/CI-RELEASE-0.1.pdf first-page >/dev/null 2>&1
test -s first-page.png
python3 - <<'PY'
import json, zipfile
with zipfile.ZipFile("output/CI-RELEASE-0.1.docx") as doc:
    assert "word/document.xml" in doc.namelist()
    xml = doc.read("word/document.xml")
    assert b"TOC" in xml or b"Inhaltsverzeichnis" in xml
manifest=json.load(open("output/CI-RELEASE-0.1.publication.json"))
assert manifest["status"]=="published-from-signed-sources"
assert set(manifest["independent_approvals"])=={"quality","security"}
PY
if publish "$root/signed-approval.json"; then echo "Existing release overwritten" >&2; exit 1; fi
printf '\nManipulation\n' >> docs/products/ci-release.md
if dotnet "$dll" product publish "$root" "$root/template.yaml" "$root/unapproved" \
  --context "$root/context.yaml" --control "$root/control.yaml" \
  --trust "$root/policy.json" --approval "$root/signed-approval.json"; then
  echo "Tampered source published" >&2; exit 1
fi
test ! -e unapproved
echo "Publication smoke tests PASS"
