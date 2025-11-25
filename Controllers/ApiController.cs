using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ImageMagick;
using ZXing;
using ZXing.Common;

namespace DotNetSigningServer.Controllers
{
    [ApiController]
    public class ApiController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly PdfSigningService _signingService;
        private readonly IApiAuthService _apiAuthService;

        public ApiController(ApplicationDbContext dbContext, PdfSigningService signingService, IApiAuthService apiAuthService)
        {
            _dbContext = dbContext;
            _signingService = signingService;
            _apiAuthService = apiAuthService;
        }

        [HttpPost("/api/presign")]
        public async Task<IActionResult> PreSign([FromBody] PreSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                var signingData = new SigningData();
                string fieldName = string.IsNullOrWhiteSpace(input.FieldName)
                    ? $"Signature_{signingData.Id.Replace("-", string.Empty)}"
                    : input.FieldName;

                var (presignedPdfPath, hashToSign) = _signingService.HandlePreSign(input, fieldName);
                signingData.FieldName = fieldName;
                signingData.PresignedPdfPath = presignedPdfPath;
                signingData.HashToSign = hashToSign;
                signingData.CertificatePem = input.CertificatePem;
                signingData.TsaUrl = input.TsaUrl;
                signingData.TsaUsername = input.TsaUsername;
                signingData.TsaPassword = input.TsaPassword;

                signingData.UserId = user.Id;

                _dbContext.SigningData.Add(signingData);
                await _dbContext.SaveChangesAsync();

                return Ok(new { id = signingData.Id, hashToSign = signingData.HashToSign });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred during the presign process: {ex.Message}");
            }
        }

        [HttpPost("/api/sign")]
        public async Task<IActionResult> Sign([FromBody] SignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            var signingData = await _dbContext.SigningData.FindAsync(input.Id);
            if (signingData == null)
            {
                return NotFound(new { message = "Signing data not found for the provided ID." });
            }
            if (signingData.UserId != user.Id)
            {
                return Forbid();
            }

            try
            {
                var result = _signingService.HandleSign(
                    input,
                    signingData.PresignedPdfPath,
                    signingData.CertificatePem,
                    signingData.FieldName,
                    signingData.TsaUrl,
                    signingData.TsaUsername,
                    signingData.TsaPassword);

                System.IO.File.Delete(signingData.PresignedPdfPath);
                _dbContext.SigningData.Remove(signingData);
                await DebitUserAsync(user);

                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred during the final signing process: {ex.Message}");
            }
        }

        [HttpPost("/api/sign-pfx")]
        public async Task<IActionResult> SignWithPfx([FromBody] PfxSignInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                var result = _signingService.SignWithPfx(input);
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred during the PFX signing process: {ex.Message}");
            }
        }

        [HttpPost("/api/timestamp")]
        public async Task<IActionResult> ApplyTimestamp([FromBody] DocumentTimestampInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                var result = _signingService.ApplyDocumentTimestamp(input);
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred while applying the timestamp: {ex.Message}");
            }
        }

        [HttpPost("/api/attachment")]
        public async Task<IActionResult> AddAttachment([FromBody] AddAttachmentInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            try
            {
                var result = _signingService.AddAttachment(input);
                await DebitUserAsync(user);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred while adding the attachment: {ex.Message}");
            }
        }

        [HttpPost("/api/find-codes")]
        public async Task<IActionResult> FindCodes([FromBody] FindCodesInput input)
        {
            var (user, error) = await EnsureUserWithCreditsAsync(originHeader: Request.Headers["Origin"].ToString());
            if (error != null || user == null) return error!;

            if (string.IsNullOrWhiteSpace(input.PdfContent))
            {
                return BadRequest(new { message = "PdfContent is required." });
            }

            var formats = ParseFormats(input.CodeType);
            try
            {
                var results = await DetectCodesAsync(input.PdfContent, formats);
                await DebitUserAsync(user);
                return Ok(new { results });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred while scanning codes: {ex.Message}");
            }
        }

        private IList<BarcodeFormat> ParseFormats(string codeType)
        {
            var formats = new List<BarcodeFormat>();
            var normalized = (codeType ?? "any").Trim().ToLowerInvariant();
            if (normalized == "qr")
            {
                formats.Add(BarcodeFormat.QR_CODE);
            }
            else if (normalized is "datamatrix" or "data-matrix" or "dm")
            {
                formats.Add(BarcodeFormat.DATA_MATRIX);
            }
            else if (normalized == "pdf417")
            {
                formats.Add(BarcodeFormat.PDF_417);
            }
            else if (normalized == "aztec")
            {
                formats.Add(BarcodeFormat.AZTEC);
            }
            else
            {
                formats.AddRange(new[]
                {
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX,
                    BarcodeFormat.PDF_417,
                    BarcodeFormat.AZTEC
                });
            }

            return formats;
        }

        private async Task<List<object>> DetectCodesAsync(string base64Pdf, IList<BarcodeFormat> formats)
        {
            byte[] pdfBytes;
            try
            {
                pdfBytes = Convert.FromBase64String(base64Pdf);
            }
            catch
            {
                throw new InvalidOperationException("PdfContent is not valid base64.");
            }

            var results = new List<object>();

            using var collection = new MagickImageCollection();
            var settings = new MagickReadSettings
            {
                Density = new Density(300, 300),
                Format = MagickFormat.Pdf
            };
            using (var ms = new MemoryStream(pdfBytes))
            {
                await collection.ReadAsync(ms, settings);
            }

            var reader = new BarcodeReaderGeneric
            {
                Options = new DecodingOptions
                {
                    PossibleFormats = formats.ToList(),
                    TryHarder = true,
                    PureBarcode = false
                }
            };

            for (var pageIndex = 0; pageIndex < collection.Count; pageIndex++)
            {
                var page = collection[pageIndex];
                var rgba = page.ToByteArray(MagickFormat.Rgba);
                var luminance = new RGBLuminanceSource(rgba, (int)page.Width, (int)page.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
                var decoded = reader.DecodeMultiple(luminance);
                if (decoded == null) continue;

                foreach (var code in decoded)
                {
                    var point = code.ResultPoints?.FirstOrDefault();
                    results.Add(new
                    {
                        value = code.Text,
                        codeType = code.BarcodeFormat.ToString(),
                        position = new { x = point?.X ?? 0, y = point?.Y ?? 0 },
                        page = pageIndex + 1
                    });
                }
            }

            return results;
        }

        private async Task<User?> GetAuthenticatedUserAsync(string? originHeader = null)
        {
            var user = await _apiAuthService.ValidateTokenAsync(Request.Headers["Authorization"].ToString(), originHeader);
            if (user == null)
            {
                return null;
            }

            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == user.Id);
        }

        private async Task<(User? user, IActionResult? error)> EnsureUserWithCreditsAsync(int requiredCredits = 1, string? originHeader = null)
        {
            var user = await GetAuthenticatedUserAsync(originHeader);
            if (user == null)
            {
                return (null, Unauthorized());
            }

            if (requiredCredits > 0 && user.CreditsRemaining < requiredCredits)
            {
                return (null, StatusCode(StatusCodes.Status402PaymentRequired, new { message = "No credits remaining. Please purchase more to continue." }));
            }

            return (user, null);
        }

        private async Task DebitUserAsync(User user, int debit = 1)
        {
            if (debit <= 0)
            {
                return;
            }

            user.CreditsRemaining = Math.Max(0, user.CreditsRemaining - debit);
            _dbContext.Users.Update(user);
            await _dbContext.SaveChangesAsync();
        }
    }
}
