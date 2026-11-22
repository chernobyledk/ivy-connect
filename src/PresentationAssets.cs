using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class PresentationAssets
{
    internal static Icon ReadIcon()
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QQPrivateApp.LauncherIcon"))
        {
            if (stream == null) return (Icon)SystemIcons.Application.Clone();
            using (var icon = new Icon(stream)) return (Icon)icon.Clone();
        }
    }

    internal static Bitmap ReadCover(string path)
    {
        using (var file = File.OpenRead(path))
            return ReadCover(file);
    }

    internal static Bitmap ReadDefaultCover()
    {
        using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("QQPrivateApp.DefaultCover"))
        {
            if (embedded == null) throw new FileNotFoundException("Embedded default cover missing");
            return ReadCover(embedded);
        }
    }

    private static Bitmap ReadCover(Stream file)
    {
        if (file.Length == 0 || file.Length > 24L * 1024 * 1024) throw new InvalidDataException("图片文件过大或为空");
        using (var source = Image.FromStream(file, true, true))
        {
            if ((long)source.Width * source.Height > 24L * 1024 * 1024) throw new InvalidDataException("图片像素过多");
            return new Bitmap(source);
        }
    }
}

internal sealed class PictureCoverController : IDisposable
{
    private readonly string customFile, defaultFile;
    private readonly Action<string> report;
    private readonly Timer timer = new Timer { Interval = 150 };
    private Bitmap image;
    private Cover cover;
    private Target target;
    private string source;
    internal IntPtr Window { get { return cover == null ? IntPtr.Zero : cover.Handle; } }

    internal PictureCoverController(string profile, Action<string> report, string defaultFile = null)
    {
        customFile = Path.Combine(profile, "qq-cover.user.png");
        this.defaultFile = defaultFile;
        this.report = report;
        timer.Tick += (sender, args) => Update();
        Load(File.Exists(customFile));
    }

    private void Load(bool custom)
    {
        Bitmap next = null;
        string status;
        try
        {
            next = custom ? PresentationAssets.ReadCover(customFile) :
                defaultFile == null ? PresentationAssets.ReadDefaultCover() : PresentationAssets.ReadCover(defaultFile);
            status = custom ? "自选图片" : "默认图片";
        }
        catch (Exception error)
        {
            if (!IsImageError(error)) throw;
            status = (custom ? "自选图片" : "默认图片") + "缺失或无法读取，已使用纯色";
        }
        Swap(next, status);
    }

    private static bool IsImageError(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
            error is OutOfMemoryException || error is System.Runtime.InteropServices.ExternalException;
    }

    private void Swap(Bitmap next, string status)
    {
        var previous = image;
        image = next;
        source = status;
        if (cover != null) cover.UseImage(image);
        if (previous != null) previous.Dispose();
        Describe();
    }

    private void Describe() { report("遮罩" + (cover == null ? "关闭" : "开启") + " · " + source); }

    internal void UseCustom(string path)
    {
        Bitmap next;
        try { next = PresentationAssets.ReadCover(path); }
        catch (Exception error)
        {
            if (!IsImageError(error)) throw;
            Swap(null, "所选图片无法读取，已使用纯色");
            return;
        }
        string pending = customFile + ".next";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(customFile));
            next.Save(pending, ImageFormat.Png);
            if (File.Exists(customFile)) File.Replace(pending, customFile, null);
            else File.Move(pending, customFile);
            Swap(next, "自选图片");
            next = null;
        }
        finally
        {
            if (next != null) next.Dispose();
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    internal void Reset()
    {
        if (File.Exists(customFile)) File.Delete(customFile);
        Load(false);
    }

    internal void Show(Target window)
    {
        Close();
        target = window;
        cover = new Cover(false);
        cover.UseImage(image);
        if (Update()) { timer.Start(); Describe(); }
    }

    private bool Update()
    {
        if (cover == null) return false;
        try { if (cover.Follow(target)) { return true; } }
        catch (InvalidOperationException) { }
        Close();
        report("遮罩已关闭 · QQ 已隐藏、最小化、关闭或置顶。恢复普通展开后可重开遮罩。");
        return false;
    }

    internal void Close()
    {
        timer.Stop();
        if (cover != null) { cover.Dispose(); cover = null; }
        target = null;
        Describe();
    }

    public void Dispose()
    {
        Close();
        timer.Dispose();
        if (image != null) image.Dispose();
    }
}
