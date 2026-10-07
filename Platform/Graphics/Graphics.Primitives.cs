using System.Globalization;

namespace Alexander.Graphics;

public readonly record struct Color(byte R, byte G, byte B, byte A = 255) {
	public static Color Transparent => new(0, 0, 0, 0);

	// "#RRGGBB" or "#RRGGBBAA".
	public static Color FromHex(string hex) {
		var text = hex.TrimStart('#');

		if (text.Length is not (6 or 8)) {
			throw new FormatException($"'{hex}' is not #RRGGBB or #RRGGBBAA.");
		}

		byte Part(int index) {
			return byte.Parse(
				text.AsSpan(index, 2),
				NumberStyles.HexNumber,
				CultureInfo.InvariantCulture);
		}

		return new Color(Part(0), Part(2), Part(4), text.Length == 8 ? Part(6) : (byte)255);
	}

	public Color WithAlpha(byte alpha) {
		return this with { A = alpha };
	}
}

public readonly record struct Point(double X, double Y) {
	public Point Offset(double dx, double dy) {
		return new Point(X + dx, Y + dy);
	}
}

public readonly record struct Size(double Width, double Height);

public readonly record struct Rect(double X, double Y, double Width, double Height) {
	public double Left => X;
	public double Top => Y;
	public double Right => X + Width;
	public double Bottom => Y + Height;
	public Point Center => new(X + Width / 2, Y + Height / 2);

	public static Rect FromSize(Size size) {
		return new Rect(0, 0, size.Width, size.Height);
	}

	public bool Contains(Point point) {
		return point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;
	}

	public Rect Deflate(double amount) {
		return new Rect(
			X + amount,
			Y + amount,
			Math.Max(0, Width - 2 * amount),
			Math.Max(0, Height - 2 * amount));
	}
}

public sealed record TextStyle(double FontSize = 13, bool Bold = false, bool Monospace = false) {
	public static TextStyle Body { get; } = new();
	public static TextStyle Strong { get; } = new(13, Bold: true);
	public static TextStyle Heading { get; } = new(16, Bold: true);
	public static TextStyle Small { get; } = new(11);
	public static TextStyle Mono { get; } = new(13, Monospace: true);
}

// The palette a view should draw with, so service windows match the rest
// of the GUI in both light and dark themes.
public sealed record ViewTheme(
	Color Background,
	Color Surface,
	Color Border,
	Color Text,
	Color MutedText,
	Color Accent,
	Color Danger,
	Color Success) {

	public static ViewTheme Dark { get; } = new(
		Color.FromHex("#1E1E1E"),
		Color.FromHex("#252526"),
		Color.FromHex("#3F3F46"),
		Color.FromHex("#DCDCDC"),
		Color.FromHex("#9D9D9D"),
		Color.FromHex("#3794FF"),
		Color.FromHex("#F14C4C"),
		Color.FromHex("#4EC9B0"));

	public static ViewTheme Light { get; } = new(
		Color.FromHex("#FFFFFF"),
		Color.FromHex("#F3F3F3"),
		Color.FromHex("#CCCCCC"),
		Color.FromHex("#1E1E1E"),
		Color.FromHex("#6E6E6E"),
		Color.FromHex("#005FB8"),
		Color.FromHex("#C42B1C"),
		Color.FromHex("#0F7B0F"));
}
