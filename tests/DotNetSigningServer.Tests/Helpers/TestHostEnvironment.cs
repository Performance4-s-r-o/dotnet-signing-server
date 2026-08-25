using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DotNetSigningServer.Tests.Helpers;

/// <summary>
/// Minimal <see cref="IHostEnvironment"/> for services whose behaviour differs
/// between Development and Production (e.g. the localhost origin bypass).
/// </summary>
public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "DotNetSigningServer.Tests";
    public string ContentRootPath { get; set; } = ".";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
