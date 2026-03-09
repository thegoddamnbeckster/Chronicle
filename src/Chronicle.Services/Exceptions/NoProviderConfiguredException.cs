namespace Chronicle.Services.Exceptions;

public class NoProviderConfiguredException : InvalidOperationException
{
    public NoProviderConfiguredException(string message) : base(message) { }
}
