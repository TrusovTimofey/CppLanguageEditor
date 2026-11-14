namespace CppLanguageEditor.Models;

public sealed class EditorError
{
    public int Line { get; init; }
    public int Column { get; init; }
    public int Length { get; init; } = 1;
    public string Message { get; init; } = string.Empty;
    public string Severity { get; init; } = "Ошибка";
}
