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
using iText.IO.Font.Constants;
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

            // When AutoHeight grew the box, shift Y down so the new height stays anchored at the original top edge.
            if (layout.BoxHeightPt != input.SignRect.Height)
            {
                y = input.SignRect.Y + input.SignRect.Height - layout.BoxHeightPt;
            }

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
            bool? autoHeight = null)
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
            var cn = subjectDN?
                .Split(",")
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith("CN="))?
                .Substring(3);
            var org = subjectDN?
                .Split(",")
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith("O="))?
                .Substring(2);

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

            SignatureFieldAppearance appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);
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
            float finalY = signRect.Y;
            if (autoHeight == true && Math.Abs(layout.BoxHeightPt - signRect.Height) > 0.01f)
            {
                finalY = signRect.Y + signRect.Height - layout.BoxHeightPt;
            }
            signerProperties
                .SetPageRect(new Rectangle(signRect.X, finalY, layout.BoxWidthPt, layout.BoxHeightPt))
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
            return new LayoutInput
            {
                BoxWidthPt = designWidth ?? signRect.Width,
                BoxHeightPt = designHeight ?? signRect.Height,
                AutoHeight = autoHeight ?? false,
                Appearance = new LayoutModule.LayoutAppearance
                {
                    FontFamily = string.IsNullOrWhiteSpace(appearance.FontFamily) ? "Helvetica" : appearance.FontFamily!,
                    FontSize = appearance.FontSize ?? 10f,
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
            var fontName = fontFamily switch
            {
                "Times" => StandardFonts.TIMES_ROMAN,
                "Courier" => StandardFonts.COURIER,
                _ => StandardFonts.HELVETICA,
            };
            return PdfFontFactory.CreateFont(fontName);
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
            var div = new Div()
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetHeight(UnitValue.CreatePercentValue(100))
                .SetPadding(layout.PaddingPt);

            if (!string.IsNullOrEmpty(backgroundImage))
            {
                var bgData = ImageDataFactory.Create(Convert.FromBase64String(backgroundImage!));
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

            if (layout.Columns.Count == 0) return div;

            var weights = layout.Columns.Select(c => c.Weight).ToArray();
            var table = new Table(weights)
                .SetWidth(UnitValue.CreatePercentValue(100))
                .UseAllAvailableWidth();

            foreach (var col in layout.Columns)
            {
                var cell = new Cell()
                    .SetBorder(Border.NO_BORDER)
                    .SetVerticalAlignment(VerticalAlignment.MIDDLE);

                switch (col.Kind)
                {
                    case ColumnKind.Left:
                        if (!string.IsNullOrEmpty(signImage))
                            cell.Add(CreateAutoScaledImage(signImage!));
                        if (!string.IsNullOrEmpty(logoImage))
                            cell.Add(CreateAutoScaledImage(logoImage!));
                        break;

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
                            cell.Add(CreateAutoScaledImage(stampImage!));
                        break;
                }
                table.AddCell(cell);
            }

            div.Add(table);
            return div;
        }

        private static Image CreateAutoScaledImage(string base64)
        {
            var img = new Image(ImageDataFactory.Create(Convert.FromBase64String(base64)));
            img.SetAutoScale(true);
            return img;
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
