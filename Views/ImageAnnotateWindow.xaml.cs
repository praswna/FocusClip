using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusClip.Views;

/// <summary>이미지 주석 편집기(C5). 이미지 위 InkCanvas 펜/지우개 + 휠 줌(커서중심)/중클릭 팬.
/// 저장 시 원본과 잉크를 합성한 PNG 비트맵을 Result로 돌려준다. (CM DrawingDialog/DrawingCanvas 대응)</summary>
public partial class ImageAnnotateWindow : Window
{
    private BitmapSource _src; // 자르기로 교체될 수 있음

    /// <summary>저장 시 합성된 결과 비트맵(취소면 null).</summary>
    public BitmapSource? Result { get; private set; }

    // 스냅샷 기반 undo/redo
    private readonly Stack<StrokeCollection> _undo = new();
    private readonly Stack<StrokeCollection> _redo = new();
    private StrokeCollection _last = new();
    private bool _suppress;

    // 중클릭 팬
    private bool _panning;
    private Point _panStart;
    private double _panH, _panV;

    // 자르기
    private bool _cropMode;
    private bool _cropDragging;
    private Point _cropStart;

    public ImageAnnotateWindow(BitmapSource src)
    {
        InitializeComponent();
        _src = src;

        // InkCanvas/Image 를 원본 픽셀 크기로 1:1 배치 → 스트로크 좌표 = 이미지 픽셀 좌표.
        BaseImage.Source = src;
        BaseImage.Width = src.PixelWidth;
        BaseImage.Height = src.PixelHeight;
        Ink.Width = src.PixelWidth;
        Ink.Height = src.PixelHeight;

        Ink.EditingMode = InkCanvasEditingMode.Ink;
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Red,
            Width = 4,
            Height = 4,
            FitToCurve = true,
        };

        Ink.Strokes.StrokesChanged += (_, _) =>
        {
            if (_suppress) return;
            _undo.Push(_last);
            _redo.Clear();
            _last = Clone(Ink.Strokes);
            UpdateHistoryButtons();
        };
        UpdateHistoryButtons();
    }

    private static StrokeCollection Clone(StrokeCollection src)
        => new(src.Select(s => s.Clone()));

    // ── 도구 ──
    private void Pen_Click(object sender, RoutedEventArgs e) { ExitCropMode(); Ink.EditingMode = InkCanvasEditingMode.Ink; }
    private void Erase_Click(object sender, RoutedEventArgs e) { ExitCropMode(); Ink.EditingMode = InkCanvasEditingMode.EraseByStroke; }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string hex)
        {
            ExitCropMode();
            var c = (Color)ColorConverter.ConvertFromString(hex);
            Ink.DefaultDrawingAttributes.Color = c;
            Ink.EditingMode = InkCanvasEditingMode.Ink;
        }
    }

    // ── 자르기 ──
    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_cropMode) { ExitCropMode(); return; }
        _cropMode = true;
        Ink.EditingMode = InkCanvasEditingMode.None; // 자르기 중엔 펜 입력 끔
        Ink.UseCustomCursor = true;
        Ink.Cursor = Cursors.Cross;
        CropRect.Visibility = Visibility.Collapsed;
        CropApplyBtn.IsEnabled = false;
        CropBtn.Content = "✂ 취소";
        CropHint.Text = "드래그로 영역 선택 후 [적용]";
    }

    private void ExitCropMode()
    {
        if (!_cropMode && CropRect.Visibility == Visibility.Collapsed) return;
        _cropMode = false;
        _cropDragging = false;
        Ink.UseCustomCursor = false;
        Ink.EditingMode = InkCanvasEditingMode.Ink;
        CropRect.Visibility = Visibility.Collapsed;
        CropApplyBtn.IsEnabled = false;
        CropBtn.Content = "✂ 자르기";
        CropHint.Text = "";
    }

    private void CropApply_Click(object sender, RoutedEventArgs e)
    {
        if (CropRect.Visibility != Visibility.Visible || CropRect.Width < 1 || CropRect.Height < 1) return;
        int x = (int)Math.Round(CropRect.Margin.Left);
        int y = (int)Math.Round(CropRect.Margin.Top);
        int w = (int)Math.Round(CropRect.Width);
        int h = (int)Math.Round(CropRect.Height);
        x = Math.Max(0, Math.Min(x, _src.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, _src.PixelHeight - 1));
        w = Math.Max(1, Math.Min(w, _src.PixelWidth - x));
        h = Math.Max(1, Math.Min(h, _src.PixelHeight - y));
        try
        {
            var cropped = new CroppedBitmap(_src, new Int32Rect(x, y, w, h));
            cropped.Freeze();
            _src = cropped;
            BaseImage.Source = _src;
            BaseImage.Width = w; BaseImage.Height = h;
            Ink.Width = w; Ink.Height = h;
            var m = Matrix.Identity; m.Translate(-x, -y);
            foreach (var s in Ink.Strokes) s.Transform(m, false); // 주석을 자른 좌표계로 이동
            _undo.Clear(); _redo.Clear(); _last = Clone(Ink.Strokes); // 자르기는 구조 변경 → 되돌리기 초기화
            UpdateHistoryButtons();
        }
        catch { }
        ExitCropMode();
    }

    private Point ClampToImage(Point p)
        => new Point(Math.Max(0, Math.Min(p.X, _src.PixelWidth)), Math.Max(0, Math.Min(p.Y, _src.PixelHeight)));

    private void SetCropRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        CropRect.Margin = new Thickness(x, y, 0, 0);
        CropRect.Width = Math.Abs(a.X - b.X);
        CropRect.Height = Math.Abs(a.Y - b.Y);
    }

    private void Thick_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Ink?.DefaultDrawingAttributes == null) return;
        Ink.DefaultDrawingAttributes.Width = e.NewValue;
        Ink.DefaultDrawingAttributes.Height = e.NewValue;
    }

    // ── Undo / Redo / Clear ──
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(Clone(Ink.Strokes));
        var prev = _undo.Pop();
        ApplyStrokes(prev);
        UpdateHistoryButtons();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(Clone(Ink.Strokes));
        var next = _redo.Pop();
        ApplyStrokes(next);
        UpdateHistoryButtons();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Ink.Strokes.Clear();

    private void ApplyStrokes(StrokeCollection snapshot)
    {
        _suppress = true;
        Ink.Strokes.Clear();
        foreach (var s in snapshot) Ink.Strokes.Add(s.Clone());
        _suppress = false;
        _last = Clone(Ink.Strokes);
    }

    private void UpdateHistoryButtons()
    {
        if (UndoBtn != null) UndoBtn.IsEnabled = _undo.Count > 0;
        if (RedoBtn != null) RedoBtn.IsEnabled = _redo.Count > 0;
    }

    // ── 확대/축소 ──
    private void SetZoom(double scale)
    {
        scale = Math.Max(0.1, Math.Min(8.0, scale));
        Zoom.ScaleX = scale;
        Zoom.ScaleY = scale;
        if (ZoomLabel != null) ZoomLabel.Text = $"{Math.Round(scale * 100)}%";
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Zoom.ScaleX * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Zoom.ScaleX / 1.2);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    // CM DrawingCanvas: 그냥 휠 = 줌(커서 중심)
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        Point contentPt = e.GetPosition(Surface);    // 이미지 픽셀 좌표(변환 전)
        Point viewportPt = e.GetPosition(Scroller);
        double old = Zoom.ScaleX;
        double next = Math.Max(0.1, Math.Min(8.0, old * (e.Delta > 0 ? 1.1 : 1 / 1.1)));
        if (Math.Abs(next - old) < 1e-6) return;

        SetZoom(next);
        Scroller.UpdateLayout(); // 새 스케일 레이아웃 반영 후 오프셋 보정
        Scroller.ScrollToHorizontalOffset(contentPt.X * next - viewportPt.X);
        Scroller.ScrollToVerticalOffset(contentPt.Y * next - viewportPt.Y);
    }

    // ── 중클릭 드래그 = 팬 ──
    private void Scroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_cropMode && e.LeftButton == MouseButtonState.Pressed)
        {
            _cropDragging = true;
            _cropStart = ClampToImage(e.GetPosition(Surface));
            SetCropRect(_cropStart, _cropStart);
            CropRect.Visibility = Visibility.Visible;
            CropApplyBtn.IsEnabled = false;
            Scroller.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            _panning = true;
            _panStart = e.GetPosition(Scroller);
            _panH = Scroller.HorizontalOffset;
            _panV = Scroller.VerticalOffset;
            Scroller.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Scroller_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_cropDragging)
        {
            SetCropRect(_cropStart, ClampToImage(e.GetPosition(Surface)));
            e.Handled = true;
            return;
        }
        if (!_panning) return;
        Point p = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(_panH - (p.X - _panStart.X));
        Scroller.ScrollToVerticalOffset(_panV - (p.Y - _panStart.Y));
    }

    private void Scroller_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropDragging)
        {
            _cropDragging = false;
            Scroller.ReleaseMouseCapture();
            CropApplyBtn.IsEnabled = CropRect.Width >= 4 && CropRect.Height >= 4;
            e.Handled = true;
            return;
        }
        if (_panning && e.MiddleButton == MouseButtonState.Released)
        {
            _panning = false;
            Scroller.ReleaseMouseCapture();
        }
    }

    // ── 저장: 원본 + 잉크 합성 ──
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int w = _src.PixelWidth, h = _src.PixelHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(_src, new Rect(0, 0, w, h));
                Ink.Strokes.Draw(dc); // 스트로크 좌표 = 이미지 픽셀 좌표
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            Result = rtb;
            DialogResult = true;
        }
        catch
        {
            DialogResult = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
