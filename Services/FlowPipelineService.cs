using System.Text.Json;
using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotNetSigningServer.Services
{
    public class FlowPipelineService
    {
        private readonly ILogger<FlowPipelineService> _logger;
        private readonly PdfTemplateService _pdfTemplateService;
        private readonly PdfConversionService _pdfConversionService;
        private readonly PdfSigningService _pdfSigningService;
        private readonly ApplicationDbContext _dbContext;
        private readonly ContentLimitGuard _limitGuard;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _root;

        public FlowPipelineService(
            ILogger<FlowPipelineService> logger,
            PdfTemplateService pdfTemplateService,
            PdfConversionService pdfConversionService,
            PdfSigningService pdfSigningService,
            ApplicationDbContext dbContext,
            ContentLimitGuard limitGuard,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _pdfTemplateService = pdfTemplateService;
            _pdfConversionService = pdfConversionService;
            _pdfSigningService = pdfSigningService;
            _dbContext = dbContext;
            _limitGuard = limitGuard;
            _serviceProvider = serviceProvider;
            _root = Path.Combine(AppContext.BaseDirectory, "flow-runs");
            Directory.CreateDirectory(_root);
        }

        public async Task<Guid> StartFlowAsync(Guid userId, FlowPipelineInput input, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var state = new FlowRunState
            {
                Id = id,
                UserId = userId,
                Status = FlowRunStatus.InProgress,
                OriginalInput = input,
                CurrentPdfs = new List<string>(),
                Flow = input.Flow ?? new List<FlowOperation>(),
                PendingSignatures = new List<PendingSignatureState>()
            };
            await PersistAsync(state, cancellationToken);

            _ = Task.Run(() => ProcessFlowAsync(id, cancellationToken), cancellationToken);
            return id;
        }

        public async Task<FlowRunResponse> GetStatusAsync(Guid id, Guid userId, CancellationToken cancellationToken)
        {
            var state = await LoadAsync(id, cancellationToken);
            if (state == null || state.UserId != userId)
            {
                throw new InvalidOperationException("Flow not found");
            }

            return new FlowRunResponse
            {
                Id = state.Id,
                Status = state.Status,
                Error = state.Error,
                Results = state.Status == FlowRunStatus.Done ? state.CurrentPdfs : null,
                PendingSignatures = state.Status == FlowRunStatus.WaitingForSignatures
                    ? state.PendingSignatures.Select(p => new PendingSignature { Id = p.SigningDataId, HashToSign = p.HashToSign }).ToList()
                    : null
            };
        }

        public async Task<FlowRunResponse> CompleteSignaturesAsync(Guid flowId, Guid userId, List<FlowSignedHash> signatures, CancellationToken cancellationToken)
        {
            var state = await LoadAsync(flowId, cancellationToken);
            if (state == null || state.UserId != userId)
            {
                throw new InvalidOperationException("Flow not found");
            }

            if (state.Status != FlowRunStatus.WaitingForSignatures)
            {
                throw new InvalidOperationException("Flow is not waiting for signatures.");
            }

            var signatureMap = signatures.ToDictionary(s => s.Id, s => s.SignedHash);
            var results = new List<string>();
            foreach (var pending in state.PendingSignatures)
            {
                if (!signatureMap.TryGetValue(pending.SigningDataId, out var signedHash))
                {
                    throw new InvalidOperationException($"Missing signed hash for {pending.SigningDataId}");
                }

                var signingData = await _dbContext.SigningData.FindAsync(pending.SigningDataId);
                if (signingData == null)
                {
                    throw new InvalidOperationException($"Signing data not found: {pending.SigningDataId}");
                }

                var signInput = new SignInput
                {
                    Id = pending.SigningDataId,
                    SignedHash = signedHash
                };

                var result = _pdfSigningService.HandleSign(
                    signInput,
                    signingData.PresignedPdfPath,
                    signingData.CertificatePem,
                    signingData.FieldName,
                    signingData.TsaUrl,
                    signingData.TsaUsername,
                    signingData.TsaPassword);

                if (File.Exists(signingData.PresignedPdfPath))
                {
                    File.Delete(signingData.PresignedPdfPath);
                }

                _dbContext.SigningData.Remove(signingData);
                results.Add(result);
            }

            state.Status = FlowRunStatus.Done;
            state.PendingSignatures.Clear();
            state.CurrentPdfs = results;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await PersistAsync(state, cancellationToken);

            return new FlowRunResponse
            {
                Id = state.Id,
                Status = state.Status,
                Results = results
            };
        }

        private async Task ProcessFlowAsync(Guid flowId, CancellationToken cancellationToken)
        {
            FlowRunState? state = await LoadAsync(flowId, cancellationToken);
            if (state == null) return;

            try
            {
                var pdfs = await ResolveSourcePdfsAsync(state, cancellationToken);

                ValidateFlowOrder(state.Flow);

                foreach (var op in state.Flow)
                {
                    var action = (op.Action ?? string.Empty).Trim().ToLowerInvariant();
                    switch (action)
                    {
                        case "pdfa":
                            pdfs = pdfs.Select(p => _pdfConversionService.ConvertToPdfA(new ConvertToPdfAInput
                            {
                                PdfContent = p,
                                Conformance = op.Data.TryGetProperty("conformance", out var c) ? c.GetString() : "PDF/A-2B"
                            })).ToList();
                            break;
                        case "attachment":
                            pdfs = pdfs.Select(p =>
                            {
                                var attach = op.Data.Deserialize<AddAttachmentInput>() ?? new AddAttachmentInput();
                                attach.PdfContent = p;
                                return _pdfSigningService.AddAttachment(attach);
                            }).ToList();
                            break;
                        case "timestamp":
                            pdfs = pdfs.Select(p =>
                            {
                                var ts = op.Data.Deserialize<DocumentTimestampInput>() ?? new DocumentTimestampInput();
                                ts.PdfContent = p;
                                return _pdfSigningService.ApplyDocumentTimestamp(ts);
                            }).ToList();
                            break;
                        case "sign-pfx":
                            pdfs = pdfs.Select(p =>
                            {
                                var sign = op.Data.Deserialize<PfxSignInput>() ?? new PfxSignInput();
                                sign.PdfContent = p;
                                return _pdfSigningService.SignWithPfx(sign);
                            }).ToList();
                            state.Status = FlowRunStatus.Done;
                            state.CurrentPdfs = pdfs;
                            await PersistAsync(state, cancellationToken);
                            return;
                        case "presign":
                            var pending = new List<PendingSignatureState>();
                            foreach (var p in pdfs)
                            {
                                var pre = op.Data.Deserialize<PreSignInput>() ?? new PreSignInput();
                                pre.PdfContent = p;
                                if (pre.TemplateId.HasValue)
                                {
                                    var sigField = await ResolveSignatureFieldAsync(pre.TemplateId.Value, state.UserId, pre.FieldName, cancellationToken);
                                    if (sigField != null)
                                    {
                                        pre.SignRect = sigField.Rect;
                                        pre.SignPageNumber = sigField.Page <= 0 ? 1 : sigField.Page;
                                        pre.FieldName = string.IsNullOrWhiteSpace(sigField.FieldName) ? pre.FieldName : sigField.FieldName;
                                    }
                                }
                                var field = pre.FieldName ?? $"Signature_{Guid.NewGuid():N}";
                                var (presignedPdfPath, hashToSign) = _pdfSigningService.HandlePreSign(pre, field);

                                var signingData = new SigningData
                                {
                                    FieldName = field,
                                    PresignedPdfPath = presignedPdfPath,
                                    HashToSign = hashToSign,
                                    CertificatePem = pre.CertificatePem,
                                    TsaUrl = pre.TsaUrl,
                                    TsaUsername = pre.TsaUsername,
                                    TsaPassword = pre.TsaPassword,
                                    UserId = state.UserId,
                                    Id = Guid.NewGuid().ToString()
                                };
                                _dbContext.SigningData.Add(signingData);
                                pending.Add(new PendingSignatureState
                                {
                                    SigningDataId = signingData.Id,
                                    HashToSign = signingData.HashToSign
                                });
                            }

                            await _dbContext.SaveChangesAsync(cancellationToken);
                            state.Status = FlowRunStatus.WaitingForSignatures;
                            state.PendingSignatures = pending;
                            await PersistAsync(state, cancellationToken);
                            return;
                        default:
                            throw new InvalidOperationException($"Unsupported flow action '{op.Action}'.");
                    }
                }

                state.Status = FlowRunStatus.Done;
                state.CurrentPdfs = pdfs;
                await PersistAsync(state, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Flow {FlowId} failed", flowId);
                state.Status = FlowRunStatus.Error;
                state.Error = ex.Message;
                await PersistAsync(state, cancellationToken);
            }
        }

        private static void ValidateFlowOrder(IEnumerable<FlowOperation> flow)
        {
            var actions = flow.Select(f => (f.Action ?? string.Empty).Trim().ToLowerInvariant()).ToList();
            var finalActions = new[] { "presign", "timestamp", "sign-pfx" };
            for (int i = 0; i < actions.Count; i++)
            {
                if (finalActions.Contains(actions[i]) && i != actions.Count - 1)
                {
                    throw new InvalidOperationException($"{actions[i]} must be the last step in the flow.");
                }
            }
        }

        private async Task<List<string>> ResolveSourcePdfsAsync(FlowRunState state, CancellationToken cancellationToken)
        {
            if (state.OriginalInput.FillPdf != null)
            {
                var fillInput = state.OriginalInput.FillPdf;
                var result = await _pdfTemplateService.FillAsync(fillInput, state.UserId, cancellationToken);
                return result.Files.ToList();
            }

            if (state.OriginalInput.PdfContents != null && state.OriginalInput.PdfContents.Count > 0)
            {
                return state.OriginalInput.PdfContents.ToList();
            }

            throw new InvalidOperationException("No PDF source provided.");
        }

        private async Task<PdfFieldDefinition?> ResolveSignatureFieldAsync(Guid templateId, Guid userId, string? fieldName, CancellationToken cancellationToken)
        {
            var template = await _pdfTemplateService.GetTemplateAsync(templateId, userId, cancellationToken);
            var fields = template.Fields ?? new List<PdfFieldDefinition>();
            return fields.FirstOrDefault(f =>
                string.Equals(f.Type, "signature", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(fieldName) || string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)));
        }

        private async Task PersistAsync(FlowRunState state, CancellationToken cancellationToken)
        {
            var path = Path.Combine(_root, $"{state.Id}.json");
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, cancellationToken);
        }

        private async Task<FlowRunState?> LoadAsync(Guid id, CancellationToken cancellationToken)
        {
            var path = Path.Combine(_root, $"{id}.json");
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<FlowRunState>(json);
        }

        private class FlowRunState
        {
            public Guid Id { get; set; }
            public Guid UserId { get; set; }
            public string Status { get; set; } = FlowRunStatus.InProgress;
            public string? Error { get; set; }
            public FlowPipelineInput OriginalInput { get; set; } = new();
            public List<FlowOperation> Flow { get; set; } = new();
            public List<string> CurrentPdfs { get; set; } = new();
            public List<PendingSignatureState> PendingSignatures { get; set; } = new();
        }

        private class PendingSignatureState
        {
            public string SigningDataId { get; set; } = string.Empty;
            public string HashToSign { get; set; } = string.Empty;
        }

        private static class FlowRunStatus
        {
            public const string InProgress = "inprogress";
            public const string Done = "done";
            public const string Error = "error";
            public const string WaitingForSignatures = "waiting_for_signatures";
        }
    }
}
