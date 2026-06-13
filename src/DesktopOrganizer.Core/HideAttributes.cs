using System.IO;

namespace DesktopOrganizer.Services;

/// <summary>
/// Единственный источник правды о том, какие биты атрибутов приложение добавляет, скрывая элемент
/// со стола. Hidden+System («защищённый файл ОС») прячет иконку, пока выключен ShowSuperHidden
/// (по умолчанию выключен); только Hidden оставлял бы её видимой полупрозрачной.
/// Живёт в Core, чтобы и приложение (DesktopIconService.HideMask), и чистая логика восстановления
/// (RestorePlanner) ссылались на одно значение без зависимости теста от WinExe.
/// </summary>
public static class HideAttributes
{
    public const FileAttributes Mask = FileAttributes.Hidden | FileAttributes.System;
}
