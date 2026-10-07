namespace Alexander.Graphics;

// Small immediate-mode helpers so every service doesn't reinvent buttons
// and text trimming. Lay out in Render, draw, keep the instances around
// to hit-test in OnPointerDown.
public sealed class ViewButton(string label) {
	public string Label { get; set; } = label;
	public Rect Bounds { get; set; }
	public bool IsEnabled { get; set; } = true;
	public bool IsDanger { get; set; }

	// pointer: the current pointer position, for hover feedback.
	public void Draw(IGraphics graphics, Point? pointer) {
		var theme = graphics.Theme;
		var tone = IsDanger ? theme.Danger : theme.Accent;
		var hovered = IsEnabled && pointer is { } position && Bounds.Contains(position);

		graphics.FillRect(Bounds, hovered ? tone.WithAlpha(60) : theme.Surface, 4);
		graphics.DrawRect(Bounds, IsEnabled ? tone : theme.Border, 1, 4);

		var size = graphics.MeasureText(Label, TextStyle.Small);

		graphics.DrawText(
			Label,
			new Point(
				Bounds.X + (Bounds.Width - size.Width) / 2,
				Bounds.Y + (Bounds.Height - size.Height) / 2),
			IsEnabled ? theme.Text : theme.MutedText,
			TextStyle.Small);
	}

	public bool HitTest(Point point) {
		return IsEnabled && Bounds.Contains(point);
	}
}

public static class ViewText {
	// Trims text with "…" so it fits in maxWidth.
	public static string Ellipsize(
		IGraphics graphics,
		string text,
		double maxWidth,
		TextStyle? style = null) {

		if (maxWidth <= 0) {
			return "";
		}

		if (graphics.MeasureText(text, style).Width <= maxWidth) {
			return text;
		}

		int low = 0, high = text.Length;

		while (low < high) {
			var middle = (low + high + 1) / 2;

			if (graphics.MeasureText(text[..middle] + "…", style).Width <= maxWidth) {
				low = middle;
			} else {
				high = middle - 1;
			}
		}

		return low == 0 ? "…" : text[..low] + "…";
	}
}
