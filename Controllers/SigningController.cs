using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace DotNetSigningServer.Controllers
{
    [ApiController]
    public class SigningController : ControllerBase
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly PdfSigningService _signingService;

        public SigningController(ApplicationDbContext dbContext, PdfSigningService signingService)
        {
            _dbContext = dbContext;
            _signingService = signingService;
        }

        [HttpPost("/presign")]
        public async Task<IActionResult> PreSign([FromBody] PreSignInput input)
        {
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

        [HttpPost("/sign")]
        public async Task<IActionResult> Sign([FromBody] SignInput input)
        {
            var signingData = await _dbContext.SigningData.FindAsync(input.Id);
            if (signingData == null)
            {
                return NotFound(new { message = "Signing data not found for the provided ID." });
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
                await _dbContext.SaveChangesAsync();

                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred during the final signing process: {ex.Message}");
            }
        }

        [HttpPost("/sign-pfx")]
        public IActionResult SignWithPfx([FromBody] PfxSignInput input)
        {
            try
            {
                var result = _signingService.SignWithPfx(input);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred during the PFX signing process: {ex.Message}");
            }
        }

        [HttpPost("/timestamp")]
        public IActionResult ApplyTimestamp([FromBody] DocumentTimestampInput input)
        {
            try
            {
                var result = _signingService.ApplyDocumentTimestamp(input);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred while applying the timestamp: {ex.Message}");
            }
        }
    }
}
