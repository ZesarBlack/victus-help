using GHelper.Helpers;
using System.Drawing.Drawing2D;

namespace GHelper.UI
{
    // Lightweight sparkline of the TempHistory rolling window
    public class TempChart : Control
    {
        public static Color colorCPU = RForm.colorCustom;
        public static Color colorGPU = RForm.colorStandard;

        const float MinRange = 30;
        const float MaxRange = 100;

        public TempChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float scale = DeviceDpi / 96f;
            float axisWidth = 44 * scale;

            RectangleF plot = new RectangleF(axisWidth, 4 * scale, Width - axisWidth - 2 * scale, Height - 8 * scale);
            if (plot.Width <= 0 || plot.Height <= 0) return;

            TempSample[] samples = TempHistory.GetSamples();

            float min = MinRange, max = MaxRange;
            foreach (var s in samples)
            {
                if (s.Cpu is not null) { min = Math.Min(min, (float)s.Cpu); max = Math.Max(max, (float)s.Cpu); }
                if (s.Gpu is not null) { min = Math.Min(min, (float)s.Gpu); max = Math.Max(max, (float)s.Gpu); }
            }
            min = (float)Math.Floor(min / 10) * 10;
            max = (float)Math.Ceiling(max / 10) * 10;

            using var gridPen = new Pen(RForm.chartGrid, scale);
            using var textBrush = new SolidBrush(Color.FromArgb(160, RForm.foreMain));
            using var font = new Font(Font.FontFamily, 7F);

            for (float t = min; t <= max; t += 20)
            {
                float y = plot.Bottom - (t - min) / (max - min) * plot.Height;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);

                string label = TempHelper.FormatTemp(t);
                SizeF size = g.MeasureString(label, font);
                g.DrawString(label, font, textBrush, plot.Left - size.Width - 2 * scale, y - size.Height / 2);
            }

            if (samples.Length < 2) return;

            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            PointF ToPoint(long time, float temp) => new PointF(
                plot.Right - (now - time) / (TempHistory.WindowSeconds * 1000f) * plot.Width,
                plot.Bottom - (temp - min) / (max - min) * plot.Height);

            DrawSeries(g, samples, s => s.Cpu, colorCPU, scale, ToPoint);
            DrawSeries(g, samples, s => s.Gpu, colorGPU, scale, ToPoint);
        }

        static void DrawSeries(Graphics g, TempSample[] samples, Func<TempSample, float?> value, Color color, float scale, Func<long, float, PointF> toPoint)
        {
            var points = new List<PointF>();
            foreach (var s in samples)
            {
                float? temp = value(s);
                if (temp is not null) points.Add(toPoint(s.Time, (float)temp));
            }

            if (points.Count < 2) return;

            using var pen = new Pen(color, 2 * scale) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, points.ToArray());
        }
    }
}
