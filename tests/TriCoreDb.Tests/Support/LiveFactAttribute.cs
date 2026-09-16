namespace TriCoreDb.Tests.Support;

/// <summary>
/// A test that needs a real <c>tricore-server</c>. It is skipped, rather than failed,
/// when there is no binary to start.
/// </summary>
/// <remarks>
/// Somebody who cloned this repository to read it, or who installed the package from
/// NuGet, has no server binary. <c>dotnet test</c> must still be green for them, and a
/// skipped test says why it was skipped where a failing one only says what broke.
/// </remarks>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (TriCoreServer.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>A data-driven test that needs a real server. See <see cref="LiveFactAttribute"/>.</summary>
public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (TriCoreServer.SkipReason is { } reason) Skip = reason;
    }
}
