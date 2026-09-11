namespace telnet_cs.CiTests
{
  using System;

  internal abstract class WriteHandlerBase
  {
    public abstract Func<byte[], bool> Check { get; }

    public abstract void Handle();
  }
}
