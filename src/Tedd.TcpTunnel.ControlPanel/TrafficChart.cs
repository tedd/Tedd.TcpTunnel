using Tedd.TcpTunnel.Management;

namespace Tedd.TcpTunnel.ControlPanel;

public sealed class TrafficChart : VerticalStackLayout
{
    private readonly GraphicsView _graph;
    private readonly ChartDrawing _drawing = new();
    private readonly Label _rate = new() { FontSize = 26, FontAttributes = FontAttributes.Bold };
    private readonly Label _details = new() { FontSize = 11 };
    private IReadOnlyList<TrafficSample> _samples = [];
    private readonly bool _outbound;
    private bool _hovering;

    public TrafficChart(string title, bool outbound)
    {
        _outbound = outbound;
        Spacing = 6;
        Add(new Label { Text = title, FontSize = 11, CharacterSpacing = 1, TextColor = OptionForm.Theme("Muted") });
        Add(_rate);
        _graph = new GraphicsView { Drawable = _drawing, HeightRequest = 110 };
        _graph.StartHoverInteraction += (_, args) => Hover(args);
        _graph.MoveHoverInteraction += (_, args) => Hover(args);
        _graph.EndHoverInteraction += (_, _) => { _hovering = false; _drawing.Hover = -1; UpdateLabels(); _graph.Invalidate(); };
        _graph.StartInteraction += (_, args) => Hover(args);
        Add(_graph);
        _details.TextColor = OptionForm.Theme("Muted");
        Add(_details);
        Add(new Label { Text = "GREEN  Compressed payload     CYAN  Uncompressed payload     LINE  Original data", FontSize = 10, TextColor = OptionForm.Theme("Muted") });
        UpdateLabels();
    }

    public void Update(IReadOnlyList<TrafficSample> samples)
    {
        _samples = samples;
        _drawing.Rates = samples.Select(s => _outbound ? s.Outbound : s.Inbound).ToArray();
        _drawing.Hover = _hovering && samples.Count > 0 ? Math.Clamp(_drawing.Hover, 0, samples.Count - 1) : -1;
        UpdateLabels(_drawing.Hover);
        _graph.Invalidate();
    }

    private void Hover(TouchEventArgs args)
    {
        if (_samples.Count == 0 || args.Touches.Length == 0) return;
        _hovering = true;
        var index = Math.Clamp((int)Math.Round((args.Touches[0].X - 45) / Math.Max(1, _graph.Width - 55) * (_samples.Count - 1)), 0, _samples.Count - 1);
        _drawing.Hover = index;
        UpdateLabels(index);
        _graph.Invalidate();
    }

    private void UpdateLabels(int index = -1)
    {
        if (_samples.Count == 0) { _rate.Text = "— MB/s"; _details.Text = "Waiting for two live samples. Hover or touch the graph to inspect."; return; }
        var sample = _samples[index < 0 ? _samples.Count - 1 : index];
        var rate = _outbound ? sample.Outbound : sample.Inbound;
        _rate.Text = $"{rate.UncompressedMBps:N2} MB/s";
        _details.Text = $"{sample.Time.ToLocalTime():HH:mm:ss}   Original {rate.UncompressedMBps:N2}   Encoded {rate.EncodedMBps:N2}   Compressed {rate.CompressedMBps:N2}   Plain {rate.UncompressedPayloadMBps:N2} MB/s   Ratio {(rate.CompressionRatio is { } ratio ? ratio.ToString("N2") + ":1" : "—")}";
    }

    private sealed class ChartDrawing : IDrawable
    {
        public TrafficRate[] Rates { get; set; } = [];
        public int Hover { get; set; } = -1;
        public void Draw(ICanvas canvas, RectF area)
        {
            var left = 45f; var top = 8f; var width = Math.Max(1, area.Width - 55); var height = area.Height - 27;
            var max = (float)Math.Max(0.1, Rates.Select(r => Math.Max(r.UncompressedMBps, r.EncodedMBps)).DefaultIfEmpty(0).Max() * 1.15);
            canvas.FontSize = 10; canvas.FontColor = OptionForm.Theme("Muted"); canvas.StrokeSize = 1;
            for (var i = 0; i < 4; i++)
            {
                var y = top + height * i / 3;
                canvas.StrokeColor = OptionForm.Theme("Line"); canvas.DrawLine(left, y, left + width, y);
                canvas.DrawString((max * (3 - i) / 3).ToString("0.0"), 0, y - 7, 39, 15, HorizontalAlignment.Right, VerticalAlignment.Center);
            }
            canvas.DrawString(Rates.Length + " samples · MB/s", left, area.Height - 16, width, 15, HorizontalAlignment.Left, VerticalAlignment.Center);
            if (Rates.Length < 2) return;
            float X(int i) => left + width * i / (Rates.Length - 1);
            float Y(double v) => top + height - (float)v / max * height;
            void Fill(Func<TrafficRate, double> bottom, Func<TrafficRate, double> upper, Color color)
            {
                using var path = new PathF();
                path.MoveTo(X(0), Y(bottom(Rates[0])));
                for (var i = 0; i < Rates.Length; i++) path.LineTo(X(i), Y(upper(Rates[i])));
                for (var i = Rates.Length - 1; i >= 0; i--) path.LineTo(X(i), Y(bottom(Rates[i])));
                path.Close(); canvas.FillColor = color; canvas.FillPath(path);
            }
            Fill(_ => 0, r => r.CompressedMBps, Color.FromArgb("#18B563").WithAlpha(0.75f));
            Fill(r => r.CompressedMBps, r => r.EncodedMBps, Color.FromArgb("#37C3DB").WithAlpha(0.75f));
            using var line = new PathF();
            line.MoveTo(X(0), Y(Rates[0].UncompressedMBps));
            for (var i = 1; i < Rates.Length; i++) line.LineTo(X(i), Y(Rates[i].UncompressedMBps));
            canvas.StrokeSize = 2; canvas.StrokeColor = OptionForm.Theme("Ink"); canvas.DrawPath(line);
            if (Hover >= 0 && Hover < Rates.Length)
            {
                canvas.StrokeColor = Color.FromArgb("#F3BF3E"); canvas.StrokeSize = 1;
                canvas.DrawLine(X(Hover), top, X(Hover), top + height);
            }
        }
    }
}
