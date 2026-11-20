using System;

namespace BD3DOrchestrator;

public interface IMessageLogger
{
    event Action<string>? MessageLogged;

    void Log(string message);
}

public sealed class MessageLogger : IMessageLogger
{
    public event Action<string>? MessageLogged;
    public void Log(string message) => this.MessageLogged?.Invoke(message);
}
