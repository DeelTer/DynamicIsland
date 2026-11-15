using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Island;

/// Frozen-screen colour picker with a magnifier loupe. Esc / right click cancels.
sealed class ColorPicker : Form
{
    readonly Bitmap shot;
    readonly Native.Rect screen;
    Point mouse;
    Color? result;

    ColorPicker(Native.Rect screen)
    {
        this.screen = screen;
        shot = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(screen.Left, screen.Top, 0, 0, new Size(screen.Width, screen.Height));
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    public static Color? Pick()
    {
        var monitor = Native.MonitorFromPoint(Native.Cursor, 2);
        using var picker = new ColorPicker(Native.Monitor(monitor).Screen);
        picker.Load += (_, _) => Native.SetWindowPos(picker.Handle, 0, picker.screen.Left, picker.screen.Top, picker.screen.Width, picker.screen.Height, 0x14);
        picker.ShowDialog();
        return picker.result;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        var cursor = Native.Cursor;
        mouse = new Point(cursor.X - screen.Left, cursor.Y - screen.Top);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var old = mouse;
        mouse = new Point(e.X * shot.Width / Math.Max(1, ClientSize.Width), e.Y * shot.Height / Math.Max(1, ClientSize.Height));
        Invalidate(Loupe(old)); Invalidate(Loupe(mouse));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            result = shot.GetPixel(Math.Clamp(mouse.X, 0, shot.Width - 1), Math.Clamp(mouse.Y, 0, shot.Height - 1));
        Close();
    }

    Rectangle Loupe(Point p)
    {
        const int size = 150;
        int x = p.X + 24, y = p.Y + 24;
        if (x + size > ClientSize.Width) x = p.X - 24 - size;
        if (y + size + 40 > ClientSize.Height) y = p.Y - 24 - size - 40;
        return new Rectangle(x - 4, y - 4, size + 8, size + 48);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(shot, ClientRectangle, new Rectangle(0, 0, shot.Width, shot.Height), GraphicsUnit.Pixel);
        var box = Loupe(mouse);
        var lens = new Rectangle(box.X + 4, box.Y + 4, 150, 150);
        const int cells = 15;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var clip = Round(lens, 75))
        {
            g.SetClip(clip);
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(shot, lens, new Rectangle(mouse.X - cells / 2, mouse.Y - cells / 2, cells, cells), GraphicsUnit.Pixel);
            float cell = 150f / cells;
            using var center = new Pen(Color.White, 2);
            g.DrawRectangle(center, lens.X + cell * (cells / 2), lens.Y + cell * (cells / 2), cell, cell);
            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(Color.FromArgb(230, 255, 255, 255), 3);
            g.DrawPath(ring, clip);
        }
        var color = shot.GetPixel(Math.Clamp(mouse.X, 0, shot.Width - 1), Math.Clamp(mouse.Y, 0, shot.Height - 1));
        var label = new Rectangle(lens.X + 20, lens.Bottom + 8, 110, 30);
        using (var pill = Round(label, 15)) using (var fill = new SolidBrush(Color.FromArgb(235, 12, 12, 12))) g.FillPath(fill, pill);
        using (var swatch = new SolidBrush(color)) g.FillEllipse(swatch, label.X + 8, label.Y + 8, 14, 14);
        using var font = new Font("Segoe UI Variable Text", 10f, FontStyle.Bold);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString($"#{color.R:X2}{color.G:X2}{color.B:X2}", font, Brushes.White, label.X + 28, label.Y + 6);
    }

    static GraphicsPath Round(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) shot.Dispose();
        base.Dispose(disposing);
    }
}
