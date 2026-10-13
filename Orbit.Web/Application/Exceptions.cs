using Orbit.Application.Models;

namespace Orbit.Application;

public abstract class OrbitException(string message) : Exception(message);

public sealed class NotFoundException(string message) : OrbitException(message);

public sealed class ForbiddenException(string message) : OrbitException(message);

public sealed class ValidationException(string message) : OrbitException(message);

/// <summary>
/// Some of a request's answers failed their checks (§6.20): a message per question, keyed by question id, so the flow can show
/// each one beside its question. Anywhere else it is an ordinary flash message.
/// </summary>
public sealed class RequestAnswersException(IReadOnlyDictionary<Guid, string> errors)
    : OrbitException(errors.Count == 1 ? "One answer needs attention." : $"{errors.Count} answers need attention.")
{
    public IReadOnlyDictionary<Guid, string> Errors { get; } = errors;
}

/// <summary>
/// The directory import refused an Import or an Update managers after reading the directory (§6.13.1), before changing
/// anything. It carries the list it read, so the page shows it again without reading the directory a second time.
/// Anywhere else it is an ordinary flash message.
/// </summary>
public sealed class DirectoryImportRefusedException(string message, DirectoryImportPreview preview) : OrbitException(message)
{
    public DirectoryImportPreview Preview { get; } = preview;
}
