using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopOrganizer.Services;

/// <summary>Извлечение и кэширование иконок оболочки Windows для файлов, папок и ярлыков.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const int SHIL_EXTRALARGE = 2; // системный список иконок 48px
    private const int ILD_TRANSPARENT = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Минимальная часть vtable IImageList — до метода GetIcon включительно.
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        int ReplaceIcon(int i, IntPtr hicon, out int pi);
        int SetOverlayImage(int iImage, int iOverlay);
        int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        int AddMasked(IntPtr hbmImage, uint crMask, out int pi);
        int Draw(IntPtr pimldp);
        int Remove(int i);
        int GetIcon(int i, int flags, out IntPtr picon);
    }

    /// <summary>Иконка элемента. size > 32 — четкая 48px из системного image list (без размытия).</summary>
    public static ImageSource? GetIcon(string path, bool isDirectory, int size = 32)
    {
        try
        {
            var large = size > 32;
            var ext = Path.GetExtension(path);
            // У exe/lnk/ico/url иконка индивидуальная — кэшируем по полному пути, иначе по расширению.
            var perPath = isDirectory || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
            var key = perPath ? path : (string.IsNullOrEmpty(ext) ? "<noext>" : ext);
            if (isDirectory && !HasCustomFolderIcon(path)) key = "<dir>";
            key = (large ? "48|" : "32|") + key;

            if (_cache.TryGetValue(key, out var cached)) return cached;

            var shfi = new SHFILEINFO();
            uint flags = large ? SHGFI_SYSICONINDEX : SHGFI_ICON | SHGFI_LARGEICON;
            uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            var exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
            if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;

            var res = SHGetFileInfo(path, attrs, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (res == IntPtr.Zero) return null;

            var hIcon = shfi.hIcon;
            if (large)
            {
                var iid = typeof(IImageList).GUID;
                if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) != 0 || list == null) return null;
                list.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out hIcon);
            }
            if (hIcon == IntPtr.Zero) return null;

            try
            {
                var img = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                _cache[key] = img;
                return img;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("IconCache", ex);
            return null;
        }
    }

    private static bool HasCustomFolderIcon(string path)
    {
        try { return File.Exists(Path.Combine(path, "desktop.ini")); }
        catch { return false; }
    }
}
