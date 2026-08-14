namespace NexusPdf.Pdf.Abstractions;

public class PdfEngineException : Exception
{
    public PdfEngineException(string message) : base(message) { }
    public PdfEngineException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Файл зашифрован: нужен пароль (или введённый пароль не подошёл).</summary>
public sealed class PdfPasswordRequiredException : PdfEngineException
{
    public PdfPasswordRequiredException() : base("Документ защищён паролем.") { }
}

/// <summary>Файл не является корректным PDF или повреждён сильнее, чем движок может восстановить.</summary>
public sealed class PdfCorruptedException : PdfEngineException
{
    public PdfCorruptedException(string message) : base(message) { }
}

/// <summary>Функция запрошена, но поддерживающий её движок недоступен в этой установке.</summary>
public sealed class PdfFeatureUnavailableException : PdfEngineException
{
    public PdfFeatureUnavailableException(string feature, string reason)
        : base($"Функция «{feature}» недоступна: {reason}") { }
}
