namespace GuideAntsApi.Models.Settings;

public sealed class AddModelException : Exception
{
    public AddModelException(
        string code,
        string step,
        string message,
        string? remediation = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Step = step ?? throw new ArgumentNullException(nameof(step));
        Remediation = remediation;
    }

    public string Code { get; }

    public string Step { get; }

    public string? Remediation { get; }

    public AddModelErrorDto ToDto() => new(Code, Step, Message, Remediation);
}
