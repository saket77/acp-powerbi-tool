namespace Acp.Portal.Infrastructure.Data;

/// <summary>
/// Turns off row-level security for the enclosing block.
/// </summary>
/// <remarks>
/// <para>
/// This exists because some work genuinely has no user behind it — the import worker processing
/// a queue message, a fixture building the world a test is about to check. Those need to see
/// every project; a request never does.
/// </para>
/// <para>
/// It is deliberately awkward and deliberately easy to grep for. If this appears anywhere on
/// the path of an HTTP request, that is a bug, and it is the kind of bug that quietly shows one
/// client another client's data. The isolation suite asserts that the request path never uses it.
/// </para>
/// </remarks>
public static class UnfilteredScope
{
    private static readonly AsyncLocal<bool> Active = new();

    public static bool IsActive => Active.Value;

    public static IDisposable Enter()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
