using Microsoft.JSInterop;

namespace RichContent.Sample;

/// <summary>
/// A .NET method that fails when script calls it, for the browser tests: the browser only sees a rejected promise,
/// which a script may well handle quietly.
/// </summary>
public static class DeliberateFaults
{
    [JSInvokable("DeliberateFault")]
    public static void Throw() => throw new InvalidOperationException("Deliberate fault: invoked from script.");
}
