namespace DesktopOrganizer.Models;

/// <summary>Визуальная коробка на рабочем столе (таблица Boxes, раздел 8.1 ТЗ).</summary>
public class Box
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 240;
    public string BackgroundColor { get; set; } = "#CC1E242C";
    public double Opacity { get; set; } = 1.0;
    public bool IsLocked { get; set; }
    public bool IsCollapsed { get; set; }
    public bool IsHidden { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Размер значков элементов в DIP: 24 (мелкие), 32 (обычные), 48 (крупные).</summary>
    public int IconSize { get; set; } = 32;
}

/// <summary>Элемент внутри коробки — ссылка на файл/папку/ярлык (таблица Items, раздел 8.2 ТЗ).</summary>
public class BoxItem
{
    public long Id { get; set; }
    public long BoxId { get; set; }
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string ItemType { get; set; } = "File"; // File | Folder | Shortcut | Url
    public string Extension { get; set; } = "";
    public int DisplayOrder { get; set; }
    public bool IsMissing { get; set; }

    /// <summary>Ярлык скрыт с рабочего стола приложением (возвращается при выходе/удалении из коробки).</summary>
    public bool HiddenByApp { get; set; }

    /// <summary>Какие биты атрибутов (Hidden/System) реально добавило приложение — их и снимать при возврате.</summary>
    public System.IO.FileAttributes AddedAttributes { get; set; }

    // Не хранится в БД — иконка для отображения.
    public System.Windows.Media.ImageSource? Icon { get; set; }
}

/// <summary>Строка результата быстрого поиска (раздел 5.7 ТЗ).</summary>
public class SearchResult
{
    public long ItemId { get; set; }
    public long BoxId { get; set; }
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string BoxName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public System.Windows.Media.ImageSource? Icon { get; set; }
}
