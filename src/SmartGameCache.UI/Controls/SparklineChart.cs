using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SmartGameCache.UI.Controls;

public class SparklineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(SparklineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty =
        DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(59, 130, 246)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(40, 59, 130, 246)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineThicknessProperty =
        DependencyProperty.Register(nameof(LineThickness), typeof(double), typeof(SparklineChart),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public double LineThickness
    {
        get => (double)GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        var valList = Values?.ToList() ?? new List<double>();
        if (valList.Count < 2)
        {
            // Draw baseline subtle guide
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1), new Point(0, h - 2), new Point(w, h - 2));
            return;
        }

        double max = valList.Max();
        double min = valList.Min();
        if (Math.Abs(max - min) < 0.0001)
        {
            max += 1.0;
        }

        var points = new List<Point>();
        double stepX = w / (valList.Count - 1);

        for (int i = 0; i < valList.Count; i++)
        {
            double norm = (valList[i] - min) / (max - min);
            double y = h - 4 - (norm * (h - 8));
            points.Add(new Point(i * stepX, y));
        }

        // Draw filled area under curve
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(points[0], true, false);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i], true, false);
            }
            ctx.LineTo(new Point(points.Last().X, h), true, false);
        }
        fillGeo.Freeze();
        dc.DrawGeometry(FillBrush, null, fillGeo);

        // Draw line curve
        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i], true, false);
            }
        }
        lineGeo.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, LineThickness), lineGeo);
    }
}

