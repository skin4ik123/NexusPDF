namespace NexusPdf.Pdf.Abstractions;

/// <summary>Как записывать правки копии документа.</summary>
/// <param name="Incremental">
/// Дописать изменения к исходным байтам файла, не переписывая их. Нужно для
/// подписанных документов: полная перезапись делает подписи недействительными,
/// а дописанное обновление их сохраняет.
/// </param>
/// <param name="RemoveSecurity">Записать копию без шифрования (для перешифровки другим паролем).</param>
public readonly record struct InPlaceSaveOptions(bool Incremental = false, bool RemoveSecurity = false);
