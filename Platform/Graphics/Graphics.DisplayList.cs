namespace Alexander.Graphics;

// Remote rendering. A view renders into a RecordingGraphics in the process
// that owns it (the Shell); the recorded ops travel to the GUI, which
// replays them onto its real IGraphics. Views don't know or care - they
// draw against IGraphics either way.

public enum DrawOpKind {
	Clear,
	FillRect,
	DrawRect,
	Line,
	FillEllipse,
	DrawEllipse,
	Text,
	PushClip,
	PopClip
}

// One recorded drawing call. Fields by kind:
//   Clear                         Argb
//   FillRect / DrawRect           X, Y, W, H, Argb; FillRect A = radius; DrawRect A = thickness, B = radius
//   FillEllipse / DrawEllipse     X, Y, W, H, Argb; DrawEllipse A = thickness
//   Line                          X, Y -> W, H (end point), A = thickness, Argb
//   Text                          X, Y, Text, Style, Argb
//   PushClip                      X, Y, W, H
public sealed record DrawOp(
	DrawOpKind Kind,
	double X = 0,
	double Y = 0,
	double W = 0,
	double H = 0,
	double A = 0,
	double B = 0,
	uint Argb = 0,
	string? Text = null,
	TextStyle? Style = null);

// Measures text without a UI framework, so a view rendering in the Shell
// can still lay text out. The GUI sends real font metrics on connect.
public interface ITextMeasurer {
	Size Measure(string text, TextStyle style);
}

// Per-character advance widths (at font size 1) for printable ASCII, for
// the UI and monospace fonts in regular and bold. Measured by the GUI
// from its actual fonts; Approximate is the fallback until they arrive.
public sealed class TextMetrics : ITextMeasurer {
	public const char FirstChar = ' ';
	public const char LastChar = '~';
	public const int Count = LastChar - FirstChar + 1;

	public double[] UiRegular { get; set; } = [];
	public double[] UiBold { get; set; } = [];
	public double[] MonoRegular { get; set; } = [];
	public double[] MonoBold { get; set; } = [];

	// Line heights at font size 1.
	public double UiLineHeight { get; set; } = 1.33;
	public double MonoLineHeight { get; set; } = 1.17;

	public static TextMetrics Approximate { get; } = new() {
		UiRegular = Fill(0.52),
		UiBold = Fill(0.56),
		MonoRegular = Fill(0.6),
		MonoBold = Fill(0.6)
	};

	public Size Measure(string text, TextStyle style) {
		var table = style.Monospace
			? style.Bold ? MonoBold : MonoRegular
			: style.Bold ? UiBold : UiRegular;

		// Characters outside the table (accents, symbols, CJK) use the average.
		var average = table.Length > 0 ? table.Average() : 0.55;
		var width = 0.0;

		foreach (var character in text) {
			var index = character - FirstChar;
			width += index >= 0 && index < table.Length ? table[index] : average;
		}

		var lineHeight = style.Monospace ? MonoLineHeight : UiLineHeight;
		return new Size(width * style.FontSize, lineHeight * style.FontSize);
	}

	private static double[] Fill(double width) {
		return Enumerable.Repeat(width, Count).ToArray();
	}
}

// An IGraphics that records instead of drawing.
public sealed class RecordingGraphics(Size size, ViewTheme theme, ITextMeasurer measurer) : IGraphics {
	private readonly List<DrawOp> ops = [];

	public IReadOnlyList<DrawOp> Ops => ops;

	public Size Size => size;

	public ViewTheme Theme => theme;

	public void Clear(Color color) {
		ops.Add(new DrawOp(DrawOpKind.Clear, Argb: DisplayList.ToArgb(color)));
	}

	public void FillRect(Rect rect, Color color, double cornerRadius = 0) {
		ops.Add(new DrawOp(DrawOpKind.FillRect, rect.X, rect.Y, rect.Width, rect.Height, A: cornerRadius, Argb: DisplayList.ToArgb(color)));
	}

	public void DrawRect(Rect rect, Color color, double thickness = 1, double cornerRadius = 0) {
		ops.Add(new DrawOp(DrawOpKind.DrawRect, rect.X, rect.Y, rect.Width, rect.Height, A: thickness, B: cornerRadius, Argb: DisplayList.ToArgb(color)));
	}

	public void DrawLine(Point from, Point to, Color color, double thickness = 1) {
		ops.Add(new DrawOp(DrawOpKind.Line, from.X, from.Y, to.X, to.Y, A: thickness, Argb: DisplayList.ToArgb(color)));
	}

	public void FillEllipse(Rect bounds, Color color) {
		ops.Add(new DrawOp(DrawOpKind.FillEllipse, bounds.X, bounds.Y, bounds.Width, bounds.Height, Argb: DisplayList.ToArgb(color)));
	}

	public void DrawEllipse(Rect bounds, Color color, double thickness = 1) {
		ops.Add(new DrawOp(DrawOpKind.DrawEllipse, bounds.X, bounds.Y, bounds.Width, bounds.Height, A: thickness, Argb: DisplayList.ToArgb(color)));
	}

	public void DrawText(string text, Point origin, Color color, TextStyle? style = null) {
		ops.Add(new DrawOp(DrawOpKind.Text, origin.X, origin.Y, Argb: DisplayList.ToArgb(color), Text: text, Style: style));
	}

	public Size MeasureText(string text, TextStyle? style = null) {
		return measurer.Measure(text, style ?? TextStyle.Body);
	}

	public void PushClip(Rect rect) {
		ops.Add(new DrawOp(DrawOpKind.PushClip, rect.X, rect.Y, rect.Width, rect.Height));
	}

	public void PopClip() {
		ops.Add(new DrawOp(DrawOpKind.PopClip));
	}
}

public static class DisplayList {
	// Plays recorded ops onto a real surface.
	public static void Replay(IEnumerable<DrawOp> ops, IGraphics target) {
		foreach (var op in ops) {
			var color = FromArgb(op.Argb);
			var rect = new Rect(op.X, op.Y, op.W, op.H);

			switch (op.Kind) {
				case DrawOpKind.Clear:
					target.Clear(color);
					break;
				case DrawOpKind.FillRect:
					target.FillRect(rect, color, op.A);
					break;
				case DrawOpKind.DrawRect:
					target.DrawRect(rect, color, op.A, op.B);
					break;
				case DrawOpKind.Line:
					target.DrawLine(new Point(op.X, op.Y), new Point(op.W, op.H), color, op.A);
					break;
				case DrawOpKind.FillEllipse:
					target.FillEllipse(rect, color);
					break;
				case DrawOpKind.DrawEllipse:
					target.DrawEllipse(rect, color, op.A);
					break;
				case DrawOpKind.Text:
					target.DrawText(op.Text ?? "", new Point(op.X, op.Y), color, op.Style);
					break;
				case DrawOpKind.PushClip:
					target.PushClip(rect);
					break;
				case DrawOpKind.PopClip:
					target.PopClip();
					break;
			}
		}
	}

	public static uint ToArgb(Color color) {
		return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
	}

	public static Color FromArgb(uint argb) {
		return new Color((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
	}
}
