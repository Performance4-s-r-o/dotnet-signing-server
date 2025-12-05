using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Borders;
using iText.Kernel.Colors;
using iText.Barcodes;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace DotNetSigningServer.Services;

public class PdfTemplateService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<PdfTemplateService> _logger;
    private readonly ContentLimitGuard _limitGuard;

    public PdfTemplateService(ApplicationDbContext dbContext, ILogger<PdfTemplateService> logger, ContentLimitGuard limitGuard)
    {
        _dbContext = dbContext;
        _logger = logger;
        _limitGuard = limitGuard;
    }

    public async Task<CreateTemplateResponse> CreateTemplateAsync(CreateTemplateInput input, Guid userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.PdfContent))
        {
            throw new ArgumentException("PdfContent is required.", nameof(input.PdfContent));
        }
        if (input.Fields == null || input.Fields.Count == 0)
        {
            throw new ArgumentException("At least one field definition is required.", nameof(input.Fields));
        }

        ValidateFields(input.Fields);
        _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Template");

        var entity = new StoredPdfTemplate
        {
            UserId = userId,
            Base64Content = input.PdfContent,
            FieldsJson = JsonSerializer.Serialize(input.Fields),
            Name = string.IsNullOrWhiteSpace(input.TemplateName) ? null : input.TemplateName!.Trim()
        };

        _dbContext.StoredPdfTemplates.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CreateTemplateResponse { TemplateId = entity.Id };
    }

    public async Task<FillPdfResponse> FillAsync(FillPdfInput input, Guid userId, CancellationToken cancellationToken = default)
    {
        if (input.TemplateId == null && string.IsNullOrWhiteSpace(input.PdfContent))
        {
            throw new ArgumentException("PdfContent is required when TemplateId is not provided.", nameof(input.PdfContent));
        }

        if (input.TemplateId == null && (input.Fields == null || input.Fields.Count == 0))
        {
            throw new ArgumentException("At least one field definition is required.", nameof(input.Fields));
        }

        if (input.TemplateId == null)
        {
            ValidateFields(input.Fields);
            _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Fill PDF");
        }

        if (input.TemplateId != null)
        {
            var template = await GetTemplateForUserAsync(input.TemplateId.Value, userId, cancellationToken);
            _limitGuard.EnsurePdfWithinLimit(template.Base64Content, "Template");
        }

        if (input.Data == null || input.Data.Count == 0)
        {
            throw new ArgumentException("At least one data set is required to fill the PDF.", nameof(input.Data));
        }

        var templateBytes = await ResolveTemplateBytesAsync(input, userId, cancellationToken);
        var fields = await ResolveFieldsAsync(input, userId, cancellationToken);
        var results = new List<string>();

        foreach (var dataset in input.Data)
        {
            var filled = FillSingle(templateBytes, fields, dataset);
            results.Add(Convert.ToBase64String(filled));
        }

        return new FillPdfResponse
        {
            Files = results,
            TemplateId = input.TemplateId
        };
    }

    private async Task<byte[]> ResolveTemplateBytesAsync(FillPdfInput input, Guid userId, CancellationToken cancellationToken)
    {
        if (input.TemplateId == null)
        {
            _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Fill PDF");
            return Convert.FromBase64String(input.PdfContent);
        }

        var template = await GetTemplateForUserAsync(input.TemplateId.Value, userId, cancellationToken);

        return Convert.FromBase64String(template.Base64Content);
    }

    private async Task<List<PdfFieldDefinition>> ResolveFieldsAsync(FillPdfInput input, Guid userId, CancellationToken cancellationToken)
    {
        if (input.TemplateId == null)
        {
            return input.Fields;
        }

        var template = await GetTemplateForUserAsync(input.TemplateId.Value, userId, cancellationToken);

        var fields = JsonSerializer.Deserialize<List<PdfFieldDefinition>>(template.FieldsJson);
        if (fields == null || fields.Count == 0)
        {
            throw new InvalidOperationException("Stored template does not contain field definitions.");
        }

        return fields;
    }

    public async Task<IReadOnlyCollection<TemplateSummary>> ListTemplatesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.StoredPdfTemplates
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Name, t.CreatedAt, t.FieldsJson })
            .ToListAsync(cancellationToken);

        return rows
            .Select(t => new TemplateSummary
            {
                TemplateId = t.Id,
                Name = t.Name,
                CreatedAt = t.CreatedAt,
                FieldCount = (JsonSerializer.Deserialize<List<PdfFieldDefinition>>(t.FieldsJson, (JsonSerializerOptions?)null) ?? new List<PdfFieldDefinition>()).Count
            })
            .ToList();
    }

    public async Task<TemplateDetail> GetTemplateAsync(Guid templateId, Guid userId, CancellationToken cancellationToken = default)
    {
        var template = await GetTemplateForUserAsync(templateId, userId, cancellationToken);
        var fields = JsonSerializer.Deserialize<List<PdfFieldDefinition>>(template.FieldsJson) ?? new List<PdfFieldDefinition>();

        return new TemplateDetail
        {
            TemplateId = template.Id,
            Name = template.Name,
            CreatedAt = template.CreatedAt,
            PdfContent = template.Base64Content,
            Fields = fields
        };
    }

    public async Task<CreateTemplateResponse> UpdateTemplateAsync(Guid templateId, Guid userId, UpdateTemplateInput input, CancellationToken cancellationToken = default)
    {
        var template = await GetTemplateForUserAsync(templateId, userId, cancellationToken);

        if (input.Fields != null && input.Fields.Count > 0)
        {
            ValidateFields(input.Fields);
            template.FieldsJson = JsonSerializer.Serialize(input.Fields);
        }

        if (!string.IsNullOrWhiteSpace(input.PdfContent))
        {
            _limitGuard.EnsurePdfWithinLimit(input.PdfContent, "Template");
            template.Base64Content = input.PdfContent;
        }

        if (input.TemplateName != null)
        {
            template.Name = string.IsNullOrWhiteSpace(input.TemplateName) ? null : input.TemplateName.Trim();
        }

        _dbContext.StoredPdfTemplates.Update(template);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CreateTemplateResponse { TemplateId = template.Id };
    }

    public async Task DeleteTemplateAsync(Guid templateId, Guid userId, CancellationToken cancellationToken = default)
    {
        var template = await GetTemplateForUserAsync(templateId, userId, cancellationToken);
        _dbContext.StoredPdfTemplates.Remove(template);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<StoredPdfTemplate> GetTemplateForUserAsync(Guid templateId, Guid userId, CancellationToken cancellationToken)
    {
        var template = await _dbContext.StoredPdfTemplates.FindAsync(new object[] { templateId }, cancellationToken);
        if (template == null || template.UserId != userId)
        {
            throw new InvalidOperationException("Template not found or not accessible for the current user.");
        }

        return template;
    }

    private static byte[] FillSingle(byte[] templateBytes, IReadOnlyCollection<PdfFieldDefinition> fields, FillDataSet dataSet)
    {
        var valueLookup = (dataSet?.Data ?? new List<PdfFieldValue>()).ToDictionary(
            k => k.FieldName,
            v => v,
            StringComparer.OrdinalIgnoreCase);

        using var msIn = new MemoryStream(templateBytes);
        using var msOut = new MemoryStream();
        var reader = new PdfReader(msIn);
        var writer = new PdfWriter(msOut);
        var pdfDoc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

        foreach (var field in fields)
        {
            if (!valueLookup.TryGetValue(field.FieldName, out var provided))
            {
                continue;
            }

            if (string.Equals(field.Type, "signature", StringComparison.OrdinalIgnoreCase))
            {
                // Signature fields are filled via presign/sign/timestamp endpoints only.
                continue;
            }

            var value = provided.Value ?? string.Empty;
            var pageNumber = field.Page <= 0 ? 1 : field.Page;
            if (pageNumber > pdfDoc.GetNumberOfPages())
            {
                pageNumber = pdfDoc.GetNumberOfPages();
            }
            var page = pdfDoc.GetPage(pageNumber);
            var rect = NormalizeRectForRotation(page, new Rectangle(field.Rect.X, field.Rect.Y, field.Rect.Width, field.Rect.Height));

            if (string.Equals(field.Type, "image", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    TryAddImage(value, pdfDoc, pageNumber, rect);
                }
                continue;
            }
            if (string.Equals(field.Type, "barcode", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    TryAddBarcode(value, pdfDoc, pageNumber, rect, field);
                }
                continue;
            }
            if (string.Equals(field.Type, "table", StringComparison.OrdinalIgnoreCase))
            {
                if (provided.TableValue != null)
                {
                    TryAddTable(provided.TableValue, pdfDoc, pageNumber, rect, field);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            AddText(value, pdfDoc, pageNumber, rect, field);
        }

        pdfDoc.Close();
        return msOut.ToArray();
    }

    private static void AddText(string value, PdfDocument pdfDoc, int pageNumber, Rectangle rect, PdfFieldDefinition field)
    {
        var page = pdfDoc.GetPage(pageNumber);
        // var canvas = new Canvas(new PdfCanvas(page), page.GetPageSize());
        var fontName = ResolveFont(field.FontName, field.FontWeight);
        var horiz = (field.HorizontalAlign ?? "left").ToLowerInvariant();
        var vert = (field.VerticalAlign ?? "center").ToLowerInvariant();
        var paragraph = new Paragraph(value)
            .SetFontSize(field.FontSize <= 0 ? 12 : field.FontSize)
            .SetTextAlignment(
                horiz switch
                {
                    "center" => TextAlignment.CENTER,
                    "right" => TextAlignment.RIGHT,
                    _ => TextAlignment.LEFT
                }
            )
            .SetVerticalAlignment(vert switch
            {
                "top" => VerticalAlignment.TOP,
                "bottom" => VerticalAlignment.BOTTOM,
                _ => VerticalAlignment.MIDDLE
            })
            .SetMargin(0)
            // .SetMultipliedLeading(1.1f)
            .SetFixedPosition(pageNumber, rect.GetX(), rect.GetY(), rect.GetWidth());

        if (!string.IsNullOrWhiteSpace(fontName))
        {
            paragraph.SetFont(iText.Kernel.Font.PdfFontFactory.CreateFont(fontName));
        }

        // var textAlign = horiz switch
        // {
        //     "center" => TextAlignment.CENTER,
        //     "right" => TextAlignment.RIGHT,
        //     _ => TextAlignment.LEFT
        // };
        // var vAlign = vert switch
        // {
        //     "top" => VerticalAlignment.TOP,
        //     "bottom" => VerticalAlignment.BOTTOM,
        //     _ => VerticalAlignment.MIDDLE
        // };

        // var anchorX = horiz switch
        // {
        //     "center" => rect.GetX() + rect.GetWidth() / 2,
        //     "right" => rect.GetX() + rect.GetWidth(),
        //     _ => rect.GetX()
        // };
        // var anchorY = vert switch
        // {
        //     "top" => rect.GetY() + rect.GetHeight(),
        //     "bottom" => rect.GetY(),
        //     _ => rect.GetY() + rect.GetHeight() / 2
        // };

        // canvas.ShowTextAligned(paragraph, anchorX, anchorY, pageNumber, textAlign, vAlign, 0);
        // canvas.Add(paragraph);
        // canvas.Close();

        var pdfCanvas = new PdfCanvas(page);
        pdfCanvas.SaveState();

        try
        {
            var canvas = new Canvas(pdfCanvas, rect);
            canvas.Add(paragraph);
            canvas.Close();
        }
        finally
        {
            pdfCanvas.RestoreState();
        }
    }

    private static void TryAddImage(string base64, PdfDocument pdfDoc, int pageNumber, Rectangle rect)
    {
        try
        {
            var imageBytes = Convert.FromBase64String(base64);
            var imgData = ImageDataFactory.Create(imageBytes);
            var image = new Image(imgData)
                .ScaleToFit(rect.GetWidth(), rect.GetHeight())
                .SetFixedPosition(pageNumber, rect.GetX(), rect.GetY());

            var page = pdfDoc.GetPage(pageNumber);
            var pdfCanvas = new PdfCanvas(page);
            pdfCanvas.SaveState();

            try
            {
                var canvas = new Canvas(pdfCanvas, rect);
                canvas.Add(image);
                canvas.Close();
            }
            finally
            {
                pdfCanvas.RestoreState();
            }
        }
        catch
        {
            // ignore invalid image content and continue with the rest
        }
    }

    private static void TryAddBarcode(string value, PdfDocument pdfDoc, int pageNumber, Rectangle rect, PdfFieldDefinition field)
    {
        try
        {
            var format = (field.BarcodeFormat ?? "code128").ToLowerInvariant();
            Image? barcodeImage = format switch
            {
                "qr" or "qrcode" or "qr-code" => CreateQrCode(pdfDoc, value),
                "datamatrix" or "data-matrix" or "dm" => CreateDataMatrix(pdfDoc, value),
                "pdf417" => CreatePdf417(pdfDoc, value),
                "ean13" or "ean-13" => CreateEan(pdfDoc, value, BarcodeEAN.EAN13),
                "ean8" or "ean-8" => CreateEan(pdfDoc, value, BarcodeEAN.EAN8),
                "upc" or "upca" or "upc-a" => CreateEan(pdfDoc, value, BarcodeEAN.UPCA),
                "code39" or "code-39" => CreateCode39(pdfDoc, value),
                "itf" or "interleaved2of5" or "i2of5" => CreateInterleaved25(pdfDoc, value),
                _ => CreateCode128(pdfDoc, value),
            };

            if (barcodeImage == null) return;

            barcodeImage.ScaleToFit(rect.GetWidth(), rect.GetHeight());
            barcodeImage.SetFixedPosition(pageNumber, rect.GetX(), rect.GetY());

            var page = pdfDoc.GetPage(pageNumber);
            var pdfCanvas = new PdfCanvas(page);
            pdfCanvas.SaveState();

            try
            {
                var canvas = new Canvas(pdfCanvas, rect);
                canvas.Add(barcodeImage);
                canvas.Close();
            }
            finally
            {
                pdfCanvas.RestoreState();
            }
        }
        catch
        {
            // ignore barcode render errors
        }
    }

    private static void TryAddTable(List<List<string>> rows, PdfDocument pdfDoc, int pageNumber, Rectangle rect, PdfFieldDefinition field)
    {
        var page = pdfDoc.GetPage(pageNumber);
        var columns = field.TableColumns ?? new List<TableColumnDefinition>();
        if (columns.Count == 0)
        {
            columns.Add(new TableColumnDefinition
            {
                Name = "Column 1",
                WidthPercent = 100,
                FontSize = field.FontSize,
                FontWeight = field.FontWeight,
                BorderStyle = "none"
            });
        }

        var columnCount = Math.Max(1, columns.Count);
        var widths = columns.Select(c => c.WidthPercent ?? (100f / columnCount)).ToList();
        var widthSum = widths.Sum();
        if (Math.Abs(widthSum) < 0.001f)
        {
            widthSum = 100f;
        }
        var normalized = widths.Select(w => (float)(rect.GetWidth() * (w / widthSum))).ToArray();

        var table = new Table(normalized);
        table.SetFixedPosition(pageNumber, rect.GetX(), rect.GetY(), rect.GetWidth());
        table.SetBorder(Border.NO_BORDER);
        table.SetPadding(0);
        table.SetMargin(0);

        var effectiveRows = rows?.Count > 0
            ? rows
            : new List<List<string>> { columns.Select(c => c.Name ?? string.Empty).ToList() };

        foreach (var row in effectiveRows)
        {
            for (int i = 0; i < columnCount; i++)
            {
                var col = columns[Math.Min(i, columns.Count - 1)];
                var cellText = i < row.Count ? row[i] ?? string.Empty : string.Empty;
                var paragraph = new Paragraph(cellText)
                    .SetMargin(0)
                    .SetPadding(0)
                    .SetTextAlignment((col.HorizontalAlign ?? "left").ToLowerInvariant() switch
                    {
                        "center" => TextAlignment.CENTER,
                        "right" => TextAlignment.RIGHT,
                        _ => TextAlignment.LEFT
                    })
                    .SetFontSize(col.FontSize <= 0 ? field.FontSize : col.FontSize)
                    .SetMultipliedLeading(1.1f);

                var colFont = ResolveFont(field.FontName, col.FontWeight ?? field.FontWeight);
                if (!string.IsNullOrWhiteSpace(colFont))
                {
                    paragraph.SetFont(iText.Kernel.Font.PdfFontFactory.CreateFont(colFont));
                }

                var cell = new Cell().Add(paragraph)
                    .SetPadding(4)
                    .SetVerticalAlignment((col.VerticalAlign ?? "center").ToLowerInvariant() switch
                    {
                        "top" => VerticalAlignment.TOP,
                        "bottom" => VerticalAlignment.BOTTOM,
                        _ => VerticalAlignment.MIDDLE
                    });

                var borderStyle = (col.BorderStyle ?? "none").ToLowerInvariant();
                if (borderStyle == "dashed")
                {
                    cell.SetBorder(new DashedBorder(ColorConstants.BLACK, 1));
                }
                else if (borderStyle == "filled")
                {
                    cell.SetBorder(new SolidBorder(ColorConstants.BLACK, 1));
                    cell.SetBackgroundColor(new DeviceRgb(240, 240, 240));
                }
                else
                {
                    cell.SetBorder(Border.NO_BORDER);
                }

                table.AddCell(cell);
            }
        }

        // using var canvas = new Canvas(new PdfCanvas(page), rect);

        var pdfCanvas = new PdfCanvas(page);
        pdfCanvas.SaveState();

        try
        {
            var canvas = new Canvas(pdfCanvas, rect);
            canvas.Add(table);
            canvas.Close();
        }
        finally
        {
            pdfCanvas.RestoreState();
        }
    }

    private static Image CreateQrCode(PdfDocument pdfDoc, string value)
    {
        var barcode = new BarcodeQRCode(value);
        var form = barcode.CreateFormXObject(pdfDoc);
        return new Image(form);
    }

    private static Image CreateDataMatrix(PdfDocument pdfDoc, string value)
    {
        var barcode = new BarcodeDataMatrix(value);
        var form = barcode.CreateFormXObject(pdfDoc);
        return new Image(form);
    }

    private static Image CreatePdf417(PdfDocument pdfDoc, string value)
    {
        var barcode = new BarcodePDF417();
        barcode.SetCode(value);
        var form = barcode.CreateFormXObject(pdfDoc);
        return new Image(form);
    }

    private static Image CreateEan(PdfDocument pdfDoc, string value, int type)
    {
        var barcode = new BarcodeEAN(pdfDoc);
        barcode.SetCodeType(type);
        barcode.SetCode(value);
        var form = barcode.CreateFormXObject(pdfDoc);
        return new Image(form);
    }

    private static Image CreateCode128(PdfDocument pdfDoc, string value)
    {
        var barcode = new Barcode128(pdfDoc);
        barcode.SetCode(value);
        var form = barcode.CreateFormXObject(pdfDoc);
        return new Image(form);
    }

    private static Image CreateCode39(PdfDocument pdfDoc, string value)
    {
        var barcode = new Barcode39(pdfDoc);
        barcode.SetCode(value);
        var form = barcode.CreateFormXObject(pdfDoc);
        return new Image(form);
    }

    private static Image CreateInterleaved25(PdfDocument pdfDoc, string value)
    {
        var barcode = new BarcodeInter25(pdfDoc);
        barcode.SetCode(value);
        var form = barcode.CreateFormXObject(pdfDoc);
        return new Image(form);
    }

    private static void ValidateFields(IEnumerable<PdfFieldDefinition> fields)
    {
        var regex = new Regex("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text", "image", "barcode", "signature", "table" };
        var allowedWeights = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tiny", "normal", "bold" };
        var allowedHorizontal = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "left", "center", "right" };
        var allowedVertical = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "top", "center", "bottom" };
        var allowedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Helvetica", "Times-Roman", "Courier", "Symbol", "ZapfDingbats"
        };

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.FieldName) || !regex.IsMatch(field.FieldName))
            {
                throw new ArgumentException("FieldName must contain only letters, numbers, underscore or dash.");
            }

            if (!allowedTypes.Contains(field.Type ?? ""))
            {
                throw new ArgumentException("Unsupported field type. Allowed: text, image, barcode.");
            }

            if (!allowedHorizontal.Contains(field.HorizontalAlign ?? "left"))
            {
                throw new ArgumentException("HorizontalAlign must be one of: left, center, right.");
            }
            if (!allowedVertical.Contains(field.VerticalAlign ?? "center"))
            {
                throw new ArgumentException("VerticalAlign must be one of: top, center, bottom.");
            }

            if (field.FontSize < 5 || field.FontSize > 128)
            {
                throw new ArgumentException("FontSize must be between 5 and 128.");
            }

            if (!allowedWeights.Contains(field.FontWeight ?? "normal"))
            {
                throw new ArgumentException("FontWeight must be one of: tiny, normal, bold.");
            }

            if (!string.IsNullOrWhiteSpace(field.FontName) && !allowedFonts.Contains(field.FontName))
            {
                throw new ArgumentException("FontName is not allowed.");
            }

            if (string.Equals(field.Type, "table", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Columns.HasValue && field.Columns.Value <= 0)
                {
                    throw new ArgumentException("Table columns must be greater than zero.");
                }
                if (field.TableColumns != null && field.TableColumns.Count > 0)
                {
                    if (field.Columns.HasValue && field.TableColumns.Count != field.Columns.Value)
                    {
                        throw new ArgumentException("TableColumns length must match the number of columns.");
                    }

                    foreach (var col in field.TableColumns)
                    {
                        if (string.IsNullOrWhiteSpace(col.Name))
                        {
                            throw new ArgumentException("Table column name is required.");
                        }
                        if (col.WidthPercent.HasValue && (col.WidthPercent.Value <= 0 || col.WidthPercent.Value > 100))
                        {
                            throw new ArgumentException("Table column width percent must be between 0 and 100.");
                        }
                        if (col.FontSize < 5 || col.FontSize > 128)
                        {
                            throw new ArgumentException("Table column font size must be between 5 and 128.");
                        }
                        if (!allowedWeights.Contains(col.FontWeight ?? "normal"))
                        {
                            throw new ArgumentException("Table column font weight must be one of: tiny, normal, bold.");
                        }
                        if (!allowedHorizontal.Contains(col.HorizontalAlign ?? "left"))
                        {
                            throw new ArgumentException("Table column horizontal align must be one of: left, center, right.");
                        }
                        if (!allowedVertical.Contains(col.VerticalAlign ?? "center"))
                        {
                            throw new ArgumentException("Table column vertical align must be one of: top, center, bottom.");
                        }
                        var border = (col.BorderStyle ?? "none").ToLowerInvariant();
                        if (border is not ("none" or "dashed" or "filled"))
                        {
                            throw new ArgumentException("Table column border must be one of: none, dashed, filled.");
                        }
                    }
                }
                else
                {
                    throw new ArgumentException("Table fields must define at least one column.");
                }
            }
        }
    }

    private static string ResolveFont(string? fontName, string? fontWeight)
    {
        var weight = (fontWeight ?? "normal").ToLowerInvariant();
        var isBold = weight.Contains("bold");
        if (weight == "tiny")
        {
            isBold = false;
        }

        if (string.Equals(fontName, "courier", StringComparison.OrdinalIgnoreCase))
        {
            return isBold ? StandardFonts.COURIER_BOLD : StandardFonts.COURIER;
        }
        if (string.Equals(fontName, "times-roman", StringComparison.OrdinalIgnoreCase) || string.Equals(fontName, "times", StringComparison.OrdinalIgnoreCase))
        {
            return isBold ? StandardFonts.TIMES_BOLD : StandardFonts.TIMES_ROMAN;
        }
        if (string.Equals(fontName, "symbol", StringComparison.OrdinalIgnoreCase))
        {
            return StandardFonts.SYMBOL;
        }
        if (string.Equals(fontName, "zapfdingbats", StringComparison.OrdinalIgnoreCase))
        {
            return StandardFonts.ZAPFDINGBATS;
        }

        // Default Helvetica
        return isBold ? StandardFonts.HELVETICA_BOLD : StandardFonts.HELVETICA;
    }

    private static Rectangle NormalizeRectForRotation(PdfPage page, Rectangle rect)
    {
        var rotation = ((page.GetRotation() % 360) + 360) % 360;
        var pageSize = page.GetPageSize();
        var pageWidth = pageSize.GetWidth();
        var pageHeight = pageSize.GetHeight();

        return rotation switch
        {
            90 => new Rectangle(
                (float)rect.GetY(),
                (float)(pageWidth - rect.GetX() - rect.GetWidth()),
                (float)rect.GetHeight(),
                (float)rect.GetWidth()),
            270 => new Rectangle(
                (float)(pageHeight - rect.GetY() - rect.GetHeight()),
                (float)rect.GetX(),
                (float)rect.GetHeight(),
                (float)rect.GetWidth()),
            180 => new Rectangle(
                (float)(pageWidth - rect.GetX() - rect.GetWidth()),
                (float)(pageHeight - rect.GetY() - rect.GetHeight()),
                (float)rect.GetWidth(),
                (float)rect.GetHeight()),
            _ => rect
        };
    }
}
