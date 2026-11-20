using System;
using System.Collections.Generic;

namespace BD3DOrchestrator.Tests.Fakes;

public sealed class FakeMessageLogger : IMessageLogger
{
    public event Action<string>? MessageLogged;

    public List<string> Messages { get; } = [];

    public void Log(string message)
    {
        this.Messages.Add(message);
        this.MessageLogged?.Invoke(message);
    }
}
