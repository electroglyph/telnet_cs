namespace telnet_cs.Tests
{
    using Xunit;

    /// <summary>
    /// Serial collection for tests that mutate process-global resources no
    /// async flow can isolate (e.g. <c>Console.SetOut</c>). Everything else
    /// runs in parallel per class.
    /// </summary>
    [CollectionDefinition("Serial", DisableParallelization = true)]
    public sealed class SerialCollection
    {
    }
}
