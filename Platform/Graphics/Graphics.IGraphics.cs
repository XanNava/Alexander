namespace Alexander.Graphics;

// The drawing surface a ServiceView renders onto. Coordinates are
// device-independent pixels with (0,0) at the view's top-left. The GUI
// implements this over WPF; another host could implement it over
// anything else without the views changing.
public interface IGraphics {
	Size Size { get; }
	ViewTheme Theme { get; }

	void Clear(Color color);

	void FillRect(Rect rect, Color color, double cornerRadius = 0);
	void DrawRect(Rect rect, Color color, double thickness = 1, double cornerRadius = 0);
	void DrawLine(Point from, Point to, Color color, double thickness = 1);
	void FillEllipse(Rect bounds, Color color);
	void DrawEllipse(Rect bounds, Color color, double thickness = 1);

	// origin is the top-left of the text's layout box.
	void DrawText(string text, Point origin, Color color, TextStyle? style = null);
	Size MeasureText(string text, TextStyle? style = null);

	// Clips must be balanced; the host pops any left over after Render.
	void PushClip(Rect rect);
	void PopClip();
}
