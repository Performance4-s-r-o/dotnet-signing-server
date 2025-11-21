using DotNetSigningServer.Models;
using DotNetSigningServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace DotNetSigningServer.Controllers
{
    [ApiController]
    public class AttachmentController : ControllerBase
    {
        private readonly PdfSigningService _signingService;

        public AttachmentController(PdfSigningService signingService)
        {
            _signingService = signingService;
        }

        [HttpPost("/attachment")]
        public IActionResult AddAttachment([FromBody] AddAttachmentInput input)
        {
            try
            {
                var result = _signingService.AddAttachment(input);
                return Ok(new { result });
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex);
                return Problem($"An error occurred while adding the attachment: {ex.Message}");
            }
        }
    }
}
