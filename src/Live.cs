using System.Drawing;
using System.Drawing.Drawing2D;

namespace RedTune
{
    // The moving shapes of the sound page's live pictures (the monitor's levels, the dot of the sound, the spectrum) are
    // drawn through ILive. 19.11 it had a second side that drew them on the graphics card (DirectComposition); taken out
    // again on 20.11 (dragging felt late through the go-between, the saving over the cached drawing was ~0.2 % of the
    // processor). The sources and the measurements are kept in RedTune-devtools\gpu-render.
    interface ILive
    {
        void FillRect(Color c, float x, float y, float w, float h);
        void Line(Color c, float width, float x0, float y0, float x1, float y1, bool dash = false);
        void FillPoly(Color c, PointF[] p);
        void Lines(Color c, float width, PointF[] p);
        void Ellipse(Color c, float x, float y, float w, float h);
    }

    sealed class GdiLive : ILive
    {
        readonly Graphics g;
        public GdiLive(Graphics g) { this.g = g; }
        public void FillRect(Color c, float x, float y, float w, float h) { if (w <= 0 || h <= 0) return; using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, h); }
        public void Line(Color c, float width, float x0, float y0, float x1, float y1, bool dash = false) { using (var p = new Pen(c, width)) { if (dash) p.DashStyle = DashStyle.Dash; g.DrawLine(p, x0, y0, x1, y1); } }
        public void FillPoly(Color c, PointF[] pts) { if (pts.Length < 3) return; using (var path = new GraphicsPath()) { path.AddLines(pts); path.CloseFigure(); using (var b = new SolidBrush(c)) g.FillPath(b, path); } }
        public void Lines(Color c, float width, PointF[] pts) { if (pts.Length < 2) return; using (var p = new Pen(c, width) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts); }
        public void Ellipse(Color c, float x, float y, float w, float h) { using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y, w, h); }
    }
}
