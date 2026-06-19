// Intentionally empty. A type-keyed enum converter was added here on a false
// hypothesis (cross-enum member-name cache collision on "center"); the wire-
// contract tests proved the built-in JsonStringEnumConverter binds both
// PdfHorizontalAlign and PdfVerticalAlign correctly, so this is not needed.
// Can be deleted from source control.
namespace DotNetSigningServer.Models;
