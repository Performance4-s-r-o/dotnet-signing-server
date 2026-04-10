using DotNetSigningServer.Models;
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
            var pageSize = page.GetPageSize();

            float x = input.SignRect.X;
            float y = input.SignRect.Y;
            float width = input.SignRect.Width;
            float height = input.SignRect.Height;

            var hasSignImage = !string.IsNullOrEmpty(input.SignImageContent);
            var hasCompanyLogo = !string.IsNullOrEmpty(input.CompanyLogoContent);
            var hasStamp = !string.IsNullOrEmpty(input.StampImageContent);
            var hasBgImage = !string.IsNullOrEmpty(input.BackgroundImageContent);

            var appearanceOptions = input.Appearance ?? new SignatureAppearanceOptions();
            var showReason = appearanceOptions.ShowReason;
            var showDate = appearanceOptions.ShowDate;

            DeviceRgb? fgColor = !string.IsNullOrWhiteSpace(appearanceOptions.ForegroundColor)
                ? PdfCryptoHelper.ParseHexColor(appearanceOptions.ForegroundColor!) : null;
            DeviceRgb? bgColor = !string.IsNullOrWhiteSpace(appearanceOptions.BackgroundColor)
                ? PdfCryptoHelper.ParseHexColor(appearanceOptions.BackgroundColor!) : null;

            PdfFont? font = null;
            if (!string.IsNullOrWhiteSpace(appearanceOptions.FontFamily))
            {
                var fontName = appearanceOptions.FontFamily switch
                {
                    "Times" => StandardFonts.TIMES_ROMAN,
                    "Courier" => StandardFonts.COURIER,
                    _ => StandardFonts.HELVETICA,
                };
                font = PdfFontFactory.CreateFont(fontName);
            }

            float? fontSize = appearanceOptions.FontSize;

            var rect = new Rectangle(x, y, width, height);
            var canvas = new Canvas(new PdfCanvas(page), rect);

            if (hasStamp || hasBgImage || hasCompanyLogo)
            {
                // Full layout: 3-column table matching BuildSignerProperties pattern
                var div = new Div()
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .SetHeight(UnitValue.CreatePercentValue(100));

                if (hasBgImage)
                {
                    var bgData = ImageDataFactory.Create(Convert.FromBase64String(input.BackgroundImageContent!));
                    var shouldRepeat = appearanceOptions?.BackgroundRepeat ?? true;
                    var builder = new BackgroundImage.Builder()
                        .SetImage(new PdfImageXObject(bgData));

                    if (shouldRepeat)
                    {
                        builder.SetBackgroundRepeat(new BackgroundRepeat(BackgroundRepeat.BackgroundRepeatValue.REPEAT));
                    }
                    else
                    {
                        var bgSize = new BackgroundSize();
                        bgSize.SetBackgroundSizeToContain();
                        builder.SetBackgroundSize(bgSize)
                            .SetBackgroundRepeat(new BackgroundRepeat(BackgroundRepeat.BackgroundRepeatValue.NO_REPEAT))
                            .SetBackgroundPosition(new BackgroundPosition().SetPositionX(BackgroundPosition.PositionX.CENTER).SetPositionY(BackgroundPosition.PositionY.CENTER));
                    }

                    div.SetBackgroundImage(builder.Build());
                }
                else if (bgColor != null)
                {
                    div.SetBackgroundColor(bgColor);
                }

                var table = new Table(new float[] { 1, 2, 1 })
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .UseAllAvailableWidth();

                // Left: signature image + company logo
                var leftCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasSignImage)
                {
                    var img = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.SignImageContent!)));
                    img.SetAutoScale(true);
                    leftCell.Add(img);
                }
                if (hasCompanyLogo)
                {
                    var logoImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.CompanyLogoContent!)));
                    logoImg.SetAutoScale(true);
                    leftCell.Add(logoImg);
                }
                table.AddCell(leftCell);

                // Center: text lines
                var centerCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                var lines = BuildVisualSignTextLines(input, appearanceOptions);
                foreach (var line in lines)
                {
                    var p = new Paragraph(line);
                    if (font != null) p.SetFont(font);
                    if (fgColor != null) p.SetFontColor(fgColor);
                    if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                    centerCell.Add(p);
                }
                table.AddCell(centerCell);

                // Right: stamp image
                var rightCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasStamp)
                {
                    var stampImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.StampImageContent!)));
                    stampImg.SetAutoScale(true);
                    rightCell.Add(stampImg);
                }
                table.AddCell(rightCell);

                div.Add(table);
                canvas.Add(div);
            }
            else if (hasSignImage)
            {
                // Image + text layout matching digital signature appearance
                var lines = BuildVisualSignTextLines(input, appearanceOptions);

                if (lines.Count > 0)
                {
                    // 2-column table: [signature image | text lines]
                    var div = new Div()
                        .SetWidth(UnitValue.CreatePercentValue(100))
                        .SetHeight(UnitValue.CreatePercentValue(100));

                    if (bgColor != null)
                        div.SetBackgroundColor(bgColor);

                    var table = new Table(new float[] { 1, 2 })
                        .SetWidth(UnitValue.CreatePercentValue(100))
                        .UseAllAvailableWidth();

                    var imgCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                    var img = new Image(ImageDataFactory.Create(Convert.FromBase64String(input.SignImageContent!)));
                    img.SetAutoScale(true);
                    imgCell.Add(img);
                    table.AddCell(imgCell);

                    var textCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                    foreach (var line in lines)
                    {
                        var p = new Paragraph(line);
                        if (font != null) p.SetFont(font);
                        if (fgColor != null) p.SetFontColor(fgColor);
                        if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                        textCell.Add(p);
                    }
                    table.AddCell(textCell);

                    div.Add(table);
                    canvas.Add(div);
                }
                else
                {
                    // No text lines -- just the image
                    var imgData = ImageDataFactory.Create(Convert.FromBase64String(input.SignImageContent!));
                    var img = new Image(imgData);
                    img.ScaleToFit(width, height);
                    img.SetFixedPosition(x, y);

                    canvas.Close();
                    var layoutDoc = new iText.Layout.Document(pdfDoc);
                    layoutDoc.Add(img);
                    layoutDoc.Flush();
                    pdfDoc.Close();
                    return Convert.ToBase64String(msOut.ToArray());
                }
            }
            else
            {
                // Text-only visual stamp
                var lines = BuildVisualSignTextLines(input, appearanceOptions);
                if (bgColor != null)
                {
                    var div = new Div()
                        .SetWidth(UnitValue.CreatePercentValue(100))
                        .SetHeight(UnitValue.CreatePercentValue(100))
                        .SetBackgroundColor(bgColor);
                    foreach (var line in lines)
                    {
                        var p = new Paragraph(line);
                        if (font != null) p.SetFont(font);
                        if (fgColor != null) p.SetFontColor(fgColor);
                        if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                        div.Add(p);
                    }
                    canvas.Add(div);
                }
                else
                {
                    foreach (var line in lines)
                    {
                        var p = new Paragraph(line);
                        if (font != null) p.SetFont(font);
                        if (fgColor != null) p.SetFontColor(fgColor);
                        if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                        canvas.Add(p);
                    }
                }
            }

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
            bool visible = true)
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

            var subjectDN = chain?.FirstOrDefault()?.GetSubjectDN()?.ToString();
            var cn = subjectDN?.Split(",").Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("CN="))?.Substring(3);

            SignatureFieldAppearance appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);

            // Build text lines based on appearance options (or use defaults)
            var showSignerName = appearanceOptions?.ShowSignerName ?? true;
            var showCompanyName = appearanceOptions?.ShowCompanyName ?? true;
            var showReason = appearanceOptions?.ShowReason ?? true;
            var showDate = appearanceOptions?.ShowDate ?? true;

            var lines = new List<string>();
            if (showSignerName && !string.IsNullOrWhiteSpace(cn))
                lines.Add($"Signed by {cn}");
            if (showCompanyName && !string.IsNullOrWhiteSpace(subjectDN))
            {
                var orgMatch = subjectDN!.Split(",").Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("O="));
                if (orgMatch != null)
                    lines.Add($"Company: {orgMatch.Substring(2)}");
            }
            if (showReason && !string.IsNullOrWhiteSpace(reason))
                lines.Add($"Reason: {reason}");
            if (!string.IsNullOrWhiteSpace(appearanceOptions?.DescriptionText))
                lines.Add(appearanceOptions!.DescriptionText!);

            // Resolve styling
            var hasSignImage = !string.IsNullOrEmpty(signImageContent);
            var hasCompanyLogo = !string.IsNullOrEmpty(companyLogoContent);
            var hasStamp = !string.IsNullOrEmpty(stampImageContent);
            var hasBgImage = !string.IsNullOrEmpty(backgroundImageContent);

            DeviceRgb? fgColor = !string.IsNullOrWhiteSpace(appearanceOptions?.ForegroundColor)
                ? PdfCryptoHelper.ParseHexColor(appearanceOptions!.ForegroundColor!) : null;
            DeviceRgb? bgColor = !string.IsNullOrWhiteSpace(appearanceOptions?.BackgroundColor)
                ? PdfCryptoHelper.ParseHexColor(appearanceOptions!.BackgroundColor!) : null;

            PdfFont? font = null;
            if (!string.IsNullOrWhiteSpace(appearanceOptions?.FontFamily))
            {
                var fontName = appearanceOptions!.FontFamily switch
                {
                    "Times" => StandardFonts.TIMES_ROMAN,
                    "Courier" => StandardFonts.COURIER,
                    _ => StandardFonts.HELVETICA,
                };
                font = PdfFontFactory.CreateFont(fontName);
            }

            float? fontSize = appearanceOptions?.FontSize;

            if (hasStamp || hasBgImage || hasCompanyLogo)
            {
                // Custom Div-based layout for stamp/background/company logo support
                var div = new Div()
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .SetHeight(UnitValue.CreatePercentValue(100));

                // Background: image takes priority over color
                if (hasBgImage)
                {
                    var bgData = ImageDataFactory.Create(Convert.FromBase64String(backgroundImageContent!));
                    var bgSize = new BackgroundSize();
                    bgSize.SetBackgroundSizeToContain();
                    div.SetBackgroundImage(new BackgroundImage.Builder()
                        .SetImage(new PdfImageXObject(bgData))
                        .SetBackgroundSize(bgSize)
                        .Build());
                }
                else if (bgColor != null)
                {
                    div.SetBackgroundColor(bgColor);
                }

                // 3-column table: [signature+logo | text | stamp]
                var table = new Table(new float[] { 1, 2, 1 })
                    .SetWidth(UnitValue.CreatePercentValue(100))
                    .UseAllAvailableWidth();

                // Left: signature image + company logo (stacked vertically)
                var leftCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasSignImage)
                {
                    var img = new Image(ImageDataFactory.Create(Convert.FromBase64String(signImageContent!)));
                    img.SetAutoScale(true);
                    leftCell.Add(img);
                }
                if (hasCompanyLogo)
                {
                    var logoImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(companyLogoContent!)));
                    logoImg.SetAutoScale(true);
                    leftCell.Add(logoImg);
                }
                table.AddCell(leftCell);

                // Center: text paragraphs
                var centerCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                foreach (var line in lines)
                {
                    var p = new Paragraph(line);
                    if (font != null) p.SetFont(font);
                    if (fgColor != null) p.SetFontColor(fgColor);
                    if (fontSize.HasValue) p.SetFontSize(fontSize.Value);
                    centerCell.Add(p);
                }
                table.AddCell(centerCell);

                // Right: stamp image
                var rightCell = new Cell().SetBorder(Border.NO_BORDER).SetVerticalAlignment(VerticalAlignment.MIDDLE);
                if (hasStamp)
                {
                    var stampImg = new Image(ImageDataFactory.Create(Convert.FromBase64String(stampImageContent!)));
                    stampImg.SetAutoScale(true);
                    rightCell.Add(stampImg);
                }
                table.AddCell(rightCell);

                div.Add(table);
                appearance.SetContent(div);
            }
            else
            {
                // Existing path -- backward compatible
                var reasonLine = string.Join("\n", lines);
                var appearanceText = new SignedAppearanceText().SetReasonLine(reasonLine);

                if (hasSignImage)
                {
                    byte[] image = Convert.FromBase64String(signImageContent!);
                    appearance.SetContent(appearanceText, ImageDataFactory.Create(image));
                }
                else
                {
                    appearance.SetContent(appearanceText);
                }

                // Apply color and font customization
                if (fgColor != null)
                    appearance.SetFontColor(fgColor);
                if (bgColor != null)
                    appearance.SetBackgroundColor(bgColor);
                if (fontSize.HasValue)
                    appearance.SetFontSize(fontSize.Value);
                if (font != null)
                    appearance.SetFont(font);
            }

            signerProperties
                .SetPageRect(new Rectangle(signRect.X, signRect.Y, signRect.Width, signRect.Height))
                .SetPageNumber(pageNumber)
                .SetSignatureAppearance(appearance);

            return signerProperties;
        }

        public static List<string> BuildVisualSignTextLines(VisualSignInput input, SignatureAppearanceOptions appearance)
        {
            var lines = new List<string>();
            if (appearance.ShowSignerName && !string.IsNullOrWhiteSpace(input.SignerName))
                lines.Add($"Signed by {input.SignerName}");
            if (appearance.ShowReason && !string.IsNullOrWhiteSpace(input.Reason))
                lines.Add($"Reason: {input.Reason}");
            if (appearance.ShowLocation && !string.IsNullOrWhiteSpace(input.Location))
                lines.Add($"Location: {input.Location}");
            if (appearance.ShowDate)
                lines.Add($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            if (!string.IsNullOrWhiteSpace(appearance.DescriptionText))
                lines.Add(appearance.DescriptionText!);
            return lines;
        }
    }
}
