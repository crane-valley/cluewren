using Xunit.Abstractions;
using Xunit.Sdk;

namespace Cluewren.Tests;

// xUnit v2 has no dynamic skip. A measurement that cannot decide throws InconclusiveException, and a test marked
// [InconclusiveFact] then reports as skipped with that reason instead of passing.
public sealed class InconclusiveException(string reason) : Exception(reason);

[XunitTestCaseDiscoverer("Cluewren.Tests.InconclusiveFactDiscoverer", "Cluewren.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class InconclusiveFactAttribute : FactAttribute;

public sealed class InconclusiveFactDiscoverer(IMessageSink sink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions options, ITestMethod method, IAttributeInfo attribute) =>
        [new InconclusiveTestCase(sink, options.MethodDisplayOrDefault(), options.MethodDisplayOptionsOrDefault(), method)];
}

public sealed class InconclusiveTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer", true)]
    public InconclusiveTestCase()
    {
    }

    public InconclusiveTestCase(IMessageSink sink, TestMethodDisplay display, TestMethodDisplayOptions options, ITestMethod method)
        : base(sink, display, options, method)
    {
    }

    public override async Task<RunSummary> RunAsync(IMessageSink sink, IMessageBus bus, object[] arguments,
        ExceptionAggregator aggregator, CancellationTokenSource cancel)
    {
        var skipping = new SkippingBus(bus);
        var summary = await base.RunAsync(sink, skipping, arguments, aggregator, cancel);
        summary.Failed -= skipping.Skipped;
        summary.Skipped += skipping.Skipped;
        return summary;
    }

    sealed class SkippingBus(IMessageBus inner) : IMessageBus
    {
        public int Skipped;

        public bool QueueMessage(IMessageSinkMessage message)
        {
            if (message is ITestFailed failed && failed.ExceptionTypes.FirstOrDefault() == typeof(InconclusiveException).FullName)
            {
                Skipped++;
                return inner.QueueMessage(new TestSkipped(failed.Test, "inconclusive: " + failed.Messages.FirstOrDefault()));
            }
            return inner.QueueMessage(message);
        }

        public void Dispose() => inner.Dispose();
    }
}
