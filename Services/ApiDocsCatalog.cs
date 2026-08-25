namespace DotNetSigningServer.Services;

/// <summary>
/// One documented API endpoint. Drives both the overview table and its own
/// detail page, so the two cannot drift apart.
///
/// All prose is referenced by SharedStrings resource key rather than stored
/// inline, so the reference reads in the visitor's language. Only the wire
/// samples stay literal — request and response bodies are code, not copy.
/// </summary>
/// <param name="Slug">URL segment under /api/docs/.</param>
/// <param name="Method">HTTP verb shown in the overview table.</param>
/// <param name="Path">Route, e.g. /api/presign.</param>
/// <param name="TitleKey">Resource key for the heading and &lt;title&gt;.</param>
/// <param name="PurposeKey">Resource key for the one-line overview summary.</param>
/// <param name="CreditsKey">Resource key for the credit cost, as displayed.</param>
/// <param name="MetaKey">Resource key for the search-result snippet.</param>
/// <param name="IntroKey">Resource key for the lead paragraph.</param>
/// <param name="Sample">Request/response sample, rendered verbatim in a code block.</param>
/// <param name="Curl">Ready-to-paste cURL command, or null when not applicable.</param>
/// <param name="NoteKeys">Resource keys for extra paragraphs shown above the sample.</param>
public sealed record ApiEndpointDoc(
    string Slug,
    string Method,
    string Path,
    string TitleKey,
    string PurposeKey,
    string CreditsKey,
    string MetaKey,
    string IntroKey,
    string Sample,
    string? Curl = null,
    IReadOnlyList<string>? NoteKeys = null);

/// <summary>
/// The API reference content, split per endpoint so each one is its own
/// indexable URL instead of an anchor on a single long page.
/// </summary>
public static class ApiDocsCatalog
{
    public static IReadOnlyList<ApiEndpointDoc> All(string apiBase)
    {
        var b = string.IsNullOrWhiteSpace(apiBase) ? "/api" : apiBase.TrimEnd('/');

        return new[]
        {
            new ApiEndpointDoc(
                Slug: "presign",
                Method: "POST",
                Path: "/api/presign",
                TitleKey: "ApiDocPresignTitle",
                PurposeKey: "ApiDocPresignPurpose",
                CreditsKey: "ApiDocPresignCredits",
                MetaKey: "ApiDocPresignMeta",
                IntroKey: "ApiDocPresignIntro",
                Sample: """
POST /api/presign
Authorization: Bearer <token>
Content-Type: application/json

{
  "certificatePem": "-----BEGIN CERTIFICATE-----…",
  "pdfContent": "base64-pdf",
  "location": "Prague, CZ",
  "reason": "Approving contract",
  "signRect": { "x": 50, "y": 120, "width": 200, "height": 50 },
  "signPageNumber": 1,
  "signImageContent": "base64-png-optional",
  "fieldName": "Signature1",
  "tsaUrl": "https://freetsa.org/tsr"
}

Response:
{ "id": "ef2c4d7a-…", "hashToSign": "6c3d…9f" }
""",
                Curl: $$"""curl -X POST {{b}}/presign -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"pdfContent":"base64-pdf","location":"Prague, CZ","reason":"Approving contract","signRect":{"x":50,"y":120,"width":200,"height":50},"signPageNumber":1,"fieldName":"Signature1","tsaUrl":"https://freetsa.org/tsr"}'""",
                NoteKeys: new[] { "ApiDocPresignNote1" }),

            new ApiEndpointDoc(
                Slug: "sign",
                Method: "POST",
                Path: "/api/sign",
                TitleKey: "ApiDocSignTitle",
                PurposeKey: "ApiDocSignPurpose",
                CreditsKey: "ApiDocSignCredits",
                MetaKey: "ApiDocSignMeta",
                IntroKey: "ApiDocSignIntro",
                Sample: """
POST /api/sign
Authorization: Bearer <token>
Content-Type: application/json

{
  "id": "ef2c4d7a-…",
  "signedHash": "base64-signature"
}

Response:
{ "result": "base64-pdf-with-signature" }
""",
                Curl: $$"""curl -X POST {{b}}/sign -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"id":"ef2c4d7a-0000-0000-0000-000000000000","signedHash":"base64-signature"}'"""),

            new ApiEndpointDoc(
                Slug: "sign-pfx",
                Method: "POST",
                Path: "/api/sign-pfx",
                TitleKey: "ApiDocSignPfxTitle",
                PurposeKey: "ApiDocSignPfxPurpose",
                CreditsKey: "ApiDocSignPfxCredits",
                MetaKey: "ApiDocSignPfxMeta",
                IntroKey: "ApiDocSignPfxIntro",
                Sample: """
POST /api/sign-pfx
Authorization: Bearer <token>
Content-Type: application/json

{
  "pdfContent": "base64-pdf",
  "pfxContent": "base64-pfx",
  "pfxPassword": "password",
  "location": "Prague, CZ",
  "reason": "Internal approval",
  "signRect": { "x": 40, "y": 100, "width": 180, "height": 45 },
  "signPageNumber": 1,
  "signImageContent": null,
  "fieldName": "Signature1"
}

Response:
{ "result": "base64-pdf-with-signature" }
""",
                Curl: $$"""curl -X POST {{b}}/sign-pfx -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"pdfContent":"base64-pdf","pfxContent":"base64-pfx","pfxPassword":"password","fieldName":"Signature1"}'"""),

            new ApiEndpointDoc(
                Slug: "timestamp",
                Method: "POST",
                Path: "/api/timestamp",
                TitleKey: "ApiDocTimestampTitle",
                PurposeKey: "ApiDocTimestampPurpose",
                CreditsKey: "ApiDocTimestampCredits",
                MetaKey: "ApiDocTimestampMeta",
                IntroKey: "ApiDocTimestampIntro",
                Sample: """
POST /api/timestamp
Authorization: Bearer <token>
Content-Type: application/json

{
  "pdfContent": "base64-pdf",
  "location": "Prague, CZ",
  "reason": "Timestamp",
  "signRect": { "x": 30, "y": 90, "width": 180, "height": 40 },
  "signPageNumber": 1,
  "fieldName": "Timestamp1",
  "tsaUrl": "https://freetsa.org/tsr"
}

Response:
{ "result": "base64-pdf-with-timestamp" }
""",
                Curl: $$"""curl -X POST {{b}}/timestamp -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"pdfContent":"base64-pdf","fieldName":"Timestamp1","tsaUrl":"https://freetsa.org/tsr"}'"""),

            new ApiEndpointDoc(
                Slug: "attachment",
                Method: "POST",
                Path: "/api/attachment",
                TitleKey: "ApiDocAttachmentTitle",
                PurposeKey: "ApiDocAttachmentPurpose",
                CreditsKey: "ApiDocAttachmentCredits",
                MetaKey: "ApiDocAttachmentMeta",
                IntroKey: "ApiDocAttachmentIntro",
                Sample: """
POST /api/attachment
Authorization: Bearer <token>
Content-Type: application/json

{
  "pdfContent": "base64-pdf",
  "attachmentContent": "base64-binary",
  "fileName": "terms.pdf",
  "description": "Terms and conditions",
  "mimeType": "application/pdf"
}

Response:
{ "result": "base64-pdf-with-attachment" }
""",
                Curl: $$"""curl -X POST {{b}}/attachment -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"pdfContent":"base64-pdf","attachmentContent":"base64-binary","fileName":"terms.pdf","mimeType":"application/pdf"}'"""),

            new ApiEndpointDoc(
                Slug: "convert-pdfa",
                Method: "POST",
                Path: "/api/convert/pdfa",
                TitleKey: "ApiDocConvertPdfaTitle",
                PurposeKey: "ApiDocConvertPdfaPurpose",
                CreditsKey: "ApiDocConvertPdfaCredits",
                MetaKey: "ApiDocConvertPdfaMeta",
                IntroKey: "ApiDocConvertPdfaIntro",
                Sample: """
POST /api/convert/pdfa
Authorization: Bearer <token>
Content-Type: application/json

{
  "pdfContent": "base64-pdf",
  "conformance": "PDF/A-2B"
}

Response:
{
  "result": "base64-pdf-a",
  "conformance": "PDF/A-2B"
}
""",
                Curl: $$"""curl -X POST {{b}}/convert/pdfa -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"pdfContent":"base64-pdf","conformance":"PDF/A-2B"}'""",
                NoteKeys: new[] { "ApiDocConvertPdfaNote1", "ApiDocConvertPdfaNote2" }),

            new ApiEndpointDoc(
                Slug: "fill-pdf",
                Method: "POST",
                Path: "/api/fill-pdf",
                TitleKey: "ApiDocFillPdfTitle",
                PurposeKey: "ApiDocFillPdfPurpose",
                CreditsKey: "ApiDocFillPdfCredits",
                MetaKey: "ApiDocFillPdfMeta",
                IntroKey: "ApiDocFillPdfIntro",
                Sample: """
POST /api/fill-pdf
Authorization: Bearer <token>
Content-Type: application/json

{
  "templateId": "a1b2c3d4-…",
  "data": [
    { "data": [ { "fieldName": "Name", "value": "Ada" } ] },
    { "data": [ { "fieldName": "Name", "value": "Grace" } ] }
  ]
}

Response:
{ "files": ["base64-pdf", "base64-pdf"], "templateId": "a1b2c3d4-…" }
""",
                Curl: $$"""curl -X POST {{b}}/fill-pdf -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"templateId":"a1b2c3d4-0000-0000-0000-000000000000","data":[{"data":[{"fieldName":"Name","value":"Ada"}]}]}'""",
                NoteKeys: new[] { "ApiDocFillPdfNote1" }),

            new ApiEndpointDoc(
                Slug: "find-codes",
                Method: "POST",
                Path: "/api/find-codes",
                TitleKey: "ApiDocFindCodesTitle",
                PurposeKey: "ApiDocFindCodesPurpose",
                CreditsKey: "ApiDocFindCodesCredits",
                MetaKey: "ApiDocFindCodesMeta",
                IntroKey: "ApiDocFindCodesIntro",
                Sample: """
POST /api/find-codes
Authorization: Bearer <token>
Content-Type: application/json

{
  "pdfContent": "base64-pdf",
  "codeType": "any"
}

codeType: qr | datamatrix | pdf417 | aztec | any

Response:
{
  "results": [
    { "value": "INV-2024-001", "codeType": "QR_CODE", "position": { "x": 212.5, "y": 412.0 }, "page": 2 }
  ]
}
""",
                Curl: $$"""curl -X POST {{b}}/find-codes -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"pdfContent":"base64-pdf","codeType":"any"}'""",
                NoteKeys: new[] { "ApiDocFindCodesNote1" }),

            new ApiEndpointDoc(
                Slug: "pdf-template-create",
                Method: "POST",
                Path: "/api/pdf-template",
                TitleKey: "ApiDocTemplateCreateTitle",
                PurposeKey: "ApiDocTemplateCreatePurpose",
                CreditsKey: "ApiDocTemplateCreateCredits",
                MetaKey: "ApiDocTemplateCreateMeta",
                IntroKey: "ApiDocTemplateCreateIntro",
                Sample: """
POST /api/pdf-template
Authorization: Bearer <token>
Content-Type: application/json

{
  "pdfContent": "base64-pdf",
  "templateName": "Invoice",
  "fields": [
    {
      "fieldName": "Name",
      "page": 1,
      "rect": { "x": 40, "y": 700, "width": 200, "height": 32 },
      "type": "text",
      "fontSize": 12,
      "fontName": "Helvetica",
      "horizontalAlign": "left"
    }
  ]
}

Response:
{ "templateId": "a1b2c3d4-…" }
""",
                Curl: $$"""curl -X POST {{b}}/pdf-template -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"pdfContent":"base64-pdf","templateName":"Invoice","fields":[{"fieldName":"Name","page":1,"rect":{"x":40,"y":700,"width":200,"height":32},"type":"text"}]}'""",
                NoteKeys: new[] { "ApiDocTemplateCreateNote1", "ApiDocTemplateCreateNote2" }),

            new ApiEndpointDoc(
                Slug: "pdf-template-list",
                Method: "GET",
                Path: "/api/pdf-template",
                TitleKey: "ApiDocTemplateListTitle",
                PurposeKey: "ApiDocTemplateListPurpose",
                CreditsKey: "ApiDocTemplateListCredits",
                MetaKey: "ApiDocTemplateListMeta",
                IntroKey: "ApiDocTemplateListIntro",
                Sample: """
GET /api/pdf-template
Authorization: Bearer <token>

Response:
{
  "templates": [
    {
      "templateId": "a1b2c3d4-…",
      "name": "Invoice",
      "createdAt": "2026-08-25T09:12:44+00:00",
      "fieldCount": 7
    }
  ]
}
""",
                Curl: $$"""curl -H "Authorization: Bearer <token>" {{b}}/pdf-template"""),

            new ApiEndpointDoc(
                Slug: "pdf-template-get",
                Method: "GET",
                Path: "/api/pdf-template/{templateId}",
                TitleKey: "ApiDocTemplateGetTitle",
                PurposeKey: "ApiDocTemplateGetPurpose",
                CreditsKey: "ApiDocTemplateGetCredits",
                MetaKey: "ApiDocTemplateGetMeta",
                IntroKey: "ApiDocTemplateGetIntro",
                Sample: """
GET /api/pdf-template/{templateId}
Authorization: Bearer <token>

Response:
{
  "templateId": "a1b2c3d4-…",
  "name": "Invoice",
  "createdAt": "2026-08-25T09:12:44+00:00",
  "pdfContent": "base64-pdf",
  "fields": [
    { "fieldName": "Name", "page": 1, "rect": { "x": 40, "y": 700, "width": 200, "height": 32 }, "type": "text" }
  ]
}
""",
                Curl: $$"""curl -H "Authorization: Bearer <token>" {{b}}/pdf-template/a1b2c3d4-0000-0000-0000-000000000000""",
                NoteKeys: new[] { "ApiDocTemplateGetNote1" }),

            new ApiEndpointDoc(
                Slug: "pdf-template-update",
                Method: "PUT",
                Path: "/api/pdf-template/{templateId}",
                TitleKey: "ApiDocTemplateUpdateTitle",
                PurposeKey: "ApiDocTemplateUpdatePurpose",
                CreditsKey: "ApiDocTemplateUpdateCredits",
                MetaKey: "ApiDocTemplateUpdateMeta",
                IntroKey: "ApiDocTemplateUpdateIntro",
                Sample: """
PUT /api/pdf-template/{templateId}
Authorization: Bearer <token>
Content-Type: application/json

{
  "templateName": "Invoice 2027",
  "fields": [
    { "fieldName": "Name", "page": 1, "rect": { "x": 40, "y": 700, "width": 200, "height": 32 }, "type": "text" }
  ]
}

Response:
{ "templateId": "a1b2c3d4-…" }
""",
                Curl: $$"""curl -X PUT {{b}}/pdf-template/a1b2c3d4-0000-0000-0000-000000000000 -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"templateName":"Invoice 2027"}'""",
                NoteKeys: new[] { "ApiDocTemplateUpdateNote1" }),

            new ApiEndpointDoc(
                Slug: "pdf-template-delete",
                Method: "DELETE",
                Path: "/api/pdf-template/{templateId}",
                TitleKey: "ApiDocTemplateDeleteTitle",
                PurposeKey: "ApiDocTemplateDeletePurpose",
                CreditsKey: "ApiDocTemplateDeleteCredits",
                MetaKey: "ApiDocTemplateDeleteMeta",
                IntroKey: "ApiDocTemplateDeleteIntro",
                Sample: """
DELETE /api/pdf-template/{templateId}
Authorization: Bearer <token>

Response:
204 No Content
""",
                Curl: $$"""curl -X DELETE -H "Authorization: Bearer <token>" {{b}}/pdf-template/a1b2c3d4-0000-0000-0000-000000000000""",
                NoteKeys: new[] { "ApiDocTemplateDeleteNote1" }),
        };
    }

    public static ApiEndpointDoc? Find(string? slug, string apiBase) =>
        string.IsNullOrWhiteSpace(slug)
            ? null
            : All(apiBase).FirstOrDefault(e =>
                string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
