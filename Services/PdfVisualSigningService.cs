using DotNetSigningServer.Models;
using DotNetSigningServer.Services.SignatureLayout;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.Signatures;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Borders;
using LayoutModule = DotNetSigningServer.Services.SignatureLayout;

namespace DotNetSigningServer.Services
{
    public class PdfVisualSigningService
    {
        public string ApplyVisualSign(VisualSignInput input)
        {
            byte[] pdfBytes = Convert.FromBase64String(input.PdfContent);

            using var msIn = new MemoryStream(pdfBytes);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var writer = new PdfWriter(msOut);
            var pdfDoc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

            int pageNumber = input.SignPageNumber <= 0 ? 1 : input.SignPageNumber;
            var page = pdfDoc.GetPage(pageNumber);

            var appearanceOptions = input.Appearance ?? new SignatureAppearanceOptions();

            var layoutInput = BuildLayoutInput(
                signRect: input.SignRect,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                appearance: appearanceOptions,
                signImage: input.SignImageContent,
                logoImage: input.CompanyLogoContent,
                stampImage: input.StampImageContent,
                reason: input.Reason,
                location: input.Location,
                signerName: input.SignerName,
                companyName: null,
                description: appearanceOptions.DescriptionText,
                dateUtcNow: DateTime.UtcNow);

            var layout = SignatureLayoutCalculator.ComputeLayout(layoutInput);

            float x = input.SignRect.X;
            float y = input.SignRect.Y;
            float width = layout.BoxWidthPt;
            float height = layout.BoxHeightPt;

            // AutoHeight grew the box: anchor to original top edge.
            if (input.AutoHeight == true && Math.Abs(layout.BoxHeightPt - input.SignRect.Height) > 0.01f)
            {
                y = input.SignRect.Y + input.SignRect.Height - layout.BoxHeightPt;
            }

            // Safety net: clamp the final rect to the page. The SPFx client
            // clamps its drag position against the SPFx-predicted height, but
            // if the dotnet layout produces a different (usually larger)
            // height — due to algorithm drift or preview-value divergence —
            // the anchor-at-top shift above can push y negative or x past
            // the page edge. Without this clamp, the signature overflows.
            var pageSize = page.GetPageSize();
            float pageW = pageSize.GetWidth();
            float pageH = pageSize.GetHeight();
            if (width > pageW) width = pageW;
            if (height > pageH) height = pageH;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x + width > pageW) x = pageW - width;
            if (y + height > pageH) y = pageH - height;

            var font = ResolveFont(appearanceOptions.FontFamily);
            var fgColor = ParseColor(appearanceOptions.ForegroundColor);
            var bgColor = ParseColor(appearanceOptions.BackgroundColor);

            var rect = new Rectangle(x, y, width, height);
            var canvas = new Canvas(new PdfCanvas(page), rect);

            var div = BuildSignatureDiv(
                layout: layout,
                signImage: input.SignImageContent,
                logoImage: input.CompanyLogoContent,
                stampImage: input.StampImageContent,
                backgroundImage: input.BackgroundImageContent,
                backgroundRepeat: appearanceOptions.BackgroundRepeat,
                font: font,
                fgColor: fgColor,
                bgColor: bgColor);

            canvas.Add(div);
            canvas.Close();
            pdfDoc.Close();

            return Convert.ToBase64String(msOut.ToArray());
        }

        public static SignerProperties BuildSignerProperties(
            string fieldName,
            SignRect signRect,
            string reason,
            string location,
            int pageNumber,
            string? signImageContent,
            IX509Certificate[]? chain,
            SignatureAppearanceOptions? appearanceOptions = null,
            string? stampImageContent = null,
            string? backgroundImageContent = null,
            string? companyLogoContent = null,
            bool visible = true,
            float? designWidth = null,
            float? designHeight = null,
            bool? autoHeight = null,
            Rectangle? pageSize = null,
            string? signerNameOverride = null)
        {
            SignerProperties signerProperties = new SignerProperties().SetFieldName(fieldName);

            signerProperties
                .SetReason(reason)
                .SetLocation(location)
                .SetCertificationLevel(AccessPermissions.UNSPECIFIED)
                .SetFieldName(fieldName);

            if (!visible)
            {
                return signerProperties;
            }

            var appearanceOptionsLocal = appearanceOptions ?? new SignatureAppearanceOptions();

            var subjectDN = chain?.FirstOrDefault()?.GetSubjectDN()?.ToString();
            var cn = ExtractDnField(subjectDN, "CN=");
            var org = ExtractDnField(subjectDN, "O=");
            // Qualified EU certs often split the name: CN="Jan", GN="Jan", SN="Prasil".
            // When CN is a single token, prefer GN + SN so the signer row shows the
            // full name — matches what the verification page prints.
            if (!string.IsNullOrWhiteSpace(cn) && !cn!.Contains(' '))
            {
                var gn = ExtractDnField(subjectDN, "GN=") ?? ExtractDnField(subjectDN, "G=");
                var sn = ExtractDnField(subjectDN, "SN=");
                if (!string.IsNullOrWhiteSpace(gn) && !string.IsNullOrWhiteSpace(sn))
                {
                    cn = $"{gn} {sn}";
                }
            }
            // Client-provided display name wins when present — it comes from the
            // SSO identity (e.g. Azure AD), which is what the user expects to see.
            if (!string.IsNullOrWhiteSpace(signerNameOverride))
            {
                cn = signerNameOverride;
            }

            var layoutInput = BuildLayoutInput(
                signRect: signRect,
                designWidth: designWidth,
                designHeight: designHeight,
                autoHeight: autoHeight,
                appearance: appearanceOptionsLocal,
                signImage: signImageContent,
                logoImage: companyLogoContent,
                stampImage: stampImageContent,
                reason: reason,
                location: location,
                signerName: cn,
                companyName: org,
                description: appearanceOptionsLocal.DescriptionText,
                dateUtcNow: DateTime.UtcNow);

            var layout = SignatureLayoutCalculator.ComputeLayout(layoutInput);

            var font = ResolveFont(appearanceOptionsLocal.FontFamily);
            var fgColor = ParseColor(appearanceOptionsLocal.ForegroundColor);
            var bgColor = ParseColor(appearanceOptionsLocal.BackgroundColor);

            // SignatureFieldAppearance is a FormField — by default it draws a
            // 1pt border AND keeps a few pt of margin/padding around the
            // content so the rendered signature ends up inset from SignRect.
            // Visual sign goes through Canvas.Add(div) and has no such inset,
            // which is why the two flows render differently. Strip every
            // contributor: border, margin, padding. SetWidth/SetHeight pin
            // the form field to the full SignRect so the Div fills it edge
            // to edge.
            SignatureFieldAppearance appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);
            appearance.SetBorder(Border.NO_BORDER);
            var zero = UnitValue.CreatePointValue(0f);
            appearance.SetProperty(Property.MARGIN_LEFT, zero);
            appearance.SetProperty(Property.MARGIN_RIGHT, zero);
            appearance.SetProperty(Property.MARGIN_TOP, zero);
            appearance.SetProperty(Property.MARGIN_BOTTOM, zero);
            appearance.SetProperty(Property.PADDING_LEFT, zero);
            appearance.SetProperty(Property.PADDING_RIGHT, zero);
            appearance.SetProperty(Property.PADDING_TOP, zero);
            appearance.SetProperty(Property.PADDING_BOTTOM, zero);
            var div = BuildSignatureDiv(
                layout: layout,
                signImage: signImageContent,
                logoImage: companyLogoContent,
                stampImage: stampImageContent,
                backgroundImage: backgroundImageContent,
                backgroundRepeat: appearanceOptionsLocal.BackgroundRepeat,
                font: font,
                fgColor: fgColor,
                bgColor: bgColor);
            appearance.SetContent(div);

            // AutoHeight grew the box: anchor to original top edge.
            float finalX = signRect.X;
            float finalY = signRect.Y;
            float finalW = layout.BoxWidthPt;
            float finalH = layout.BoxHeightPt;
            if (autoHeight == true && Math.Abs(layout.BoxHeightPt - signRect.Height) > 0.01f)
            {
                finalY = signRect.Y + signRect.Height - layout.BoxHeightPt;
            }

            // Safety net: clamp the final rect to the page if caller supplied
            // the page size. See ApplyVisualSign for the same logic — this is
            // reached when the certificate-based flow (PdfSigner + BuildSignerProperties)
            // goes through here. Without the clamp a box near the page edge can
            // overflow when the dotnet-grown height differs from what the SPFx
            // client predicted at placement time.
            if (pageSize != null)
            {
                float pageW = pageSize.GetWidth();
                float pageH = pageSize.GetHeight();
                if (finalW > pageW) finalW = pageW;
                if (finalH > pageH) finalH = pageH;
                if (finalX < 0) finalX = 0;
                if (finalY < 0) finalY = 0;
                if (finalX + finalW > pageW) finalX = pageW - finalW;
                if (finalY + finalH > pageH) finalY = pageH - finalH;

            }

            signerProperties
                .SetPageRect(new Rectangle(finalX, finalY, finalW, finalH))
                .SetPageNumber(pageNumber)
                .SetSignatureAppearance(appearance);

            return signerProperties;
        }

        // ===== shared layout → iText bridge ===================================

        private static LayoutInput BuildLayoutInput(
            SignRect signRect,
            float? designWidth,
            float? designHeight,
            bool? autoHeight,
            SignatureAppearanceOptions appearance,
            string? signImage,
            string? logoImage,
            string? stampImage,
            string reason,
            string location,
            string? signerName,
            string? companyName,
            string? description,
            DateTime dateUtcNow)
        {
            // SignRect is authoritative — user can resize the box in SPFx, and
            // that resized value wins over the design default. Design dimensions
            // are fallback only (older clients that don't send SignRect size).
            // AutoHeight grow-only still applies on top of the seed in ComputeLayout.
            float boxWidth = signRect.Width > 0 ? signRect.Width : (designWidth ?? 200f);
            float boxHeight = signRect.Height > 0 ? signRect.Height : (designHeight ?? 80f);

            // Uniform scale factor between the user-resized box and the original
            // design width. Font size, padding and column gap all scale with it,
            // so shrinking the box visually shrinks everything proportionally
            // (matching the SPFx preview which CSS-scales the whole renderer).
            // Text stays selectable — it's laid out at the scaled pt size, not
            // rasterized into an image.
            float designRefW = designWidth ?? boxWidth;
            float scaleFactor = (designRefW > 0 && boxWidth > 0) ? boxWidth / designRefW : 1f;

            return new LayoutInput
            {
                BoxWidthPt = boxWidth,
                BoxHeightPt = boxHeight,
                AutoHeight = autoHeight ?? false,
                PaddingPt = 4f * scaleFactor,
                ColumnGapPt = 3f * scaleFactor,
                Appearance = new LayoutModule.LayoutAppearance
                {
                    FontFamily = string.IsNullOrWhiteSpace(appearance.FontFamily) ? "Helvetica" : appearance.FontFamily!,
                    FontSize = (appearance.FontSize ?? 10f) * scaleFactor,
                    AutoFontSize = appearance.AutoFontSize ?? false,
                    ShowReason = appearance.ShowReason,
                    ShowLocation = appearance.ShowLocation,
                    ShowDate = appearance.ShowDate,
                    ShowSignerName = appearance.ShowSignerName,
                    ShowCompanyName = appearance.ShowCompanyName,
                    ShowSignature = !string.IsNullOrEmpty(signImage),
                    ShowCompanyLogo = !string.IsNullOrEmpty(logoImage),
                },
                Labels = ResolveLabels(appearance.Labels),
                Values = new LayoutModule.LayoutValues
                {
                    Description = description,
                    Reason = reason,
                    Location = location,
                    Date = appearance.ShowDate ? dateUtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : null,
                    Signer = signerName,
                    Company = companyName,
                },
                Assets = new LayoutModule.LayoutAssets
                {
                    HasSignature = !string.IsNullOrEmpty(signImage),
                    HasCompanyLogo = !string.IsNullOrEmpty(logoImage),
                    HasStamp = !string.IsNullOrEmpty(stampImage),
                },
            };
        }

        private static string? ExtractDnField(string? subjectDN, string prefix)
        {
            if (string.IsNullOrWhiteSpace(subjectDN) || string.IsNullOrWhiteSpace(prefix)) return null;
            var part = subjectDN
                .Split(',')
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return part?.Substring(prefix.Length);
        }

        private static LayoutModule.LayoutLabels ResolveLabels(SignatureLabels? input)
        {
            // Fall back to English when a specific label slot is unset — matches the
            // TS side's getSignatureLabels(normalizeLocale(null)).
            var english = SignatureLayoutCalculator.GetLabelsForLocale("en");
            return new LayoutModule.LayoutLabels
            {
                Reason = input?.Reason ?? english.Reason,
                Location = input?.Location ?? english.Location,
                Date = input?.Date ?? english.Date,
                Signer = input?.Signer ?? english.Signer,
                Company = input?.Company ?? english.Company,
            };
        }

        private static PdfFont? ResolveFont(string? fontFamily)
        {
            if (string.IsNullOrWhiteSpace(fontFamily)) return null;
            return AppFonts.Load(AppFonts.FamilyFromLegacyName(fontFamily));
        }

        private static DeviceRgb? ParseColor(string? hex)
            => !string.IsNullOrWhiteSpace(hex) ? PdfCryptoHelper.ParseHexColor(hex!) : null;

        private static Div BuildSignatureDiv(
            LayoutResult layout,
            string? signImage,
            string? logoImage,
            string? stampImage,
            string? backgroundImage,
            bool backgroundRepeat,
            PdfFont? font,
            DeviceRgb? fgColor,
            DeviceRgb? bgColor)
        {
            // Pin the div to explicit point dimensions matching the layout box.
            // iText's Layout engine is content-driven — SetHeight(100%) ends up
            // sized to the content rather than the canvas rect, so a 300×100
            // box with 3 rows of 10pt text only renders ~65pt tall and the
            // whole signature appears squished (4.6:1 instead of 3:1).
            var div = new Div()
                .SetWidth(layout.BoxWidthPt)
                .SetHeight(layout.BoxHeightPt)
                .SetMinHeight(layout.BoxHeightPt)
                .SetPadding(layout.PaddingPt);

            if (!string.IsNullOrEmpty(backgroundImage))
            {
                var bgData = TryDecodeImageData(backgroundImage!);
                if (bgData != null)
                {
                    var builder = new BackgroundImage.Builder().SetImage(new PdfImageXObject(bgData));
                    if (backgroundRepeat)
                    {
                        builder.SetBackgroundRepeat(new BackgroundRepeat(BackgroundRepeat.BackgroundRepeatValue.REPEAT));
                    }
                    else
                    {
                        var bgSize = new BackgroundSize();
                        bgSize.SetBackgroundSizeToContain();
                        builder
                            .SetBackgroundSize(bgSize)
                            .SetBackgroundRepeat(new BackgroundRepeat(BackgroundRepeat.BackgroundRepeatValue.NO_REPEAT))
                            .SetBackgroundPosition(new BackgroundPosition()
                                .SetPositionX(BackgroundPosition.PositionX.CENTER)
                                .SetPositionY(BackgroundPosition.PositionY.CENTER));
                    }
                    div.SetBackgroundImage(builder.Build());
                }
                else if (bgColor != null)
                {
                    div.SetBackgroundColor(bgColor);
                }
            }
            else if (bgColor != null)
            {
                div.SetBackgroundColor(bgColor);
            }

            if (layout.Columns.Count == 0) return div;

            // Use explicit percent column widths — the Table(float[]) ctor
            // treats its array as absolute point widths (not weights), so
            // [1,3] becomes 1pt + 3pt, and iText's auto-stretching doesn't
            // always preserve the intended ratio. Percent units keep the
            // 25/50/25 (or 25/75, 75/25) split stable at render time.
            float totalWeight = layout.Columns.Sum(c => c.Weight);
            if (totalWeight <= 0) totalWeight = 1f;
            var columnPercents = layout.Columns
                .Select(c => UnitValue.CreatePercentValue(c.Weight * 100f / totalWeight))
                .ToArray();
            var table = new Table(columnPercents)
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetHeight(UnitValue.CreatePercentValue(100));

            // Images are height-bounded to the inner box (box height minus
            // padding). Without an explicit height cap, iText only constrains
            // width, so a wide asset expands its cell's height and inflates
            // the whole signature box — diverging from the preview which
            // always fits images inside the design's height.
            float availableImageHeightPt = Math.Max(0, layout.BoxHeightPt - layout.PaddingPt * 2);

            // Split column gap as half-padding on each inner edge so the total
            // visible space between cells equals layout.ColumnGapPt. iText's
            // Table has no native cell-gap, and percent widths swallow the
            // gap reservation from the layout calc unless we add it back here.
            float halfGap = layout.ColumnGapPt / 2f;
            int colIdx = 0;
            int lastColIdx = layout.Columns.Count - 1;
            foreach (var col in layout.Columns)
            {
                var cell = new Cell()
                    .SetBorder(Border.NO_BORDER)
                    .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                    .SetPadding(0);
                if (colIdx > 0) cell.SetPaddingLeft(halfGap);
                if (colIdx < lastColIdx) cell.SetPaddingRight(halfGap);

                switch (col.Kind)
                {
                    case ColumnKind.Left:
                        {
                            bool hasSign = !string.IsNullOrEmpty(signImage);
                            bool hasLogo = !string.IsNullOrEmpty(logoImage);
                            int imgCount = (hasSign ? 1 : 0) + (hasLogo ? 1 : 0);
                            float perImageH = imgCount > 0 ? availableImageHeightPt / imgCount : availableImageHeightPt;

                            if (hasSign)
                            {
                                var img = TryCreateFittedImage(signImage!, col.WidthPt, perImageH);
                                if (img != null) cell.Add(img);
                            }
                            if (hasLogo)
                            {
                                var img = TryCreateFittedImage(logoImage!, col.WidthPt, perImageH);
                                if (img != null) cell.Add(img);
                            }
                            break;
                        }

                    case ColumnKind.Middle:
                        foreach (var row in layout.Rows)
                        {
                            var p = new Paragraph(row.Text)
                                .SetFontSize(layout.FontSizePt)
                                .SetMargin(0)
                                .SetFixedLeading(layout.LineHeightPt);
                            if (font != null) p.SetFont(font);
                            if (fgColor != null) p.SetFontColor(fgColor);
                            cell.Add(p);
                        }
                        break;

                    case ColumnKind.Right:
                        if (!string.IsNullOrEmpty(stampImage))
                        {
                            var img = TryCreateFittedImage(stampImage!, col.WidthPt, availableImageHeightPt);
                            if (img != null) cell.Add(img);
                        }
                        break;
                }
                table.AddCell(cell);
                colIdx++;
            }

            div.Add(table);
            return div;
        }

        private static Image? TryCreateFittedImage(string base64, float maxWidthPt, float maxHeightPt)
        {
            var data = TryDecodeImageData(base64);
            if (data == null) return null;
            var img = new Image(data);
            if (maxWidthPt > 0 && maxHeightPt > 0)
            {
                img.ScaleToFit(maxWidthPt, maxHeightPt);
            }
            return img;
        }

        // Accepts either raw base64 or a `data:image/<fmt>;base64,<data>` URI
        // and returns ImageData, or null when the bytes can't be parsed as an
        // image iText supports (e.g. SVG, unknown format, malformed payload).
        // Failing soft keeps the sign operation alive — a missing stamp is
        // preferable to aborting the whole signature.
        private static iText.IO.Image.ImageData? TryDecodeImageData(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var payload = input;
            var commaIdx = payload.IndexOf(',');
            if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIdx > 0)
            {
                payload = payload.Substring(commaIdx + 1);
            }
            try
            {
                var bytes = Convert.FromBase64String(payload);
                return ImageDataFactory.Create(bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[PdfVisualSigning] TryDecodeImageData failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // Kept for any external callers that still build text lines the old way.
        // Internal signing pipeline now routes everything through the shared layout module.
        public static List<string> BuildVisualSignTextLines(VisualSignInput input, SignatureAppearanceOptions appearance)
        {
            var layoutInput = BuildLayoutInput(
                signRect: input.SignRect,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                appearance: appearance,
                signImage: input.SignImageContent,
                logoImage: input.CompanyLogoContent,
                stampImage: input.StampImageContent,
                reason: input.Reason,
                location: input.Location,
                signerName: input.SignerName,
                companyName: null,
                description: appearance.DescriptionText,
                dateUtcNow: DateTime.UtcNow);
            var rows = SignatureLayoutCalculator.BuildRows(layoutInput.Appearance, layoutInput.Labels, layoutInput.Values);
            return rows.Select(r => r.Text).ToList();
        }
    }
}
