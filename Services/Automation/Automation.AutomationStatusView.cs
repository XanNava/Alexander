using Alexander.Graphics;

namespace Alexander.Services.Automation;

// The Automation service's custom window, and the example to copy for
// other services: a live list of running folder services with a Stop
// button on each, drawn with the shared graphics library.
internal sealed class AutomationViewProvider(IServiceRegistry registry) : IServiceViewProvider {
	public const string StatusViewId = "service.status";

	public IReadOnlyList<ServiceViewInfo> Views { get; } = [
		new(StatusViewId, "Folder services", "Running folder-worker services, with stop buttons.")
	];

	public ServiceView CreateView(string id, IViewHost host) {
		return id == StatusViewId
			? new AutomationStatusView(registry, host)
			: throw new ArgumentException($"No Automation view named '{id}'.", nameof(id));
	}
}

internal sealed class AutomationStatusView(IServiceRegistry registry, IViewHost host)
	: ServiceView(host) {

	private const double RowHeight = 44;

	private readonly List<(string Name, ViewButton Stop)> stopButtons = [];
	private Point? pointer;

	public override string Title => "Folder services";

	// Services start and stop from commands and from other windows, so
	// repaint regularly rather than trying to observe every change.
	public override TimeSpan? RefreshInterval => TimeSpan.FromSeconds(1);

	public override void Render(IGraphics graphics) {
		var theme = graphics.Theme;
		graphics.Clear(theme.Background);

		var instances = registry.RunningInstances
			.OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		graphics.DrawText("Folder services", new Point(16, 12), theme.Text, TextStyle.Heading);
		graphics.DrawText($"{instances.Length} running", new Point(16, 36), theme.MutedText, TextStyle.Small);

		stopButtons.Clear();

		if (instances.Length == 0) {
			graphics.DrawText(
				"Nothing running. Use /service create or /service start.",
				new Point(16, 68),
				theme.MutedText);

			return;
		}

		var y = 60.0;

		foreach (var instance in instances) {
			if (y > graphics.Size.Height) {
				break;
			}

			var row = new Rect(12, y, Math.Max(0, graphics.Size.Width - 24), RowHeight - 6);
			graphics.FillRect(row, theme.Surface, 6);

			var running = instance.Status == ServiceStatus.Running;

			graphics.FillEllipse(
				new Rect(row.X + 12, row.Y + row.Height / 2 - 5, 10, 10),
				running ? theme.Success : theme.MutedText);

			var stop = new ViewButton("Stop") {
				Bounds = new Rect(row.Right - 72, row.Y + 7, 60, row.Height - 14),
				IsDanger = true,
				IsEnabled = running
			};

			var textWidth = stop.Bounds.X - (row.X + 32) - 12;

			graphics.DrawText(
				ViewText.Ellipsize(graphics, instance.Name, textWidth, TextStyle.Strong),
				new Point(row.X + 32, row.Y + 3),
				theme.Text,
				TextStyle.Strong);

			graphics.DrawText(
				ViewText.Ellipsize(
					graphics,
					$"{instance.TypeName} · {instance.Status} · {instance.Folder}",
					textWidth,
					TextStyle.Small),
				new Point(row.X + 32, row.Y + 21),
				theme.MutedText,
				TextStyle.Small);

			stop.Draw(graphics, pointer);
			stopButtons.Add((instance.Name, stop));

			y += RowHeight;
		}
	}

	public override void OnPointerMove(PointerEvent pointerEvent) {
		pointer = pointerEvent.Position;
		Invalidate();
	}

	public override void OnPointerLeave() {
		pointer = null;
		Invalidate();
	}

	public override void OnPointerDown(PointerEvent pointerEvent) {
		if (pointerEvent.Button != PointerButton.Left) {
			return;
		}

		foreach (var (name, stop) in stopButtons) {
			if (stop.HitTest(pointerEvent.Position)) {
				// Through the session rather than calling the registry
				// directly, so it's echoed and logged like a typed command.
				_ = Host.RunCommandAsync($"/service stop --name=\"{name}\"");
				return;
			}
		}
	}
}
