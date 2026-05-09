using System;

namespace DotNetSigningServer.Services
{
    /// <summary>
    /// Raised when communication with the configured Timestamp Authority fails
    /// (HTTP failure, malformed response, rejected query). Distinct from generic
    /// signing/PDF errors so the API can surface a precise message to the user.
    /// </summary>
    public class TsaCommunicationException : Exception
    {
        public string TsaUrl { get; }

        public TsaCommunicationException(string tsaUrl, string message, Exception? inner = null)
            : base(message, inner)
        {
            TsaUrl = tsaUrl;
        }
    }
}
